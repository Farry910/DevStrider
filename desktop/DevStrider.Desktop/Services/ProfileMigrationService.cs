using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace DevStrider.Desktop.Services;

/// <summary>
/// First-launch migration that introduces the multi-profile model. Idempotent — runs every
/// startup, no-ops once the seed profile exists and all rows are stamped.
///
/// <para>Steps:</para>
/// <list type="number">
///   <item>If no profiles exist, create one named after <see cref="UserProfile.Username"/>
///         (or "Default") and copy the legacy <see cref="AppSettings.WordDocPath"/> into it.</item>
///   <item>Set <see cref="AppSettings.ActiveProfileId"/> to a valid profile if it's unset or
///         points to a deleted profile.</item>
///   <item>Backfill <c>ProfileId</c> on any Link / Bid / Interview with
///         <see cref="ObjectId.Empty"/> — they all belong to the seed profile.</item>
/// </list>
/// </summary>
public sealed class ProfileMigrationService
{
    private readonly MongoContext _db;
    private readonly ProfilesService _profilesService;
    private readonly ProfileService _userProfile;
    private readonly SettingsService _settings;
    private readonly ActivityLogService _activity;

    public ProfileMigrationService(
        MongoContext db,
        ProfilesService profilesService,
        ProfileService userProfile,
        SettingsService settings,
        ActivityLogService activity)
    {
        _db = db;
        _profilesService = profilesService;
        _userProfile = userProfile;
        _settings = settings;
        _activity = activity;
    }

    public async Task RunAsync()
    {
        try
        {
            var profiles = await _profilesService.ListAsync();
            var settings = await _settings.GetForEditAsync();
            var settingsDirty = false;

            // 1) Seed a default profile if there are none.
            Profile? seed = null;
            if (profiles.Count == 0)
            {
                var userProfile = await _userProfile.GetAsync();
                var seedName = !string.IsNullOrWhiteSpace(userProfile.Username) &&
                                !string.Equals(userProfile.Username, "me", StringComparison.OrdinalIgnoreCase)
                    ? userProfile.Username
                    : "Default";

                seed = await _profilesService.CreateAsync(seedName, settings.WordDocPath ?? "");
                profiles.Add(seed);
                _activity.Info("Profiles", "Created default profile", $"'{seed.Name}' — migrated from single-profile setup.", silent: true);
            }

            // 2) Ensure ActiveProfileId points to a real profile.
            var currentActive = profiles.FirstOrDefault(p => p.Id == settings.ActiveProfileId);
            if (currentActive == null)
            {
                settings.ActiveProfileId = profiles[0].Id;
                settingsDirty = true;
            }

            // 3) Backfill ProfileId on legacy rows. Each collection: find rows with empty
            //    ProfileId, $set to the seed (or current active) profile in one update.
            var backfillTarget = currentActive?.Id ?? profiles[0].Id;
            var emptyFilter = Builders<GroupLink>.Filter.Eq(l => l.ProfileId, ObjectId.Empty);
            var linkResult = await _db.Links.UpdateManyAsync(
                emptyFilter,
                Builders<GroupLink>.Update.Set(l => l.ProfileId, backfillTarget));

            var bidResult = await _db.Bids.UpdateManyAsync(
                Builders<UserBid>.Filter.Eq(b => b.ProfileId, ObjectId.Empty),
                Builders<UserBid>.Update.Set(b => b.ProfileId, backfillTarget));

            var ivResult = await _db.Interviews.UpdateManyAsync(
                Builders<Interview>.Filter.Eq(i => i.ProfileId, ObjectId.Empty),
                Builders<Interview>.Update.Set(i => i.ProfileId, backfillTarget));

            var totalBackfilled =
                linkResult.ModifiedCount + bidResult.ModifiedCount + ivResult.ModifiedCount;

            if (totalBackfilled > 0)
            {
                _activity.Info("Profiles", "Backfilled legacy data",
                    $"{linkResult.ModifiedCount} links, {bidResult.ModifiedCount} bids, " +
                    $"{ivResult.ModifiedCount} interviews → " +
                    $"profile '{profiles.First(p => p.Id == backfillTarget).Name}'.",
                    silent: true);
            }

            if (settingsDirty) await _settings.SaveAsync(settings);

            await BackfillInterviewProcessesAsync();
        }
        catch (Exception ex)
        {
            _activity.Error("Profiles", "Migration failed", ex.Message);
        }
    }
    /// <summary>
    /// Give every pre-existing interview a <see cref="Interview.ProcessId"/> so old pipelines
    /// group like new ones. Idempotent — only rows still missing one are touched, so this is a
    /// no-op on every launch after the first.
    ///
    /// <para>
    /// Two rules, because neither existing field covers both cases: interviews created off a bid
    /// share a <c>BidId</c>, and everything else is chained through <c>ParentInterviewId</c>, so
    /// the chain is walked to its root and the root's id becomes the process.
    /// </para>
    /// </summary>
    private async Task BackfillInterviewProcessesAsync()
    {
        var missing = await _db.Interviews
            .Find(Builders<Interview>.Filter.Eq(i => i.ProcessId, ObjectId.Empty))
            .ToListAsync();
        if (missing.Count == 0) return;

        // Whole set, so a parent that already has a process id can still be followed.
        var all = await _db.Interviews.Find(FilterDefinition<Interview>.Empty).ToListAsync();
        var byId = all.ToDictionary(i => i.Id);
        var processByBid = new Dictionary<ObjectId, ObjectId>();
        var updated = 0;

        foreach (var iv in missing)
        {
            ObjectId process;

            if (iv.BidId != ObjectId.Empty)
            {
                // Bid-origin: every round off one bid is one process.
                if (!processByBid.TryGetValue(iv.BidId, out process))
                {
                    process = ObjectId.GenerateNewId();
                    processByBid[iv.BidId] = process;
                }
            }
            else
            {
                // Chat-origin: walk to the root of the parent chain. The guard is a cycle
                // stopper — a corrupt chain shouldn't hang startup.
                var root = iv;
                var hops = 0;
                while (root.ParentInterviewId is { } parentId
                       && byId.TryGetValue(parentId, out var parent)
                       && hops++ < 50)
                {
                    root = parent;
                }
                process = root.ProcessId != ObjectId.Empty ? root.ProcessId : root.Id;
            }

            await _db.Interviews.UpdateOneAsync(
                Builders<Interview>.Filter.Eq(x => x.Id, iv.Id),
                Builders<Interview>.Update.Set(x => x.ProcessId, process));
            iv.ProcessId = process;
            updated++;
        }

        if (updated > 0)
            _activity.Info("Profiles", "Grouped interviews into processes",
                $"{updated} interview(s) assigned a process id.", silent: true);
    }

}
