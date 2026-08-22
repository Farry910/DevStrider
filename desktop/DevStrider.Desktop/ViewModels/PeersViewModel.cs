using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

/// <summary>One row in the Peers tab — a teammate's <see cref="UserBid"/>, flattened.</summary>
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

    /// <summary>
    /// R2 object key for the resume this peer attached, or empty. Carried on the row so the
    /// Download button binds directly — the file is fetched from R2 with your own credentials,
    /// so nothing about the peer's setup is needed to read it.
    /// </summary>
    public string ResumeObjectKey { get; set; } = "";
    public string ResumeFileName { get; set; } = "";
}

/// <summary>
/// The team's work, read live.
///
/// <para>
/// This used to read a local mirror that a background service pulled down every hour. There is
/// nothing left to mirror: a teammate's bid is a row in the same table as yours with a different
/// account on it, so what shows here is what they saved, when they saved it.
/// </para>
/// </summary>
public partial class PeersViewModel : ViewModelBase
{
    private readonly IPeerDirectory _peers;
    private readonly R2StorageService _storage;

    public ObservableCollection<PeerBidRow> Bids { get; } = new();
    public ObservableCollection<PeerInterviewRow> Interviews { get; } = new();

    /// <summary>
    /// Team members. Empty string = everyone. Sourced from the identity join rather than scraped
    /// off bid rows, so a teammate who has set up but not yet bid still appears.
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

    public PeersViewModel(IPeerDirectory peers, R2StorageService storage)
    {
        _peers = peers;
        _storage = storage;
    }

    /// <summary>
    /// Download a teammate's interview resume from R2 and open it.
    ///
    /// <para>
    /// Only the object key travels through the shared database; the bytes stay in R2 and are
    /// fetched with <i>your</i> credentials. So this needs nothing from the peer beyond the key
    /// they published — and it works even if they are offline.
    /// </para>
    /// </summary>
    [RelayCommand]
    public async Task OpenPeerResumeAsync(object? param)
    {
        if (param is not PeerInterviewRow row) return;
        if (string.IsNullOrWhiteSpace(row.ResumeObjectKey))
        {
            StatusMessage = "That interview has no resume attached.";
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = $"Downloading {row.ResumeFileName}…";
            var (path, message) = await _storage.DownloadToTempAsync(row.ResumeObjectKey, row.ResumeFileName);
            StatusMessage = message;
            if (path is null) return;

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex) { StatusMessage = $"Couldn't open the file: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    /// <summary>Every (person, profile) pair on the team, from the last load.</summary>
    private List<PeerIdentity> _identities = new();

    /// <summary>
    /// Rebuild the two pickers. The profile list is narrowed to the selected user, which is what
    /// makes the pair behave as user → profile rather than one flat list of every "user / profile"
    /// pair on the team.
    /// </summary>
    private void RefreshPickers()
    {
        var previousUser = UserFilter;
        Users.Clear();
        Users.Add("");                                   // "All members"
        // One identity per (person, profile), so a person appears once per profile — collapse them.
        foreach (var name in _identities.Select(i => i.Username)
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
            foreach (var name in _identities
                        .Where(i => string.Equals(i.Username, UserFilter, StringComparison.OrdinalIgnoreCase))
                        .Select(i => i.ProfileName)
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
            _identities = await _peers.ListIdentitiesAsync();
            RefreshPickers();

            var fromUtc = From.Date.ToUniversalTime();
            var toUtc = To.Date.AddDays(1).ToUniversalTime();

            var bids = await _peers.ListBidsUpdatedBetweenAsync(fromUtc, toUtc);
            var ivs = await _peers.ListInterviewsScheduledBetweenAsync(fromUtc, toUtc, includeUndated: true);

            // Rows carry a user id and a profile id, never names — both labels come from the
            // identity join, so renaming either can't leave a stale copy on historical rows.
            var nameByUser = new Dictionary<long, string>();
            var profileById = new Dictionary<ObjectId, string>();
            foreach (var i in _identities)
            {
                if (i.UserId != 0 && !string.IsNullOrWhiteSpace(i.Username)) nameByUser[i.UserId] = i.Username;
                if (i.ProfileId != ObjectId.Empty) profileById[i.ProfileId] = i.ProfileName;
            }

            string UsernameOf(long id) => nameByUser.TryGetValue(id, out var u) ? u : "(unknown)";
            string ProfileOf(ObjectId id) => profileById.TryGetValue(id, out var p) ? p : "";

            // Two-level filter, applied post-query: the result set is small, and matching in
            // memory avoids a compound query for what is at most a few thousand rows. Profile is
            // only consulted once a user is chosen — the same profile name under two different
            // teammates is two different things.
            bool MatchesOwner(string user, string profileName) =>
                (string.IsNullOrEmpty(UserFilter) ||
                 string.Equals(UserFilter, user, StringComparison.OrdinalIgnoreCase))
                &&
                (string.IsNullOrEmpty(ProfileFilter) ||
                 string.Equals(ProfileFilter, profileName, StringComparison.OrdinalIgnoreCase));

            Bids.Clear();
            foreach (var b in bids)
            {
                var owner = UsernameOf(b.UserId);
                var profile = ProfileOf(b.ProfileId);
                if (!MatchesOwner(owner, profile)) continue;
                Bids.Add(new PeerBidRow
                {
                    Username = owner,
                    Profile = profile,
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
                var owner = UsernameOf(i.UserId);
                var profile = ProfileOf(i.ProfileId);
                if (!MatchesOwner(owner, profile)) continue;
                Interviews.Add(new PeerInterviewRow
                {
                    Username = owner,
                    Profile = profile,
                    ScheduledDate = i.ScheduledDate,
                    ScheduledTime = i.ScheduledTime,
                    InterviewType = i.InterviewType,
                    Status = i.Status,
                    Company = i.Company,
                    Role = i.Role,
                    Recruiter = i.Recruiter,
                    ResumeId = i.ResumeId,
                    ResumeObjectKey = i.ResumeObjectKey,
                    ResumeFileName = i.ResumeFileName
                });
            }

            var scope = string.IsNullOrEmpty(UserFilter)
                ? "across all team members"
                : string.IsNullOrEmpty(ProfileFilter)
                    ? $"for {UserFilter}"
                    : $"for {UserFilter} / {ProfileFilter}";
            StatusMessage = $"{Bids.Count} bids, {Interviews.Count} interviews {scope}.";
        }
        finally { IsBusy = false; }
    }
}
