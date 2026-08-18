using CommunityToolkit.Mvvm.ComponentModel;
using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Bson;

namespace DevStrider.Desktop.Services;

/// <summary>
/// One row on the bid board: the captured posting and the bid against it — a single
/// <see cref="UserBid"/> — plus warning flags computed from cross-row state (duplicate URL,
/// duplicate company+role, prior interview at the company).
///
/// <para>
/// There used to be two objects here, a link and a bid. They were always one-to-one, and a link
/// with nothing bid against it is what <see cref="BidStatuses.Draft"/> already means — so the row
/// carries one.
/// </para>
///
/// Inherits <see cref="ObservableObject"/> so the per-row transient <see cref="FastFeedDraft"/>
/// can two-way-bind to a textbox. Persisted fields are set once at construction and don't need
/// property-changed notifications.
/// </summary>
public partial class BoardRow : ObservableObject
{
    public UserBid Bid { get; set; } = default!;

    /// <summary>Same job posting — this exact URL was captured before.</summary>
    public bool UrlDuplicate { get; set; }

    /// <summary>Different posting, same company + role — likely the same job re-listed.</summary>
    public bool DuplicateCompanyRole { get; set; }

    /// <summary>You already have an interview at this company.</summary>
    public bool CompanyInterviewWarning { get; set; }

    /// <summary>
    /// One line per warning, ready to display. Built here rather than in XAML because the two
    /// duplicate kinds mean different things and must not read alike: one is "you bid this exact
    /// posting already", the other is "this looks like the same job from a different listing".
    /// </summary>
    public string DuplicateUrlNote { get; set; } = "";
    public string DuplicateRoleNote { get; set; } = "";
    public string InterviewNote { get; set; } = "";

    /// <summary>True when anything at all should be shown in the warning column.</summary>
    public bool HasWarning => UrlDuplicate || DuplicateCompanyRole || CompanyInterviewWarning;

    /// <summary>
    /// Which warning leads when a row trips more than one: an exact repeat is the strongest
    /// signal, then a prior interview at the company, then a same-role match. A semantic token
    /// rather than an icon code — the view owns how each one looks.
    /// </summary>
    public string WarningKind =>
        UrlDuplicate ? "url"
        : CompanyInterviewWarning ? "interview"
        : DuplicateCompanyRole ? "role"
        : "";

    /// <summary>All applicable warnings, one per line, for the row tooltip.</summary>
    public string WarningTooltip =>
        string.Join(Environment.NewLine,
            new[] { DuplicateUrlNote, DuplicateRoleNote, InterviewNote }
                .Where(t => !string.IsNullOrEmpty(t)));

    /// <summary>
    /// Transient (view-only) buffer for a manually-typed fast-feed line of the form
    /// "UID, Company, Role, Stack1, Stack2, …". The Apply button calls
    /// <see cref="DevStrider.Desktop.ViewModels.BidBoardViewModel.ApplyFastFeedAsync"/> which
    /// parses this and writes the parsed fields onto the bid. Never persisted.
    /// </summary>
    [ObservableProperty] private string _fastFeedDraft = "";

    public string RowKey => Bid.Id.ToString();
}

public class BidBoardService
{
    private readonly IBidRepository _bids;
    private readonly IInterviewRepository _interviews;
    private readonly ProfileContext _profileContext;

    public BidBoardService(IBidRepository bids, IInterviewRepository interviews, ProfileContext profileContext)
    {
        _bids = bids;
        _interviews = interviews;
        _profileContext = profileContext;
    }

    /// <summary>
    /// Active profile id used to scope queries. <see cref="ObjectId.Empty"/> when no profile
    /// is loaded yet (very early startup); in that case all profile-filtered queries return
    /// nothing rather than risking cross-profile data leakage.
    /// </summary>
    private ObjectId ActiveProfileId => _profileContext.Current?.Id ?? ObjectId.Empty;

