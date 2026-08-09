using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;
using MongoDB.Driver;

namespace DevStrider.Desktop.ViewModels;

/// <summary>One row in the Peers tab — flat shape sourced from <see cref="PeerBid"/>.</summary>
public sealed class PeerBidRow
{
    public string Username { get; set; } = "";
    public string Profile { get; set; } = "";
    public string Company { get; set; } = "";
    public string Role { get; set; } = "";
    public string Status { get; set; } = "";
    public string Origin { get; set; } = "";
    public string ResumeId { get; set; } = "";
    public string Stacks { get; set; } = "";
    public DateTime? AppliedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class PeerInterviewRow
{
    public string Username { get; set; } = "";
    public string Profile { get; set; } = "";
    public DateTime? ScheduledDate { get; set; }
    public string ScheduledTime { get; set; } = "";
    public string InterviewType { get; set; } = "";
    public string Status { get; set; } = "";
    public string Company { get; set; } = "";
    public string Role { get; set; } = "";
    public string Recruiter { get; set; } = "";
    public string ResumeId { get; set; } = "";
}

public partial class PeersViewModel : ViewModelBase
{
    private readonly MongoContext _db;

    public ObservableCollection<PeerBidRow> Bids { get; } = new();
    public ObservableCollection<PeerInterviewRow> Interviews { get; } = new();

    /// <summary>
    /// Team members, from the mirrored <c>peerUsers</c> identities. Empty string = everyone.
    /// Sourced from published identities rather than scraped off bid rows, so a teammate who has
    /// set up but not yet bid still appears.
    /// </summary>
    public ObservableCollection<string> Users { get; } = new();

    /// <summary>Profiles belonging to <see cref="UserFilter"/>. Empty string = all of theirs.</summary>
    public ObservableCollection<string> ProfileNames { get; } = new();

    private DateTime _from = DateTime.Today.AddDays(-30);
    public DateTime From { get => _from; set { if (SetProperty(ref _from, value)) _ = LoadAsync(); } }

    private DateTime _to = DateTime.Today.AddDays(7);
    public DateTime To { get => _to; set { if (SetProperty(ref _to, value)) _ = LoadAsync(); } }

    /// <summary>Selected teammate; empty = all. Changing it rebuilds the profile list.</summary>
    private string _userFilter = "";
    public string UserFilter
    {
        get => _userFilter;
        set
        {
            if (!SetProperty(ref _userFilter, value)) return;
            // A profile name only means something within one user, so a user change invalidates it.
            _profileFilter = "";
            OnPropertyChanged(nameof(ProfileFilter));
            OnPropertyChanged(nameof(HasUserSelected));
            _ = LoadAsync();
        }
    }

    /// <summary>
    /// Gates the profile picker. A real bool rather than binding <see cref="UserFilter"/> straight
    /// at <c>IsEnabled</c> — a string source there fails conversion silently and leaves the
    /// control enabled, which is the bug in the pattern this view used to copy.
    /// </summary>
    public bool HasUserSelected => !string.IsNullOrEmpty(_userFilter);

    /// <summary>Selected profile within the chosen user; empty = all of that user's profiles.</summary>
    private string _profileFilter = "";
    public string ProfileFilter { get => _profileFilter; set { if (SetProperty(ref _profileFilter, value)) _ = LoadAsync(); } }

    public PeersViewModel(MongoContext db)
    {
        _db = db;
    }

    /// <summary>Mirrored identities keyed by their shared-database id — resolves owner_user_id on rows.</summary>
    private Dictionary<long, PeerUser> _usersById = new();

