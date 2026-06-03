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
    /// <summary>Owners present in local peer data — feeds the filter combo. Empty string = All.</summary>
    public ObservableCollection<string> Owners { get; } = new();

    private DateTime _from = DateTime.Today.AddDays(-30);
    public DateTime From { get => _from; set { if (SetProperty(ref _from, value)) _ = LoadAsync(); } }

    private DateTime _to = DateTime.Today.AddDays(7);
    public DateTime To { get => _to; set { if (SetProperty(ref _to, value)) _ = LoadAsync(); } }

    /// <summary>Empty string = all owners.</summary>
    private string _ownerFilter = "";
    public string OwnerFilter { get => _ownerFilter; set { if (SetProperty(ref _ownerFilter, value)) _ = LoadAsync(); } }

    public PeersViewModel(MongoContext db)
    {
        _db = db;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
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

            // Owner label = "username / Profile Name". Build the set across both collections.
            string OwnerLabel(string user, string profile) => $"{user} / {profile}".Trim(' ', '/');
            var ownerSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in bids) ownerSet.Add(OwnerLabel(b.OwnerUsername, b.OwnerProfileName));
            foreach (var i in ivs)  ownerSet.Add(OwnerLabel(i.OwnerUsername, i.OwnerProfileName));

            // Apply owner filter (post-query — small N, simpler than building a Mongo Or).
            bool MatchesOwner(string label) =>
                string.IsNullOrEmpty(OwnerFilter) ||
                string.Equals(OwnerFilter, label, StringComparison.OrdinalIgnoreCase);

            Bids.Clear();
            foreach (var b in bids)
            {
                var label = OwnerLabel(b.OwnerUsername, b.OwnerProfileName);
                if (!MatchesOwner(label)) continue;
                Bids.Add(new PeerBidRow
                {
                    Username = b.OwnerUsername,
                    Profile = b.OwnerProfileName,
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
                var label = OwnerLabel(i.OwnerUsername, i.OwnerProfileName);
                if (!MatchesOwner(label)) continue;
                Interviews.Add(new PeerInterviewRow
                {
                    Username = i.OwnerUsername,
                    Profile = i.OwnerProfileName,
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

            var currentFilter = OwnerFilter;
            Owners.Clear();
            Owners.Add("");  // "All"
            foreach (var o in ownerSet.OrderBy(o => o, StringComparer.OrdinalIgnoreCase)) Owners.Add(o);
            if (!string.IsNullOrEmpty(currentFilter) && !Owners.Contains(currentFilter)) OwnerFilter = "";

            StatusMessage = $"{Bids.Count} peer bids, {Interviews.Count} peer interviews. " +
                            "Click Sync on the Sharing tab to pull the latest.";
        }
        finally { IsBusy = false; }
    }
}