    /// <summary>
    /// Build the day's bid board: every posting captured in the window, plus every row touched in
    /// it. Duplicate / interview warnings are derived in memory against all of the profile's rows
    /// — fine at one person's scale (small N).
    /// </summary>
    public async Task<List<BoardRow>> BuildAsync(DateTime localFromUtc, DateTime localToUtc)
    {
        var profileId = ActiveProfileId;
        if (profileId == ObjectId.Empty) return new List<BoardRow>();

        var all = await _bids.ListByProfileAsync(profileId);

        // Day window: captured in range OR touched in range.
        var inWindow = all
            .Where(b => (b.CreatedAt >= localFromUtc && b.CreatedAt < localToUtc)
                     || (b.UpdatedAt >= localFromUtc && b.UpdatedAt < localToUtc))
            .OrderByDescending(b => b.CreatedAt)
            .ToList();

        // URL duplicate counts (strict — query/hash kept). Only flag when >1 row shares urlNorm.
        var urlCount = all
            .Where(b => !string.IsNullOrEmpty(b.UrlNorm))
            .GroupBy(b => b.UrlNorm)
            .ToDictionary(g => g.Key, g => g.Count());

        // company+role duplicate detection. The link's applied-company / applied-role snapshot is
        // gone with the merge, so these read straight off the row.
        static string Key(string c, string r) => $"{c.Trim().ToLowerInvariant()}::{r.Trim().ToLowerInvariant()}";
        var byCompanyRole = new Dictionary<string, List<UserBid>>();
        foreach (var b in all)
        {
            if (string.IsNullOrWhiteSpace(b.Company) || string.IsNullOrWhiteSpace(b.Role)) continue;
            var k = Key(b.Company, b.Role);
            if (!byCompanyRole.TryGetValue(k, out var bucket))
                byCompanyRole[k] = bucket = new List<UserBid>();
            bucket.Add(b);
        }
        foreach (var bucket in byCompanyRole.Values)
            bucket.Sort((x, y) => x.CreatedAt.CompareTo(y.CreatedAt));

        var interviewCompanies = await _interviews.ListCompaniesByProfileWithStatusAsync(profileId, new[]
        {
            InterviewStatuses.Scheduled,
            InterviewStatuses.Completed,
            InterviewStatuses.Passed,
        });
        var interviewCompanySet = new HashSet<string>(
            interviewCompanies.Where(s => !string.IsNullOrWhiteSpace(s))
                              .Select(s => s.Trim().ToLowerInvariant()));

        var rows = new List<BoardRow>(inWindow.Count);
        foreach (var bid in inWindow)
        {
            var c = (bid.Company ?? "").Trim().ToLowerInvariant();
            var r = (bid.Role ?? "").Trim().ToLowerInvariant();

            // --- Warning 1: the exact same posting, captured before. ---
            var urlDup = !string.IsNullOrEmpty(bid.UrlNorm) && urlCount[bid.UrlNorm] > 1;
            var urlNote = "";
            if (urlDup)
            {
                var first = all.Where(x => x.UrlNorm == bid.UrlNorm && x.Id != bid.Id)
                               .OrderBy(x => x.CreatedAt)
                               .FirstOrDefault();
                urlNote = first != null
                    ? $"Same posting: you already captured this exact URL on {first.CreatedAt.ToLocalTime():MMM d}."
                    : "Same posting: this exact URL appears more than once.";
            }

            // --- Warning 2: a different posting for what looks like the same job. ---
            var crDup = false;
            var roleNote = "";
            if (c.Length > 0 && r.Length > 0)
            {
                var bucket = byCompanyRole.GetValueOrDefault(Key(c, r));
                crDup = bucket != null && bucket.Count > 1 && bucket[0].Id != bid.Id;
                if (crDup)
                {
                    roleNote = $"Same role: {bid.Company} - {bid.Role} was bid on " +
                               $"{bucket![0].CreatedAt.ToLocalTime():MMM d} from a different listing.";
                }
            }

            // --- Warning 3: you are already interviewing at this company. ---
            var ivWarn = c.Length > 0 && interviewCompanySet.Contains(c);
            var ivNote = ivWarn
                ? $"Interview: you already have one scheduled or completed at {bid.Company}."
                : "";

            rows.Add(new BoardRow
            {
                Bid = bid,
                UrlDuplicate = urlDup,
                DuplicateCompanyRole = crDup,
                CompanyInterviewWarning = ivWarn,
                DuplicateUrlNote = urlNote,
                DuplicateRoleNote = roleNote,
                InterviewNote = ivNote
            });
        }

        return rows;
    }

