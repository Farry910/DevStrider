using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Driver;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Two-way delta sync between local data and the shared Atlas cluster:
///   • Push: my bids/interviews updated since <see cref="AppSettings.LastSyncAt"/> are
///     projected into <see cref="PeerBid"/> / <see cref="PeerInterview"/> shapes (private
///     fields stripped) and upserted by <c>_id</c>.
///   • Pull: rows in Atlas updated since the same marker, authored by someone else, are
///     upserted into the local mirror collections.
///
/// <para>
/// Idempotent — running it twice in quick succession does the same work the first time
/// would have done. <see cref="AppSettings.LastSyncAt"/> only advances on full success.
/// </para>
/// </summary>
public sealed class AtlasSyncService
{
    private readonly MongoContext _local;
    private readonly AtlasContext _atlas;
    private readonly SettingsService _settings;
    private readonly ProfileService _localProfile;
    private readonly ProfilesService _profiles;
    private readonly ActivityLogService _activity;

    public AtlasSyncService(
        MongoContext local,
        AtlasContext atlas,
        SettingsService settings,
        ProfileService localProfile,
        ProfilesService profiles,
        ActivityLogService activity)
    {
        _local = local;
        _atlas = atlas;
        _settings = settings;
        _localProfile = localProfile;
        _profiles = profiles;
        _activity = activity;
    }

    /// <summary>
    /// Run a full push + pull cycle. Returns a short status string for the UI; throws
    /// nothing — failures are logged to the Activity feed.
    /// </summary>
    public async Task<string> SyncAsync()
    {
        if (!await _atlas.IsConfiguredAsync())
        {
            const string msg = "Shared MongoDB URI isn't configured — set it in Settings.";
            _activity.Warning("Atlas", "Sync skipped", msg);
            return msg;
        }

        var settings = await _settings.GetAsync();
        var userProfile = await _localProfile.GetAsync();
        var owner = (userProfile.Username ?? "").Trim();
        if (string.IsNullOrEmpty(owner))
        {
            const string msg = "Username isn't set — Settings → Identity.";
            _activity.Warning("Atlas", "Sync skipped", msg);
            return msg;
        }

        // Profile lookup so we can stamp owner-profile metadata onto pushed rows.
        var profiles = (await _profiles.ListAsync()).ToDictionary(p => p.Id);

        var lastSync = settings.LastSyncAt;
        var newSyncMark = DateTime.UtcNow;
        int pushedBids = 0, pushedIvs = 0, pulledBids = 0, pulledIvs = 0;

        try
        {
            var atlasBids = await _atlas.PeerBidsAsync();
            var atlasIvs = await _atlas.PeerInterviewsAsync();

            // ===== Push: my updated bids / interviews → Atlas ===================
            var myUpdatedBids = await _local.Bids
                .Find(b => b.UpdatedAt > lastSync)
                .ToListAsync();
            foreach (var b in myUpdatedBids)
            {
                if (!profiles.TryGetValue(b.ProfileId, out var prof)) continue;
                var peer = ToPeerBid(b, owner, prof);
                await atlasBids.ReplaceOneAsync(
                    Builders<PeerBid>.Filter.Eq(p => p.Id, peer.Id),
                    peer,
                    new ReplaceOptions { IsUpsert = true });
                pushedBids++;
            }

            var myUpdatedIvs = await _local.Interviews
                .Find(i => i.UpdatedAt > lastSync)
                .ToListAsync();
            foreach (var iv in myUpdatedIvs)
            {
                if (!profiles.TryGetValue(iv.ProfileId, out var prof)) continue;
                var peer = ToPeerInterview(iv, owner, prof);
                await atlasIvs.ReplaceOneAsync(
                    Builders<PeerInterview>.Filter.Eq(p => p.Id, peer.Id),
                    peer,
                    new ReplaceOptions { IsUpsert = true });
                pushedIvs++;
            }

            // ===== Pull: peers' updated rows → local mirror =====================
            var peerBidsCursor = await atlasBids
                .Find(b => b.UpdatedAt > lastSync && b.OwnerUsername != owner)
                .ToListAsync();
            foreach (var b in peerBidsCursor)
            {
                await _local.PeerBids.ReplaceOneAsync(
                    Builders<PeerBid>.Filter.Eq(p => p.Id, b.Id),
                    b,
                    new ReplaceOptions { IsUpsert = true });
                pulledBids++;
            }

            var peerIvsCursor = await atlasIvs
                .Find(i => i.UpdatedAt > lastSync && i.OwnerUsername != owner)
                .ToListAsync();
            foreach (var iv in peerIvsCursor)
            {
                await _local.PeerInterviews.ReplaceOneAsync(
                    Builders<PeerInterview>.Filter.Eq(p => p.Id, iv.Id),
                    iv,
                    new ReplaceOptions { IsUpsert = true });
                pulledIvs++;
            }

            // Advance the marker only on full success.
            settings.LastSyncAt = newSyncMark;
            await _settings.SaveAsync(settings);

            var status = $"Pushed {pushedBids} bids / {pushedIvs} interviews · " +
                         $"pulled {pulledBids} bids / {pulledIvs} interviews.";
            _activity.Success("Atlas", "Sync complete", status);
            return status;
        }
        catch (Exception ex)
        {
            _activity.Error("Atlas", "Sync failed", ex.Message);
            return $"Sync failed: {ex.Message}";
        }
    }

    /// <summary>Project a local <see cref="UserBid"/> into the shared shape (URL/JD/etc stripped).</summary>
    private static PeerBid ToPeerBid(UserBid b, string ownerUsername, Profile prof) => new()
    {
        Id = b.Id,
        OwnerUsername = ownerUsername,
        OwnerProfileSlug = prof.Slug(),
        OwnerProfileName = prof.Name,
        Company = b.Company ?? "",
        Role = b.Role ?? "",
        Status = b.Status ?? "",
        Origin = b.Origin ?? "",
        ResumeId = b.ResumeId ?? "",
        PrimaryStacks = b.PrimaryStacks?.ToList() ?? new(),
        CreatedAt = b.CreatedAt,
        UpdatedAt = b.UpdatedAt,
        FirstCreatedAt = b.FirstCreatedAt,
        AppliedAt = b.AppliedAt
    };

    private static PeerInterview ToPeerInterview(Interview iv, string ownerUsername, Profile prof) => new()
    {
        Id = iv.Id,
        OwnerUsername = ownerUsername,
        OwnerProfileSlug = prof.Slug(),
        OwnerProfileName = prof.Name,
        Company = iv.Company ?? "",
        Role = iv.Role ?? "",
        InterviewType = iv.InterviewType ?? "",
        Status = iv.Status ?? "",
        Recruiter = iv.Recruiter ?? "",
        ResumeId = iv.ResumeId ?? "",
        ScheduledDate = iv.ScheduledDate,
        ScheduledTime = iv.ScheduledTime ?? "",
        DurationMinutes = iv.DurationMinutes,
        CreatedAt = iv.CreatedAt,
        UpdatedAt = iv.UpdatedAt
    };
}