    /// <summary>
    /// Rebuild the two pickers. Identities come from the mirror; the profile list is narrowed to
    /// the selected user, which is what makes the pair behave as user → profile rather than one
    /// flat list of every "user / profile" pair on the team.
    /// </summary>
    private async Task RefreshPickersAsync()
    {
        var identities = await _db.PeerUsers.Find(FilterDefinition<PeerUser>.Empty).ToListAsync();
        // Rows carry only owner_user_id. Both the username and the profile name live on the
        // identity, so renaming either can't leave stale copies behind on historical bids.
        _usersById = identities.Where(u => u.RemoteId != 0).ToDictionary(u => u.RemoteId);

        var previousUser = UserFilter;
        Users.Clear();
        Users.Add("");                                   // "All members"
        // One row per (person, profile), so a person appears once per profile — collapse them.
        foreach (var name in identities.Select(u => u.Username)
                                       .Where(n => !string.IsNullOrWhiteSpace(n))
                                       .Distinct(StringComparer.OrdinalIgnoreCase)
                                       .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            Users.Add(name);
        }

        // A teammate can disappear; don't leave a stale filter selected.
        if (!string.IsNullOrEmpty(previousUser) && !Users.Contains(previousUser))
        {
            _userFilter = "";
            OnPropertyChanged(nameof(UserFilter));
            OnPropertyChanged(nameof(HasUserSelected));
        }

        ProfileNames.Clear();
        ProfileNames.Add("");                            // "All profiles"
        if (!string.IsNullOrEmpty(UserFilter))
        {
            foreach (var name in identities
                        .Where(u => string.Equals(u.Username, UserFilter, StringComparison.OrdinalIgnoreCase))
                        .Select(u => u.ProfileName)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            ProfileNames.Add(name);
        }
        }
        if (!string.IsNullOrEmpty(ProfileFilter) && !ProfileNames.Contains(ProfileFilter))
        {
            _profileFilter = "";
            OnPropertyChanged(nameof(ProfileFilter));
        }
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            await RefreshPickersAsync();

            var fromUtc = From.Date.ToUniversalTime();
            var toUtc = To.Date.AddDays(1).ToUniversalTime();

            // -------- bids --------
            var bidFilter = Builders<PeerBid>.Filter.And(
                Builders<PeerBid>.Filter.Gte(b => b.UpdatedAt, fromUtc),
                Builders<PeerBid>.Filter.Lt(b => b.UpdatedAt, toUtc));
            var bids = await _db.PeerBids
                .Find(bidFilter)
                .SortByDescending(b => b.UpdatedAt)
                .ToListAsync();

            // -------- interviews --------
            var ivFilter = Builders<PeerInterview>.Filter.Or(
                Builders<PeerInterview>.Filter.Eq(i => i.ScheduledDate, null),
                Builders<PeerInterview>.Filter.And(
                    Builders<PeerInterview>.Filter.Gte(i => i.ScheduledDate, fromUtc),
                    Builders<PeerInterview>.Filter.Lt(i => i.ScheduledDate, toUtc)));
            var ivs = await _db.PeerInterviews
                .Find(ivFilter)
                .SortBy(i => i.ScheduledDate)
                .ToListAsync();

            // Two-level filter, applied post-query: the mirror is small, and matching in memory
            // avoids building a compound Mongo filter for what is at most a few thousand rows.
            // Profile is only consulted once a user is chosen — the same profile name under two
            // different teammates is two different things.
            string UsernameOf(long id) =>
                _usersById.TryGetValue(id, out var u) ? u.Username : "(unknown)";
            string ProfileOf(long id) =>
                _usersById.TryGetValue(id, out var u) ? u.ProfileName : "";

            bool MatchesOwner(string user, string profileName) =>
                (string.IsNullOrEmpty(UserFilter) ||
                 string.Equals(UserFilter, user, StringComparison.OrdinalIgnoreCase))
                &&
                (string.IsNullOrEmpty(ProfileFilter) ||
                 string.Equals(ProfileFilter, profileName, StringComparison.OrdinalIgnoreCase));

            Bids.Clear();
            foreach (var b in bids)
            {
                var bidOwner = UsernameOf(b.OwnerUserId);
                var bidProfile = ProfileOf(b.OwnerUserId);
                if (!MatchesOwner(bidOwner, bidProfile)) continue;
                Bids.Add(new PeerBidRow
                {
                    Username = bidOwner,
                    Profile = bidProfile,
                    Company = b.Company,
                    Role = b.Role,
                    Status = b.Status,
                    Origin = b.Origin,
                    ResumeId = b.ResumeId,
                    Stacks = string.Join(", ", b.PrimaryStacks ?? new()),
                    AppliedAt = b.AppliedAt,
                    UpdatedAt = b.UpdatedAt
                });
            }

            Interviews.Clear();
            foreach (var i in ivs)
            {
                var ivOwner = UsernameOf(i.OwnerUserId);
                var ivProfile = ProfileOf(i.OwnerUserId);
                if (!MatchesOwner(ivOwner, ivProfile)) continue;
                Interviews.Add(new PeerInterviewRow
                {
                    Username = ivOwner,
                    Profile = ivProfile,
                    ScheduledDate = i.ScheduledDate,
                    ScheduledTime = i.ScheduledTime,
                    InterviewType = i.InterviewType,
                    Status = i.Status,
                    Company = i.Company,
                    Role = i.Role,
                    Recruiter = i.Recruiter,
                    ResumeId = i.ResumeId
                });
            }

            var scope = string.IsNullOrEmpty(UserFilter)
                ? "across all team members"
                : string.IsNullOrEmpty(ProfileFilter)
                    ? $"for {UserFilter}"
                    : $"for {UserFilter} / {ProfileFilter}";
            StatusMessage = $"{Bids.Count} bids, {Interviews.Count} interviews {scope}. " +
                            "Peer data refreshes on the sync schedule, or from Sharing → Sync now.";
        }
        finally { IsBusy = false; }
    }
}