    /// <summary>
    /// Look up a captured posting by the strict-normalized URL form (query + hash preserved),
    /// scoped to the active profile so the same URL under a different identity doesn't collide.
    /// Returns null on miss.
    /// </summary>
    public Task<UserBid?> FindByUrlAsync(string urlRaw)
    {
        var profileId = ActiveProfileId;
        if (profileId == ObjectId.Empty) return Task.FromResult<UserBid?>(null);
        var norm = UrlNorm.Normalize(urlRaw);
        if (string.IsNullOrEmpty(norm)) return Task.FromResult<UserBid?>(null);
        return _bids.FindByUrlNormAsync(profileId, norm);
    }

    /// <summary>
    /// Capture a URL under the active profile, or pick up the row that already holds it, then
    /// apply <paramref name="patch"/> and save. <c>joinedExisting</c> distinguishes "recorded" from
    /// "updated" for the caller's status message.
    ///
    /// <para>
    /// One method rather than find-then-create-then-update because every caller wants exactly this
    /// sequence, and splitting it is what let a capture race create two rows for one URL.
    /// </para>
    /// </summary>
    public async Task<(UserBid bid, bool joinedExisting)> CaptureAsync(
        string urlRaw, string jobDescription = "", Action<UserBid>? patch = null)
    {
        var profileId = ActiveProfileId;
        if (profileId == ObjectId.Empty)
            throw new InvalidOperationException("No active profile — create one in the Profiles tab first.");

        var existing = await FindByUrlAsync(urlRaw);
        var isNew = existing == null;

        var bid = existing ?? new UserBid
        {
            ProfileId = profileId,
            Url = (urlRaw ?? "").Trim(),
            UrlNorm = UrlNorm.Normalize(urlRaw),
            Status = BidStatuses.Draft,
        };

        // The JD is seeded on capture and never overwritten from here: a later capture of the same
        // posting carries whatever the page showed then, which is not necessarily better than what
        // is already stored, and may be empty.
        if (isNew && !string.IsNullOrWhiteSpace(jobDescription))
            bid.JobDescription = jobDescription;

        patch?.Invoke(bid);

        // Rows stay under the profile that captured them, whatever a patch says.
        if (bid.ProfileId == ObjectId.Empty) bid.ProfileId = profileId;

        StampLifecycle(bid, isNew);
        await _bids.UpsertAsync(bid);
        return (bid, !isNew);
    }

    /// <summary>Patch an existing row by id. No-op when the row is gone.</summary>
    public async Task<UserBid?> UpdateAsync(ObjectId bidId, Action<UserBid> patch)
    {
        var bid = await _bids.GetAsync(bidId);
        if (bid == null) return null;
        patch(bid);
        StampLifecycle(bid, isNew: false);
        await _bids.UpsertAsync(bid);
        return bid;
    }

    private static void StampLifecycle(UserBid bid, bool isNew)
    {
        var now = DateTime.UtcNow;
        if (isNew) bid.CreatedAt = now;
        bid.UpdatedAt = now;
        // Lock appliedAt the first time status moves off draft, and never move it again.
        if (bid.AppliedAt == null && !string.IsNullOrEmpty(bid.Status) && bid.Status != BidStatuses.Draft)
            bid.AppliedAt = now;
    }

    public Task DeleteAsync(ObjectId bidId) => _bids.DeleteAsync(bidId);
}
