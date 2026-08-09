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
    /// <summary>Same job posting — this exact URL was bid before.</summary>
    public bool LinkDuplicate { get; set; }

    /// <summary>Different posting, same company + role — likely the same job re-listed.</summary>
    public bool DuplicateCompanyRole { get; set; }

    /// <summary>You already have an interview at this company.</summary>
    public bool CompanyInterviewWarning { get; set; }

    /// <summary>
    /// One line per warning, ready to display. Built here rather than in XAML because the two
    /// duplicate kinds mean different things and must not read alike: one is "you bid this exact
    /// posting already", the other is "this looks like the same job from a different listing".
    /// </summary>
    public string DuplicateLinkNote { get; set; } = "";
    public string DuplicateRoleNote { get; set; } = "";
    public string InterviewNote { get; set; } = "";

    /// <summary>True when anything at all should be shown in the warning column.</summary>
    public bool HasWarning => LinkDuplicate || DuplicateCompanyRole || CompanyInterviewWarning;

    /// <summary>
    /// Which warning leads when a row trips more than one: an exact repeat is the strongest
    /// signal, then a prior interview at the company, then a same-role match. A semantic token
    /// rather than an icon code — the view owns how each one looks.
    /// </summary>
    public string WarningKind =>
        LinkDuplicate ? "link"
        : CompanyInterviewWarning ? "interview"
        : DuplicateCompanyRole ? "role"
        : "";

    /// <summary>All applicable warnings, one per line, for the row tooltip.</summary>
    public string WarningTooltip =>
        string.Join(Environment.NewLine,
            new[] { DuplicateLinkNote, DuplicateRoleNote, InterviewNote }
                .Where(t => !string.IsNullOrEmpty(t)));

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
    private readonly ProfileContext _profileContext;

    public BidBoardService(MongoContext db, ProfileContext profileContext)
    {
        _db = db;
        _profileContext = profileContext;
    }

    /// <summary>
    /// Active profile id used to scope queries. <see cref="ObjectId.Empty"/> when no profile
    /// is loaded yet (very early startup); in that case all profile-filtered queries return
    /// nothing rather than risking cross-profile data leakage.
    /// </summary>
    private ObjectId ActiveProfileId => _profileContext.Current?.Id ?? ObjectId.Empty;

    /// <summary>
    /// Build the day's bid board: every link created today, plus every link whose bid was
    /// touched today. Duplicate / interview warnings are derived in memory against all links
    /// — fine for single-user scale (small N).
    /// </summary>
    public async Task<List<BoardRow>> BuildAsync(DateTime localFromUtc, DateTime localToUtc)
    {
        var profileId = ActiveProfileId;
        if (profileId == ObjectId.Empty) return new List<BoardRow>();

        var allLinks = await _db.Links.Find(l => l.ProfileId == profileId)
            .SortByDescending(l => l.CreatedAt)
            .ToListAsync();
        var allBids = await _db.Bids.Find(b => b.ProfileId == profileId).ToListAsync();
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

        // Interviews for company-warning detection — scoped to this profile.
        var interviewCompanies = await _db.Interviews
            .Find(Builders<Interview>.Filter.And(
                Builders<Interview>.Filter.Eq(i => i.ProfileId, profileId),
                Builders<Interview>.Filter.In(i => i.Status,
                    new[] { InterviewStatuses.Scheduled, InterviewStatuses.Completed, InterviewStatuses.Passed })))
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

            // --- Warning 1: the exact same posting, bid before. ---
            var linkDup = !string.IsNullOrEmpty(l.UrlNorm) && urlCount[l.UrlNorm] > 1;
            var linkNote = "";
            if (linkDup)
            {
                var others = allLinks.Where(x => x.UrlNorm == l.UrlNorm && x.Id != l.Id)
                                     .OrderBy(x => x.CreatedAt).ToList();
                var first = others.FirstOrDefault();
                linkNote = first != null
                    ? $"Same posting: you already bid this exact URL on {first.CreatedAt.ToLocalTime():MMM d}."
                    : "Same posting: this exact URL appears more than once.";
            }

            // --- Warning 2: a different posting for what looks like the same job. ---
            var crDup = false;
            var roleNote = "";
            if (!string.IsNullOrEmpty(c) && !string.IsNullOrEmpty(r))
            {
                var bucket = linksByCr.GetValueOrDefault(Key(c, r));
                crDup = bucket != null && bucket.Count > 1 && bucket[0].Id != l.Id;
                if (crDup)
                {
                    var firstCr = bucket!.OrderBy(x => x.CreatedAt).First();
                    roleNote = $"Same role: {bid?.Company} - {bid?.Role} was bid on " +
                               $"{firstCr.CreatedAt.ToLocalTime():MMM d} from a different listing.";
                }
            }

            // --- Warning 3: you are already interviewing at this company. ---
            var ivWarn = !string.IsNullOrEmpty(c) && interviewCompanySet.Contains(c);
            var ivNote = ivWarn
                ? $"Interview: you already have one scheduled or completed at {bid?.Company}."
                : "";

            rows.Add(new BoardRow
            {
                Link = l,
                Bid = bid,
                LinkDuplicate = linkDup,
                DuplicateCompanyRole = crDup,
                CompanyInterviewWarning = ivWarn,
                DuplicateLinkNote = linkNote,
                DuplicateRoleNote = roleNote,
                InterviewNote = ivNote
            });
        }

        return rows;
    }

    /// <summary>
    /// Look up an existing <see cref="GroupLink"/> by the strict-normalized URL form
    /// (query + hash preserved), scoped to the active profile so a peer with the same URL
    /// on a different profile doesn't collide. Returns null on miss.
    /// </summary>
    public Task<GroupLink?> FindLinkByNormalizedUrlAsync(string urlRaw)
    {
        var profileId = ActiveProfileId;
        if (profileId == ObjectId.Empty) return Task.FromResult<GroupLink?>(null);
        var norm = UrlNorm.Normalize(urlRaw);
        if (string.IsNullOrEmpty(norm)) return Task.FromResult<GroupLink?>(null);
        return _db.Links
            .Find(l => l.ProfileId == profileId && l.UrlNorm == norm)
            .FirstOrDefaultAsync()!;
    }

    /// <summary>Add a new link under the active profile.</summary>
    public async Task<GroupLink> AddLinkAsync(string urlRaw, string sharedJd = "")
    {
        var profileId = ActiveProfileId;
        if (profileId == ObjectId.Empty)
            throw new InvalidOperationException("No active profile — create one in the Profiles tab first.");

        var urlNorm = UrlNorm.Normalize(urlRaw);
        var link = new GroupLink
        {
            ProfileId = profileId,
            Url = urlRaw.Trim(),
            UrlNorm = urlNorm,
            SharedJobDescription = sharedJd ?? ""
        };
        await _db.Links.InsertOneAsync(link);
        return link;
    }

    public async Task<UserBid> UpsertBidAsync(ObjectId linkId, Action<UserBid> patch)
    {
        var profileId = ActiveProfileId;
        if (profileId == ObjectId.Empty)
            throw new InvalidOperationException("No active profile — create one in the Profiles tab first.");

        var bid = await _db.Bids.Find(b => b.GroupLinkId == linkId).FirstOrDefaultAsync();
        if (bid == null)
        {
            bid = new UserBid { GroupLinkId = linkId, ProfileId = profileId };
            patch(bid);
            StampLifecycle(bid, isNew: true);
            await _db.Bids.InsertOneAsync(bid);
            return bid;
        }
        var was = bid.Status;
        patch(bid);
        // Don't allow re-stamping ProfileId from a patch — bids stay under their original profile.
        if (bid.ProfileId == ObjectId.Empty) bid.ProfileId = profileId;
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

    public Task DeleteBidAsync(ObjectId bidId) =>
        _db.Bids.DeleteOneAsync(b => b.Id == bidId);

    /// <summary>Hard-delete a link (use after the bid is gone — interviews must be removed first).</summary>
    public Task DeleteLinkAsync(ObjectId linkId) =>
        _db.Links.DeleteOneAsync(l => l.Id == linkId);
}
