using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Bson;

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

    /// <summary>
    /// Postings captured in the window, bid on or not. Used to be "links created"; with the
    /// link/bid merge it is simply rows whose <see cref="UserBid.CreatedAt"/> falls in range.
    /// </summary>
    public int Captured { get; set; }

    public Dictionary<string, int> ByStatus { get; set; } = new();
    public int InterviewsInRange { get; set; }
    public int InterviewsPassed { get; set; }
    public int InterviewsFailed { get; set; }

    /// <summary>"Applied" column = sum of all non-draft status counts.</summary>
    public int AppliedCount =>
        ByStatus.Where(kv => kv.Key != BidStatuses.Draft).Sum(kv => kv.Value);
}

/// <summary>
/// The numbers behind the Overview table and the bids-per-10-min chart.
///
/// <para>
/// Your rows come from the scoped repositories; everyone else's come from
/// <see cref="IPeerDirectory"/>, which reads the same tables with the account filter inverted.
/// There is no mirror and no sync lag any more — a teammate's bid is visible the moment they
/// save it.
/// </para>
/// </summary>
public class StatsService
{
    private readonly IBidRepository _bids;
    private readonly IInterviewRepository _interviews;
    private readonly IPeerDirectory _peers;
    private readonly ProfileContext _profileContext;

    public StatsService(
        IBidRepository bids,
        IInterviewRepository interviews,
        IPeerDirectory peers,
        ProfileContext profileContext)
    {
        _bids = bids;
        _interviews = interviews;
        _peers = peers;
        _profileContext = profileContext;
    }

    private ObjectId ActiveProfileId => _profileContext.Current?.Id ?? ObjectId.Empty;

    /// <summary>
    /// Bids per 10-minute slot for one local date, across you and any teammate in
    /// <paramref name="includeOwners"/>. Bucket index uses the local hour+minute of the bid's
    /// <c>AppliedAt</c> (fallback: <c>CreatedAt</c>).
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
        if (includeOwners.Contains(selfOwner) && profileId != ObjectId.Empty)
        {
            foreach (var b in await _bids.ListNonDraftByProfileAsync(profileId))
            {
                var ts = (b.AppliedAt ?? b.CreatedAt).ToLocalTime();
                if (ts >= start && ts < end) Bump(selfOwner, ts);
            }
        }

        // Peer rows carry a user id and a profile id, never a name — every label comes from the
        // identity join, so a rename can't split one person's history across two lines.
        var identities = await _peers.ListIdentitiesAsync();
        var nameByUser = NameByUser(identities);
        var wantedProfiles = identities
            .Where(i => includeOwners.Contains(NameOf(nameByUser, i.UserId)))
            .Select(i => i.ProfileId)
            .Distinct()
            .ToList();

        if (wantedProfiles.Count > 0)
        {
            foreach (var b in await _peers.ListNonDraftBidsByProfilesAsync(wantedProfiles))
            {
                var ts = (b.AppliedAt ?? b.CreatedAt).ToLocalTime();
                if (ts >= start && ts < end) Bump(NameOf(nameByUser, b.UserId), ts);
            }
        }

        return slots;
    }

    /// <summary>Overview table rows — you, then every teammate with activity in the window.</summary>
    public async Task<List<OverviewRow>> OverviewAsync(DateTime fromUtc, DateTime toUtc, string selfOwner)
    {
        var rows = new List<OverviewRow> { await BuildSelfAsync(fromUtc, toUtc, selfOwner) };

        var identities = await _peers.ListIdentitiesAsync();
        var nameByUser = NameByUser(identities);

        var peerBids = await _peers.ListBidsUpdatedBetweenAsync(fromUtc, toUtc);
        var peerIvs = await _peers.ListInterviewsScheduledBetweenAsync(fromUtc, toUtc, includeUndated: false);

        foreach (var grp in peerBids.GroupBy(b => b.UserId))
        {
            var ivsForOwner = peerIvs.Where(i => i.UserId == grp.Key).ToList();
            rows.Add(Build(NameOf(nameByUser, grp.Key), grp.ToList(), fromUtc, toUtc, ivsForOwner));
        }

        // Owners with interviews but no bids in the window — still surface them.
        foreach (var grp in peerIvs.GroupBy(i => i.UserId))
        {
            var owner = NameOf(nameByUser, grp.Key);
            if (rows.Any(r => r.Owner == owner)) continue;
            rows.Add(Build(owner, new List<UserBid>(), fromUtc, toUtc, grp.ToList()));
        }

        return rows;
    }

    private async Task<OverviewRow> BuildSelfAsync(DateTime from, DateTime to, string selfOwner)
    {
        var profileId = ActiveProfileId;
        if (profileId == ObjectId.Empty) return new OverviewRow { Owner = selfOwner };

        var bids = await _bids.ListByProfileUpdatedBetweenAsync(profileId, from, to);
        var iv = await _interviews.ListByProfileScheduledBetweenAsync(profileId, from, to);
        return Build(selfOwner, bids, from, to, iv);
    }

    /// <summary>
    /// One row from an already-windowed set. <paramref name="from"/>/<paramref name="to"/> are
    /// still needed: the bids are those <i>touched</i> in the window, and captures are the subset
    /// first seen inside it.
    /// </summary>
    private static OverviewRow Build(
        string owner, List<UserBid> bids, DateTime from, DateTime to, List<Interview> iv)
    {
        var byStatus = bids.GroupBy(b => b.Status).ToDictionary(g => g.Key, g => g.Count());
        return new OverviewRow
        {
            Owner = owner,
            Captured = bids.Count(b => b.CreatedAt >= from && b.CreatedAt < to),
            ByStatus = byStatus,
            InterviewsInRange = iv.Count,
            InterviewsPassed = iv.Count(i => i.Status == InterviewStatuses.Passed),
            InterviewsFailed = iv.Count(i => i.Status == InterviewStatuses.Failed),
        };
    }

    /// <summary>
    /// One name per person. Identities are (person, profile) pairs, so a teammate with three
    /// profiles appears three times — they all carry the same username, which is the label.
    /// </summary>
    private static Dictionary<long, string> NameByUser(IEnumerable<PeerIdentity> identities)
    {
        var map = new Dictionary<long, string>();
        foreach (var i in identities)
        {
            if (i.UserId == 0 || string.IsNullOrWhiteSpace(i.Username)) continue;
            map[i.UserId] = i.Username;
        }
        return map;
    }

    /// <summary>Label for an owner id, falling back to something visible rather than blank.</summary>
    private static string NameOf(Dictionary<long, string> nameById, long id) =>
        nameById.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? n : $"user #{id}";
}
