using DevStrider.Desktop.Models;

namespace DevStrider.Desktop.Services;

/// <summary>
/// One-time env-var seeding for empty/default settings. Each DEVSTRIDER_* variable feeds
/// one AppSettings or UserProfile field, but only when that field is still at its
/// hardcoded default. After seeding, the Settings UI is the single source of truth and
/// env vars stop mattering — clear them once you've launched at least once.
///
/// Supported variables:
///   DEVSTRIDER_MONGO_URI         → AppSettings.MongoUri               (when default "mongodb://127.0.0.1:27017")
///   DEVSTRIDER_DATABASE_NAME     → AppSettings.DatabaseName            (when default "devstrider")
///   DEVSTRIDER_USERNAME          → UserProfile.Username                (when default "me" or current Windows user)
///   DEVSTRIDER_SHARED_DB_URI     → AppSettings.SharedDbUri              (when empty)
///   DEVSTRIDER_SHARED_DB_HOST    → AppSettings.SharedDbHost             (when empty)
///   DEVSTRIDER_SHARED_DB_PORT    → AppSettings.SharedDbPort             (when default 5432)
///   DEVSTRIDER_SHARED_DB_NAME    → AppSettings.SharedDbName             (when default "devstrider")
///   DEVSTRIDER_SHARED_DB_USER    → AppSettings.SharedDbUser             (when empty)
///   DEVSTRIDER_SHARED_DB_PASSWORD→ AppSettings.SharedDbPassword         (when empty)
///   DEVSTRIDER_R2_ACCOUNT_ID     → AppSettings.R2AccountId             (when empty)
///   DEVSTRIDER_R2_BUCKET         → AppSettings.R2Bucket                (when empty)
///   DEVSTRIDER_R2_ACCESS_KEY_ID  → AppSettings.R2AccessKeyId           (when empty)
///   DEVSTRIDER_R2_SECRET_KEY     → AppSettings.R2SecretAccessKey       (when empty)
///   DEVSTRIDER_SYNC_INTERVAL_MIN → AppSettings.SyncIntervalMinutes     (when default 60)
///   DEVSTRIDER_LISTENER_PORT     → AppSettings.ListenerPort            (when default 8765)
///   DEVSTRIDER_WORD_DOC_PATH     → AppSettings.WordDocPath             (when empty)
///   DEVSTRIDER_WORD_HOTKEY       → AppSettings.WordHotkey              (when default "F9")
///
/// Note: MongoUri / DatabaseName get seeded into AppSettings here for the UI to display
/// them, but the live MongoContext is constructed from the same env vars at startup
/// (App.OnStartup) — so a runtime change in the UI won't actually re-point the connection.
/// </summary>
public static class SettingsBootstrap
{
    public static async Task ApplyAsync(SettingsService settingsService, ProfileService profileService)
    {
        var settings = await settingsService.GetForEditAsync();
        var profile = await profileService.GetAsync();
        var settingsDirty = false;
        var profileDirty = false;

        settingsDirty |= SeedIfMatch(settings.MongoUri,           "mongodb://127.0.0.1:27017", "DEVSTRIDER_MONGO_URI",        v => settings.MongoUri = v);
        settingsDirty |= SeedIfMatch(settings.DatabaseName,       "devstrider",                "DEVSTRIDER_DATABASE_NAME",    v => settings.DatabaseName = v);


        // Shared PostgreSQL. Seeding the URI flips the mode to "uri" so the seeded value is the
        // one actually used; seeding a host flips it to "parts" for the same reason.
        if (SeedIfEmpty(settings.SharedDbUri, "DEVSTRIDER_SHARED_DB_URI", v => settings.SharedDbUri = v))
        {
            settings.SharedDbMode = SharedDbCredentials.ModeUri;
            settingsDirty = true;
        }
        if (SeedIfEmpty(settings.SharedDbHost, "DEVSTRIDER_SHARED_DB_HOST", v => settings.SharedDbHost = v))
        {
            settings.SharedDbMode = SharedDbCredentials.ModeParts;
            settingsDirty = true;
        }
        settingsDirty |= SeedIfMatch(settings.SharedDbName, "devstrider", "DEVSTRIDER_SHARED_DB_NAME", v => settings.SharedDbName = v);
        settingsDirty |= SeedIfEmpty(settings.SharedDbUser,             "DEVSTRIDER_SHARED_DB_USER", v => settings.SharedDbUser = v);
        settingsDirty |= SeedIfEmpty(settings.SharedDbPassword,         "DEVSTRIDER_SHARED_DB_PASSWORD", v => settings.SharedDbPassword = v);
        if (settings.SharedDbPort == 5432)
        {
            var portEnv = ReadEnv("DEVSTRIDER_SHARED_DB_PORT");
            if (portEnv != null && int.TryParse(portEnv, out var pgPort) && pgPort > 0 && pgPort < 65536)
            {
                settings.SharedDbPort = pgPort;
                settingsDirty = true;
            }
        }

        // Cloud storage (R2) — same rule: seeded once into the local settings row, then the
        // Settings UI owns them.
        settingsDirty |= SeedIfEmpty(settings.R2AccountId,                                     "DEVSTRIDER_R2_ACCOUNT_ID",    v => settings.R2AccountId = v);
        settingsDirty |= SeedIfEmpty(settings.R2Bucket,                                        "DEVSTRIDER_R2_BUCKET",        v => settings.R2Bucket = v);
        settingsDirty |= SeedIfEmpty(settings.R2AccessKeyId,                                   "DEVSTRIDER_R2_ACCESS_KEY_ID", v => settings.R2AccessKeyId = v);
        settingsDirty |= SeedIfEmpty(settings.R2SecretAccessKey,                               "DEVSTRIDER_R2_SECRET_KEY",    v => settings.R2SecretAccessKey = v);

        settingsDirty |= SeedIfEmpty(settings.WordDocPath,                                     "DEVSTRIDER_WORD_DOC_PATH",    v => settings.WordDocPath = v);
        settingsDirty |= SeedIfMatch(settings.WordHotkey,         "F9",                        "DEVSTRIDER_WORD_HOTKEY",      v => settings.WordHotkey = v);

        // 0 is meaningful here (disables scheduled sync), so accept the full non-negative range.
        if (settings.SyncIntervalMinutes == 60)
        {
            var syncEnv = ReadEnv("DEVSTRIDER_SYNC_INTERVAL_MIN");
            if (syncEnv != null && int.TryParse(syncEnv, out var mins) && mins >= 0 && mins <= 10080)
            {
                settings.SyncIntervalMinutes = mins;
                settingsDirty = true;
            }
        }

        // Int field — accept only well-formed integers in the listening-port range.
        if (settings.ListenerPort == 8765)
        {
            var portEnv = ReadEnv("DEVSTRIDER_LISTENER_PORT");
            if (portEnv != null && int.TryParse(portEnv, out var port) && port > 0 && port < 65536)
            {
                settings.ListenerPort = port;
                settingsDirty = true;
            }
        }

        // Treat the OS-derived default (and the legacy "me") as still-defaulted so an env
        // var can override either. Anything custom the user typed is left alone.
        var currentUser = (profile.Username ?? "").Trim();
        var osDefault = ProfileService.DefaultUsername();
        if (string.Equals(currentUser, "me", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(currentUser, osDefault, StringComparison.OrdinalIgnoreCase))
        {
            var u = ReadEnv("DEVSTRIDER_USERNAME");
            if (u != null) { profile.Username = u; profileDirty = true; }
        }

        if (settingsDirty) await settingsService.SaveAsync(settings);
        if (profileDirty)  await profileService.SaveAsync(profile);
    }

    /// <summary>Reads an env var, trimmed; returns null for unset or whitespace-only values.</summary>
    public static string? ReadEnv(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    private static bool SeedIfEmpty(string current, string envName, Action<string> set)
    {
        if (!string.IsNullOrWhiteSpace(current)) return false;
        var v = ReadEnv(envName);
        if (v == null) return false;
        set(v);
        return true;
    }

    private static bool SeedIfMatch(string current, string defaultValue, string envName, Action<string> set)
    {
        if (!string.Equals(current?.Trim() ?? "", defaultValue, StringComparison.Ordinal)) return false;
        var v = ReadEnv(envName);
        if (v == null) return false;
        set(v);
        return true;
    }
}
