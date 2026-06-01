using CommunityToolkit.Mvvm.ComponentModel;
using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace DevStrider.Desktop.Services;

/// <summary>
/// One row on the bid board: the link, the user's bid (if any), and warning flags computed from
/// cross-row state (duplicate URL, duplicate company+role, prior-interview-at-company).
///
/// Inherits <see cref="ObservableObject"/> so the per-row transient <see cref="FastFeedDraft"/>
/// can two-way-bind to a textbox. Persisted fields (Link / Bid / flags) are set once at
/// construction and don't need property-changed notifications.
/// </summary>
public partial class BoardRow : ObservableObject
{
    public GroupLink Link { get; set; } = default!;
    public UserBid? Bid { get; set; }
    public bool LinkDuplicate { get; set; }
    public bool DuplicateCompanyRole { get; set; }
    public bool CompanyInterviewWarning { get; set; }

    /// <summary>
    /// Transient (view-only) buffer for a manually-typed fast-feed line of the form
    /// "UID, Company, Role, Stack1, Stack2, …". The Apply button calls
    /// <see cref="DevStrider.Desktop.ViewModels.BidBoardViewModel.ApplyFastFeedAsync"/> which
    /// parses this and writes the parsed fields onto the bid. Never persisted to Mongo.
    /// </summary>
    [ObservableProperty] private string _fastFeedDraft = "";

    public string RowKey =>
        Bid != null
            ? $"{Link.Id}-{Bid.Id}"
            : Link.Id.ToString();
}

public class BidBoardService
{
    private readonly MongoContext _db;
    public BidBoardService(MongoContext db) => _db = db;

    /// <summary>
    /// Build the day's bid board: every link created today, plus every link whose bid was
    /// touched today. Duplicate / interview warnings are derived in memory against all links
    /// — fine for single-user scale (small N).
    /// </summary>
    public async Task<List<BoardRow>> BuildAsync(DateTime localFromUtc, DateTime localToUtc)
    {
        var allLinks = await _db.Links.Find(FilterDefinition<GroupLink>.Empty)
            .SortByDescending(l => l.CreatedAt)
            .ToListAsync();
        var allBids = await _db.Bids.Find(FilterDefinition<UserBid>.Empty).ToListAsync();
        var bidByLink = allBids.ToDictionary(b => b.GroupLinkId);

        // Day window: link created in range OR bid updated in range.
        var dayLinks = allLinks
            .Where(l => (l.CreatedAt >= localFromUtc && l.CreatedAt < localToUtc)
                || (bidByLink.TryGetValue(l.Id, out var b)
                    && b.UpdatedAt >= localFromUtc && b.UpdatedAt < localToUtc))
            .ToList();

        // URL duplicate counts (strict — query/hash kept). Only flag when >1 row shares urlNorm.
        var urlCount = allLinks.GroupBy(l => l.UrlNorm)
            .ToDictionary(g => g.Key, g => g.Count());

        // company+role duplicate detection using each link's applied snapshot.
        string Key(string c, string r) => $"{c.Trim().ToLowerInvariant()}::{r.Trim().ToLowerInvariant()}";
        var linksByCr = new Dictionary<string, List<GroupLink>>();
        foreach (var l in allLinks)
        {
            var c = !string.IsNullOrWhiteSpace(l.AppliedCompany) ? l.AppliedCompany
                  : bidByLink.TryGetValue(l.Id, out var b) ? b.Company : "";
            var r = !string.IsNullOrWhiteSpace(l.AppliedRole) ? l.AppliedRole
                  : bidByLink.TryGetValue(l.Id, out var b2) ? b2.Role : "";
            if (string.IsNullOrWhiteSpace(c) || string.IsNullOrWhiteSpace(r)) continue;
            var k = Key(c, r);
            if (!linksByCr.TryGetValue(k, out var bucket))
                linksByCr[k] = bucket = new List<GroupLink>();
            bucket.Add(l);
        }

        // Interviews for company-warning detection
        var interviewCompanies = await _db.Interviews
            .Find(Builders<Interview>.Filter.In(i => i.Status,
                new[] { InterviewStatuses.Scheduled, InterviewStatuses.Completed, InterviewStatuses.Passed }))
            .Project(i => i.Company)
            .ToListAsync();
        var interviewCompanySet = new HashSet<string>(
            interviewCompanies.Where(s => !string.IsNullOrWhiteSpace(s))
                              .Select(s => s.Trim().ToLowerInvariant()));

        var rows = new List<BoardRow>(dayLinks.Count);
        foreach (var l in dayLinks)
        {
            bidByLink.TryGetValue(l.Id, out var bid);

            var c = (bid?.Company ?? "").Trim().ToLowerInvariant();
            var r = (bid?.Role ?? "").Trim().ToLowerInvariant();

            var linkDup = !string.IsNullOrEmpty(l.UrlNorm) && urlCount[l.UrlNorm] > 1;
            var crDup = false;
            if (!string.IsNullOrEmpty(c) && !string.IsNullOrEmpty(r))
            {
                var bucket = linksByCr.GetValueOrDefault(Key(c, r));
                crDup = bucket != null && bucket.Count > 1 && bucket[0].Id != l.Id;
            }
            var ivWarn = !string.IsNullOrEmpty(c) && interviewCompanySet.Contains(c);

            rows.Add(new BoardRow
            {
                Link = l,
                Bid = bid,
                LinkDuplicate = linkDup,
                DuplicateCompanyRole = crDup,
                CompanyInterviewWarning = ivWarn
            });
        }

        return rows;
    }

