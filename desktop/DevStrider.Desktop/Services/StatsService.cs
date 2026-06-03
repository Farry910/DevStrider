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
    private readonly ProfileContext _profileContext;
    public StatsService(MongoContext db, ProfileContext profileContext)
    {
        _db = db;
        _profileContext = profileContext;
    }

    private MongoDB.Bson.ObjectId ActiveProfileId => _profileContext.Current?.Id ?? MongoDB.Bson.ObjectId.Empty;

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

        var profileId = ActiveProfileId;
        if (includeOwners.Contains(selfOwner) && profileId != MongoDB.Bson.ObjectId.Empty)
        {
            var bids = await _db.Bids
                .Find(b => b.ProfileId == profileId && b.Status != BidStatuses.Draft)
                .ToListAsync();
            foreach (var b in bids)
            {
                var ts = (b.AppliedAt ?? b.FirstCreatedAt).ToLocalTime();
                if (b.AppliedAt == null && b.FirstCreatedAt == default) ts = b.CreatedAt.ToLocalTime();
                if (ts >= start && ts < end) Bump(selfOwner, ts);
            }
        }

        // Peer contribution comes straight from the local PeerBids mirror, filtered to
        // the owners the user wants overlaid on the chart.
        var peerBidFilter = Builders<PeerBid>.Filter.And(
            Builders<PeerBid>.Filter.In(b => b.OwnerUsername, includeOwners),
            Builders<PeerBid>.Filter.Ne(b => b.Status, BidStatuses.Draft));
        var peerBids = await _db.PeerBids.Find(peerBidFilter).ToListAsync();
        foreach (var b in peerBids)
        {
            var ts = (b.AppliedAt ?? b.FirstCreatedAt).ToLocalTime();
            if (b.AppliedAt == null && b.FirstCreatedAt == default) ts = b.CreatedAt.ToLocalTime();
            if (ts >= start && ts < end) Bump(b.OwnerUsername, ts);
        }

        return slots;
    }

    /// <summary>Overview table rows — self + each imported owner.</summary>
    public async Task<List<OverviewRow>> OverviewAsync(DateTime fromUtc, DateTime toUtc, string selfOwner)
    {
        var rows = new List<OverviewRow> { await BuildSelfAsync(fromUtc, toUtc, selfOwner) };

        // Peer rows are aggregated from the local PeerBids / PeerInterviews mirrors,
        // grouped by OwnerUsername. Counting "links" doesn't apply to peers (URLs aren't
        // shared), so we report 0 there.
        var peerBids = await _db.PeerBids
            .Find(b => b.OwnerUsername != selfOwner && b.UpdatedAt >= fromUtc && b.UpdatedAt < toUtc)
            .ToListAsync();
        var peerIvs = await _db.PeerInterviews
            .Find(i => i.OwnerUsername != selfOwner && i.ScheduledDate >= fromUtc && i.ScheduledDate < toUtc)
            .ToListAsync();
        var byOwner = peerBids.GroupBy(b => b.OwnerUsername);
        foreach (var grp in byOwner)
        {
            var ivsForOwner = peerIvs.Where(i => i.OwnerUsername == grp.Key).ToList();
            rows.Add(BuildPeerOverview(grp.Key, grp.ToList(), ivsForOwner));
        }
        // Owners that have interviews but no bids in window — still surface them.
        foreach (var grp in peerIvs.GroupBy(i => i.OwnerUsername))
        {
            if (rows.Any(r => r.Owner == grp.Key)) continue;
            rows.Add(BuildPeerOverview(grp.Key, new List<PeerBid>(), grp.ToList()));
        }
        return rows;
    }

    private async Task<OverviewRow> BuildSelfAsync(DateTime from, DateTime to, string selfOwner)
    {
        var profileId = ActiveProfileId;
        if (profileId == MongoDB.Bson.ObjectId.Empty)
            return new OverviewRow { Owner = selfOwner };

        var bids = await _db.Bids
            .Find(b => b.ProfileId == profileId && b.UpdatedAt >= from && b.UpdatedAt < to)
            .ToListAsync();
        var links = await _db.Links
            .Find(l => l.ProfileId == profileId && l.CreatedAt >= from && l.CreatedAt < to)
            .CountDocumentsAsync();
        var iv = await _db.Interviews
            .Find(i => i.ProfileId == profileId && i.ScheduledDate >= from && i.ScheduledDate < to)
            .ToListAsync();
        return Build(selfOwner, bids, (int)links, iv);
    }

    /// <summary>Peer overview row built from already-windowed PeerBid + PeerInterview lists.</summary>
    private static OverviewRow BuildPeerOverview(string owner, List<PeerBid> peerBids, List<PeerInterview> peerIvs)
    {
        var byStatus = peerBids.GroupBy(b => b.Status).ToDictionary(g => g.Key, g => g.Count());
        return new OverviewRow
        {
            Owner = owner,
            LinksCreated = 0, // peer URLs aren't shared
            ByStatus = byStatus,
            InterviewsInRange = peerIvs.Count,
            InterviewsPassed = peerIvs.Count(i => i.Status == InterviewStatuses.Passed),
            InterviewsFailed = peerIvs.Count(i => i.Status == InterviewStatuses.Failed),
        };
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
