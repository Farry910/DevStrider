using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;
using MongoDB.Bson;
using MongoDB.Driver;

namespace DevStrider.Desktop.ViewModels;

/// <summary>One flat row pulled from a peer's snapshot payload (dedup-by-latest applied).</summary>
public sealed class PeerBidRow
{
    public string Username { get; set; } = "";
    public string Profile { get; set; } = "";
    public string DayKey { get; set; } = "";
    public DateTime ExportedAt { get; set; }

    public string Company { get; set; } = "";
    public string Role { get; set; } = "";
    public string Status { get; set; } = "";
    public string Origin { get; set; } = "";
    public string ResumeId { get; set; } = "";
    public string Url { get; set; } = "";
    public string Stacks { get; set; } = "";
    public DateTime? AppliedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class PeerInterviewRow
{
    public string Username { get; set; } = "";
    public string Profile { get; set; } = "";
    public string DayKey { get; set; } = "";

    public DateTime? ScheduledDate { get; set; }
    public string ScheduledTime { get; set; } = "";
    public string InterviewType { get; set; } = "";
    public string Status { get; set; } = "";
    public string Company { get; set; } = "";
    public string Role { get; set; } = "";
    public string Recruiter { get; set; } = "";
    public string MeetingLink { get; set; } = "";
    public string ResumeId { get; set; } = "";
}

public partial class PeersViewModel : ViewModelBase
{
    private readonly MongoContext _db;
    private readonly ProfileContext _profileContext;

    public ObservableCollection<PeerBidRow> Bids { get; } = new();
    public ObservableCollection<PeerInterviewRow> Interviews { get; } = new();
    /// <summary>Owners present in this profile's imported snapshots — feeds the filter combo.</summary>
    public ObservableCollection<string> Owners { get; } = new();

    private DateTime _from = DateTime.Today.AddDays(-30);
    public DateTime From { get => _from; set { if (SetProperty(ref _from, value)) _ = LoadAsync(); } }

    private DateTime _to = DateTime.Today.AddDays(7);
    public DateTime To { get => _to; set { if (SetProperty(ref _to, value)) _ = LoadAsync(); } }

    /// <summary>Empty string = all owners.</summary>
    private string _ownerFilter = "";
    public string OwnerFilter { get => _ownerFilter; set { if (SetProperty(ref _ownerFilter, value)) _ = LoadAsync(); } }

    public PeersViewModel(MongoContext db, ProfileContext profileContext)
    {
        _db = db;
        _profileContext = profileContext;
        profileContext.ProfileChanged += () =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                new Action(async () => { try { await LoadAsync(); } catch { /* ignore */ } }));
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var profileId = _profileContext.Current?.Id ?? ObjectId.Empty;
            if (profileId == ObjectId.Empty)
            {
                Bids.Clear(); Interviews.Clear(); Owners.Clear();
                StatusMessage = "No active profile.";
                return;
            }

            // Snapshots are scoped to the active profile (which profile *we* imported them into).
            // Sorted newest-first so the dedupe loop keeps the latest copy of each (owner, bid).
            var snaps = await _db.ImportedSnapshots
                .Find(s => s.ProfileId == profileId)
                .SortByDescending(s => s.ExportedAt)
                .ToListAsync();

            var fromUtc = From.Date.ToUniversalTime();
            var toUtc = To.Date.AddDays(1).ToUniversalTime();

            var seenBids = new HashSet<(string owner, ObjectId id)>();
            var seenIvs = new HashSet<(string owner, ObjectId id)>();
            var bidRows = new List<PeerBidRow>();
            var ivRows = new List<PeerInterviewRow>();
            var ownerSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var snap in snaps)
            {
                SnapshotPayload? payload;
                try { payload = JsonSerializer.Deserialize<SnapshotPayload>(snap.PayloadJson); }
                catch { continue; }
                if (payload == null) continue;

                var (user, profile) = SplitOwner(snap.Owner);
                var ownerLabel = $"{user} / {profile}".Trim(' ', '/');
                ownerSet.Add(ownerLabel);
                if (!string.IsNullOrEmpty(OwnerFilter) && !string.Equals(OwnerFilter, ownerLabel, StringComparison.OrdinalIgnoreCase))
                    continue;

                var links = payload.Links?.ToDictionary(l => l.Id) ?? new Dictionary<ObjectId, GroupLink>();

                foreach (var b in payload.Bids ?? new())
                {
                    if (!seenBids.Add((snap.Owner, b.Id))) continue;            // older copy — skip
                    if (b.UpdatedAt < fromUtc || b.UpdatedAt >= toUtc) continue; // out of window
                    links.TryGetValue(b.GroupLinkId, out var link);
                    bidRows.Add(new PeerBidRow
                    {
                        Username = user, Profile = profile,
                        DayKey = snap.DayKey, ExportedAt = snap.ExportedAt,
                        Company = b.Company, Role = b.Role, Status = b.Status,
                        Origin = b.Origin, ResumeId = b.ResumeId,
                        Url = link?.Url ?? "",
                        Stacks = string.Join(", ", b.PrimaryStacks ?? new()),
                        AppliedAt = b.AppliedAt, UpdatedAt = b.UpdatedAt
                    });
                }

                foreach (var iv in payload.Interviews ?? new())
                {
                    if (!seenIvs.Add((snap.Owner, iv.Id))) continue;
                    if (iv.ScheduledDate.HasValue &&
                        (iv.ScheduledDate.Value < fromUtc || iv.ScheduledDate.Value >= toUtc))
                        continue;
                    ivRows.Add(new PeerInterviewRow
                    {
                        Username = user, Profile = profile, DayKey = snap.DayKey,
                        ScheduledDate = iv.ScheduledDate, ScheduledTime = iv.ScheduledTime,
                        InterviewType = iv.InterviewType, Status = iv.Status,
                        Company = iv.Company, Role = iv.Role,
                        Recruiter = iv.Recruiter, MeetingLink = iv.MeetingLink,
                        ResumeId = iv.ResumeId
                    });
                }
            }

            Bids.Clear();
            foreach (var b in bidRows.OrderByDescending(b => b.UpdatedAt)) Bids.Add(b);
            Interviews.Clear();
            foreach (var i in ivRows.OrderBy(i => i.ScheduledDate)) Interviews.Add(i);

            // Refresh the owner-filter dropdown, preserving the current selection if still valid.
            var currentFilter = OwnerFilter;
            Owners.Clear();
            Owners.Add(""); // "All"
            foreach (var o in ownerSet.OrderBy(o => o, StringComparer.OrdinalIgnoreCase)) Owners.Add(o);
            if (!string.IsNullOrEmpty(currentFilter) && !Owners.Contains(currentFilter)) OwnerFilter = "";

            StatusMessage = $"{Bids.Count} peer bids, {Interviews.Count} peer interviews across {snaps.Count} snapshot(s).";
        }
        finally { IsBusy = false; }
    }

    /// <summary>Parse filename slug like "alice__Fernando-Garcia" → ("alice", "Fernando Garcia").</summary>
    private static (string user, string profile) SplitOwner(string raw)
    {
        var parts = (raw ?? "").Split("__", 2);
        if (parts.Length == 2) return (parts[0], parts[1].Replace('-', ' '));
        return (raw ?? "", "");
    }
}