    /// <summary>
    /// Look up an existing <see cref="GroupLink"/> by the strict-normalized URL form
    /// (query + hash preserved). Returns null on miss. Used by the Bid-Assistant listener
    /// to decide whether a new POST should join an existing link or create one.
    /// </summary>
    public Task<GroupLink?> FindLinkByNormalizedUrlAsync(string urlRaw)
    {
        var norm = UrlNorm.Normalize(urlRaw);
        if (string.IsNullOrEmpty(norm)) return Task.FromResult<GroupLink?>(null);
        return _db.Links.Find(l => l.UrlNorm == norm).FirstOrDefaultAsync()!;
    }

    /// <summary>Add a new link (rejects same-norm duplicates in the same day window).</summary>
    public async Task<GroupLink> AddLinkAsync(string urlRaw, string sharedJd = "")
    {
        var urlNorm = UrlNorm.Normalize(urlRaw);
        var link = new GroupLink
        {
            Url = urlRaw.Trim(),
            UrlNorm = urlNorm,
            SharedJobDescription = sharedJd ?? ""
        };
        await _db.Links.InsertOneAsync(link);
        return link;
    }

    public async Task<UserBid> UpsertBidAsync(ObjectId linkId, Action<UserBid> patch)
    {
        var bid = await _db.Bids.Find(b => b.GroupLinkId == linkId).FirstOrDefaultAsync();
        if (bid == null)
        {
            bid = new UserBid { GroupLinkId = linkId };
            patch(bid);
            StampLifecycle(bid, isNew: true);
            await _db.Bids.InsertOneAsync(bid);
            return bid;
        }
        var was = bid.Status;
        patch(bid);
        StampLifecycle(bid, isNew: false, wasStatus: was);
        await _db.Bids.ReplaceOneAsync(b => b.Id == bid.Id, bid);
        return bid;
    }

    private static void StampLifecycle(UserBid bid, bool isNew, string? wasStatus = null)
    {
        var now = DateTime.UtcNow;
        if (isNew) bid.FirstCreatedAt = now;
        bid.UpdatedAt = now;
        // Lock appliedAt the first time status moves off draft, never move it again.
        if (bid.AppliedAt == null && !string.IsNullOrEmpty(bid.Status) && bid.Status != BidStatuses.Draft)
        {
            bid.AppliedAt = isNew ? bid.FirstCreatedAt : now;
        }
        _ = wasStatus;
    }

    /// <summary>
    /// Mark a link useless. Local-mode: every link belongs to "you", and you can purge it
    /// immediately as long as no interview is attached to its bid.
    /// </summary>
    public async Task<bool> SetUselessAsync(ObjectId linkId, bool useless)
    {
        var link = await _db.Links.Find(l => l.Id == linkId).FirstOrDefaultAsync();
        if (link == null) return false;

        if (useless)
        {
            link.MarkedUselessAt = DateTime.UtcNow;
            await _db.Links.ReplaceOneAsync(l => l.Id == linkId, link);

            var bids = await _db.Bids.Find(b => b.GroupLinkId == linkId).ToListAsync();
            var bidIds = bids.Select(b => b.Id).ToList();
            var hasInterview = bidIds.Count > 0 &&
                await _db.Interviews.Find(Builders<Interview>.Filter.In(i => i.BidId, bidIds))
                    .AnyAsync();
            if (!hasInterview)
            {
                await _db.Bids.DeleteManyAsync(b => b.GroupLinkId == linkId);
                await _db.Links.DeleteOneAsync(l => l.Id == linkId);
                return true;
            }
            return false;
        }

        link.MarkedUselessAt = null;
        await _db.Links.ReplaceOneAsync(l => l.Id == linkId, link);
        return false;
    }

    public Task DeleteBidAsync(ObjectId bidId) =>
        _db.Bids.DeleteOneAsync(b => b.Id == bidId);
}
