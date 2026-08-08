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

    /// <summary>
    /// One sync at a time. Now that a background scheduler fires this hourly, a manual
    /// <b>Sync now</b> can land on top of a scheduled run — and two concurrent passes share the
    /// same <see cref="AppSettings.LastSyncAt"/> marker, so whichever finished second would
    /// advance it past rows the other hadn't pushed yet. Second caller is turned away rather
    /// than queued: it has nothing to add that the in-flight run isn't already doing.
    /// </summary>
    private readonly SemaphoreSlim _syncLock = new(1, 1);

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
        // WaitAsync(0) → don't block, just report that one is already running.
        if (!await _syncLock.WaitAsync(0))
        {
            const string busy = "A sync is already running — skipped.";
            _activity.Info("Atlas", "Sync skipped", busy, silent: true);
            return busy;
        }
        try
        {
            return await SyncCoreAsync();
        }
        finally { _syncLock.Release(); }
    }

    private async Task<string> SyncCoreAsync()
    {
        if (!await _atlas.IsConfiguredAsync())
        {
            const string msg = "Shared cluster isn't configured — set the password in Settings → Peer database.";
            _activity.Warning("Atlas", "Sync skipped", msg);
            return msg;
        }

        var settings = await _settings.GetForEditAsync();
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
            var atlasUsers = await _atlas.PeerUsersAsync();

            // ===== Publish who we are ============================================
            // Unconditional, not delta-gated: it's one small upsert, and it's what makes this
            // install visible to teammates before it has pushed a single bid. Matched on username
            // so a reinstall updates the existing identity instead of forking a second one.
            var me = new PeerUser
            {
                Username = owner,
                Email = (userProfile.PersonalEmail ?? "").Trim(),
                DisplayName = string.IsNullOrWhiteSpace(userProfile.DisplayName) ? owner : userProfile.DisplayName.Trim(),
                Profiles = profiles.Values
                    .Select(p => new PeerUserProfile { Slug = p.Slug(), Name = p.Name ?? "" })
                    .OrderBy(p => p.Name)
                    .ToList(),
                UpdatedAt = DateTime.UtcNow
            };
            var existingMe = await atlasUsers.Find(u => u.Username == owner).FirstOrDefaultAsync();
            if (existingMe != null) me.Id = existingMe.Id;   // keep the row's identity stable
            await atlasUsers.ReplaceOneAsync(
                Builders<PeerUser>.Filter.Eq(u => u.Username, owner),
                me,
                new ReplaceOptions { IsUpsert = true });

            // ===== Pull everyone else's identities into the local mirror ==========
            // Full replace rather than a delta: the set is tiny (one row per teammate) and this
            // way a profile someone renamed or removed doesn't linger in our picker.
            var allUsers = await atlasUsers.Find(FilterDefinition<PeerUser>.Empty).ToListAsync();
            foreach (var u in allUsers)
            {
                await _local.PeerUsers.ReplaceOneAsync(
                    Builders<PeerUser>.Filter.Eq(x => x.Username, u.Username),
                    u,
                    new ReplaceOptions { IsUpsert = true });
            }

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
                         $"pulled {pulledBids} bids / {pulledIvs} interviews · " +
                         $"{allUsers.Count} team member{(allUsers.Count == 1 ? "" : "s")}.";
            _activity.Success("Atlas", "Sync complete", status);
            return status;
        }
        catch (Exception ex)
        {
            // Driver exceptions echo the connection string, password included — the Activity
            // feed is user-visible and gets screenshotted, so redact before it lands there.
            _activity.Error("Atlas", "Sync failed", SharedMongoCredentials.Redact(ex.Message));
            return "Sync failed — see Activity for details.";
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
