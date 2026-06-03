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
            var settings = await _settings.GetAsync();
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
        }
        catch (Exception ex)
        {
            _activity.Error("Profiles", "Migration failed", ex.Message);
        }
    }
}
