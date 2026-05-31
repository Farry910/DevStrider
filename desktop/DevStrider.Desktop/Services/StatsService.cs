using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Driver;

namespace DevStrider.Desktop.Services;

/// <summary>One 10-minute slot for the bids-per-10-min chart.</summary>
public class HourlySlot
{
    public string Label { get; set; } = "";  // "HH:MM"
    public int Index { get; set; }            // 0..143
    public Dictionary<string, int> CountsByOwner { get; set; } = new();
}

/// <summary>Per-owner counts for the Overview table — sum across status buckets.</summary>
public class OverviewRow
{
    public string Owner { get; set; } = "";
    public int LinksCreated { get; set; }
    public Dictionary<string, int> ByStatus { get; set; } = new();
    public int InterviewsInRange { get; set; }
    public int InterviewsPassed { get; set; }
    public int InterviewsFailed { get; set; }

    /// <summary>"Applied" column = sum of all non-draft status counts.</summary>
    public int AppliedCount =>
        ByStatus.Where(kv => kv.Key != BidStatuses.Draft).Sum(kv => kv.Value);
}

public class StatsService
{
    private readonly MongoContext _db;
    public StatsService(MongoContext db) => _db = db;

    /// <summary>
    /// Bids per 10-minute slot for one local date across the current user and any imported
    /// snapshots whose owner is in <paramref name="includeOwners"/>. Bucket index uses local
    /// hour+minute of the bid's <c>AppliedAt</c> (fallback: FirstCreatedAt → CreatedAt).
    /// </summary>
    public async Task<List<HourlySlot>> BidsPer10MinAsync(
        DateOnly date,
        HashSet<string> includeOwners,
        string selfOwner)
    {
        // Local-day [start, end) in local tz.
        var start = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
        var end = start.AddDays(1);

        var slots = new List<HourlySlot>(144);
        for (int i = 0; i < 144; i++)
        {
            var h = i / 6;
            var m = (i % 6) * 10;
            slots.Add(new HourlySlot
            {
                Index = i,
                Label = $"{h:D2}:{m:D2}"
            });
        }

        void Bump(string owner, DateTime localTs)
        {
            var idx = localTs.Hour * 6 + localTs.Minute / 10;
            if (idx < 0 || idx >= 144) return;
            var slot = slots[idx];
            slot.CountsByOwner[owner] = slot.CountsByOwner.GetValueOrDefault(owner) + 1;
        }

        if (includeOwners.Contains(selfOwner))
        {
            var bids = await _db.Bids.Find(b => b.Status != BidStatuses.Draft).ToListAsync();
            foreach (var b in bids)
            {
                var ts = (b.AppliedAt ?? b.FirstCreatedAt).ToLocalTime();
                if (b.AppliedAt == null && b.FirstCreatedAt == default) ts = b.CreatedAt.ToLocalTime();
                if (ts >= start && ts < end) Bump(selfOwner, ts);
            }
        }

        // Each imported snapshot contributes its own bids in the same date window.
        var snapshots = await _db.ImportedSnapshots
            .Find(s => includeOwners.Contains(s.Owner))
            .ToListAsync();
        foreach (var snap in snapshots)
        {
            try
            {
                var payload = System.Text.Json.JsonSerializer.Deserialize<SnapshotPayload>(snap.PayloadJson);
                if (payload?.Bids == null) continue;
                foreach (var b in payload.Bids.Where(b => b.Status != BidStatuses.Draft))
                {
                    var ts = (b.AppliedAt ?? b.FirstCreatedAt).ToLocalTime();
                    if (ts >= start && ts < end) Bump(snap.Owner, ts);
                }
            }
            catch { /* skip malformed snapshot */ }
        }

        return slots;
    }

    /// <summary>Overview table rows — self + each imported owner.</summary>
    public async Task<List<OverviewRow>> OverviewAsync(DateTime fromUtc, DateTime toUtc, string selfOwner)
    {
        var rows = new List<OverviewRow> { await BuildSelfAsync(fromUtc, toUtc, selfOwner) };

        var snaps = await _db.ImportedSnapshots.Find(FilterDefinition<ImportedSnapshot>.Empty).ToListAsync();
        foreach (var snap in snaps)
        {
            try
            {
                var payload = System.Text.Json.JsonSerializer.Deserialize<SnapshotPayload>(snap.PayloadJson);
                if (payload == null) continue;
                rows.Add(BuildPeer(snap.Owner, payload, fromUtc, toUtc));
            }
            catch { /* skip */ }
        }
        return rows;
    }

    private async Task<OverviewRow> BuildSelfAsync(DateTime from, DateTime to, string selfOwner)
    {
        var bids = await _db.Bids
            .Find(b => b.UpdatedAt >= from && b.UpdatedAt < to)
            .ToListAsync();
        var links = await _db.Links
            .Find(l => l.CreatedAt >= from && l.CreatedAt < to)
            .CountDocumentsAsync();
        var iv = await _db.Interviews
            .Find(i => i.ScheduledDate >= from && i.ScheduledDate < to)
            .ToListAsync();
        return Build(selfOwner, bids, (int)links, iv);
    }

    private OverviewRow BuildPeer(string owner, SnapshotPayload payload, DateTime from, DateTime to)
    {
        var bids = payload.Bids.Where(b => b.UpdatedAt >= from && b.UpdatedAt < to).ToList();
        var links = payload.Links.Count(l => l.CreatedAt >= from && l.CreatedAt < to);
        var iv = payload.Interviews.Where(i => i.ScheduledDate >= from && i.ScheduledDate < to).ToList();
        return Build(owner, bids, links, iv);
    }

    private static OverviewRow Build(string owner, List<UserBid> bids, int linksCreated, List<Interview> iv)
    {
        var byStatus = bids.GroupBy(b => b.Status).ToDictionary(g => g.Key, g => g.Count());
        return new OverviewRow
        {
            Owner = owner,
            LinksCreated = linksCreated,
            ByStatus = byStatus,
            InterviewsInRange = iv.Count,
            InterviewsPassed = iv.Count(i => i.Status == InterviewStatuses.Passed),
            InterviewsFailed = iv.Count(i => i.Status == InterviewStatuses.Failed),
        };
    }
}
