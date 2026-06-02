using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;
using MongoDB.Driver;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// "When the recruiter calls about a job you applied to weeks ago" — search across the user's
/// bids (and the link URLs they came from) by company, role, stack, or substring of the URL.
/// Each result has a Schedule-interview action that creates an Interview prefilled with
/// resumeId + JD from the bid.
/// </summary>
public partial class FindBidViewModel : ViewModelBase
{
    private readonly MongoContext _db;
    private readonly InterviewService _interviews;

    public ObservableCollection<FindBidRow> Results { get; } = new();

    private string _query = "";
    /// <summary>Live search box value — typing reloads automatically.</summary>
    public string Query
    {
        get => _query;
        set { if (SetProperty(ref _query, value)) _ = SearchAsync(); }
    }

    /// <summary>
    /// How many days back the search reaches. Defaults to 60 (the 2-month floor); changing it
    /// re-runs the search so the user can widen on demand. Stored in memory only.
    /// </summary>
    private int _searchDaysBack = 60;
    public int SearchDaysBack
    {
        get => _searchDaysBack;
        set { if (SetProperty(ref _searchDaysBack, Math.Max(7, value))) _ = SearchAsync(); }
    }

    private readonly ProfileContext _profileContext;

    public FindBidViewModel(MongoContext db, InterviewService interviews, ProfileContext profileContext)
    {
        _db = db;
        _interviews = interviews;
        _profileContext = profileContext;
        profileContext.ProfileChanged += () =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                new Action(async () => { try { await SearchAsync(); } catch { /* ignore */ } }));
    }

    [RelayCommand]
    public async Task LoadAsync() => await SearchAsync();

    [RelayCommand]
    public async Task SearchAsync()
    {
        IsBusy = true;
        try
        {
            var q = (Query ?? "").Trim();
            var profileId = _profileContext.Current?.Id ?? MongoDB.Bson.ObjectId.Empty;
            if (profileId == MongoDB.Bson.ObjectId.Empty)
            {
                Results.Clear();
                StatusMessage = "No active profile.";
                return;
            }
            // Search window: at least the last 2 months, ordered newest-first. No count cap —
            // even a year of heavy bidding (~3-4k rows) is trivial to filter client-side.
            // Tuneable via the SearchDaysBack property on this VM.
            var cutoff = DateTime.UtcNow.AddDays(-SearchDaysBack);
            var bids = await _db.Bids.Find(b => b.ProfileId == profileId && b.UpdatedAt >= cutoff)
                                     .SortByDescending(b => b.UpdatedAt)
                                     .ToListAsync();
            var links = await _db.Links.Find(l => l.ProfileId == profileId).ToListAsync();
            var linksById = links.ToDictionary(l => l.Id);

            Results.Clear();
            foreach (var b in bids)
            {
                if (!linksById.TryGetValue(b.GroupLinkId, out var link)) continue;
                if (q.Length > 0 && !Matches(b, link, q)) continue;
                Results.Add(new FindBidRow { Bid = b, Link = link });
            }
            var window = $"last {SearchDaysBack} day{(SearchDaysBack == 1 ? "" : "s")}";
            StatusMessage = q.Length == 0
                ? $"{Results.Count} bid{(Results.Count == 1 ? "" : "s")} in the {window} — type to search."
                : $"{Results.Count} match{(Results.Count == 1 ? "" : "es")} for \"{q}\" in the {window}.";
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Case-insensitive substring match across company, role, primary stacks, URL, and the
    /// link's shared JD. URL is included so e.g. "hopper" matches
    /// <c>linkedin.com/jobs/.../senior-backend-engineer-at-hopper-…</c>.
    /// </summary>
    private static bool Matches(UserBid bid, GroupLink link, string q)
    {
        var needle = q.ToLowerInvariant();
        bool hasHit(string? s) => s != null && s.Contains(needle, StringComparison.OrdinalIgnoreCase);

        if (hasHit(bid.Company)) return true;
        if (hasHit(bid.Role)) return true;
        if (hasHit(bid.ResumeId)) return true;
        if (bid.PrimaryStacks.Any(s => hasHit(s))) return true;
        if (hasHit(link.Url)) return true;
        if (hasHit(link.SharedJobDescription)) return true;
        if (hasHit(bid.JobDescription)) return true;
        return false;
    }

    /// <summary>Create the interview, carrying the bid's resumeId + JD onto the new row.</summary>
    public async Task ScheduleAsync(FindBidRow row, DateTime? date, string time,
                                    string interviewType, string recruiter, string meetingLink)
    {
        if (row?.Bid == null) return;
        var jd = (row.Bid.JobDescription ?? "").Trim();
        if (jd.Length == 0) jd = (row.Link?.SharedJobDescription ?? "").Trim();

        await _interviews.CreateAsync(new Interview
        {
            BidId = row.Bid.Id,
            ScheduledDate = date,
            ScheduledTime = time,
            InterviewType = string.IsNullOrWhiteSpace(interviewType) ? InterviewTypes.Interview : interviewType,
            Recruiter = recruiter,
            MeetingLink = meetingLink,
            Company = row.Bid.Company,
            Role = row.Bid.Role,
            ResumeId = row.Bid.ResumeId,
            AttachedJobDescription = jd,
            Status = InterviewStatuses.Scheduled,
            Origin = "FindBid"
        });
        StatusMessage = $"Interview scheduled for {row.Bid.Company} · {row.Bid.Role}.";
    }
}

public class FindBidRow
{
    public UserBid Bid { get; set; } = default!;
    public GroupLink Link { get; set; } = default!;
    public string Url => Link?.Url ?? "";
}
