namespace DevStrider.Desktop.Services;

/// <summary>
/// One-time env-var seeding for empty/default settings. Each DEVSTRIDER_* variable feeds one
/// <see cref="Models.AppSettings"/> field, but only when that field is still at its hardcoded
/// default. After seeding, the Settings UI is the single source of truth and env vars stop
/// mattering — clear them once you've launched at least once.
///
/// Supported variables:
///   DEVSTRIDER_MONGO_URI          → MongoUri            (when default "mongodb://127.0.0.1:27017")
///   DEVSTRIDER_DATABASE_NAME      → DatabaseName        (when default "devstrider")
///   DEVSTRIDER_SHARED_DB_URI      → SharedDbUri         (when empty)
///   DEVSTRIDER_SHARED_DB_HOST     → SharedDbHost        (when empty)
///   DEVSTRIDER_SHARED_DB_PORT     → SharedDbPort        (when default 5432)
///   DEVSTRIDER_SHARED_DB_NAME     → SharedDbName        (when default "devstrider")
///   DEVSTRIDER_SHARED_DB_USER     → SharedDbUser        (when empty)
///   DEVSTRIDER_SHARED_DB_PASSWORD → SharedDbPassword    (when empty)
///   DEVSTRIDER_R2_ACCOUNT_ID      → R2AccountId         (when empty)
///   DEVSTRIDER_R2_BUCKET          → R2Bucket            (when empty)
///   DEVSTRIDER_R2_ACCESS_KEY_ID   → R2AccessKeyId       (when empty)
///   DEVSTRIDER_R2_SECRET_KEY      → R2SecretAccessKey   (when empty)
///   DEVSTRIDER_LISTENER_PORT      → ListenerPort        (when default 8765)
///   DEVSTRIDER_WORD_DOC_PATH      → WordDocPath         (when empty)
///   DEVSTRIDER_WORD_HOTKEY        → WordHotkey          (when default "F9")
///
/// There is no username variable any more: the account name is the portal address on
/// <c>app_user</c> and is written by <see cref="AuthService"/> at login. Nothing on this machine
/// gets to name a user.
///
/// The Mongo variables describe the legacy local database the one-time import reads, and nothing
/// else — DevStrider's own store is the shared PostgreSQL cluster.
/// </summary>
public static class SettingsBootstrap
{
    public static async Task ApplyAsync(SettingsService settingsService)
    {
        var settings = await settingsService.GetForEditAsync();
        var dirty = false;

        dirty |= SeedIfMatch(settings.MongoUri,     "mongodb://127.0.0.1:27017", "DEVSTRIDER_MONGO_URI",     v => settings.MongoUri = v);
        dirty |= SeedIfMatch(settings.DatabaseName, "devstrider",                "DEVSTRIDER_DATABASE_NAME", v => settings.DatabaseName = v);

        // Shared PostgreSQL. Seeding the URI flips the mode to "uri" so the seeded value is the
        // one actually used; seeding a host flips it to "parts" for the same reason.
        if (SeedIfEmpty(settings.SharedDbUri, "DEVSTRIDER_SHARED_DB_URI", v => settings.SharedDbUri = v))
        {
            settings.SharedDbMode = SharedDbCredentials.ModeUri;
            dirty = true;
        }
        if (SeedIfEmpty(settings.SharedDbHost, "DEVSTRIDER_SHARED_DB_HOST", v => settings.SharedDbHost = v))
        {
            settings.SharedDbMode = SharedDbCredentials.ModeParts;
            dirty = true;
        }
        dirty |= SeedIfMatch(settings.SharedDbName, "devstrider", "DEVSTRIDER_SHARED_DB_NAME", v => settings.SharedDbName = v);
        dirty |= SeedIfEmpty(settings.SharedDbUser,              "DEVSTRIDER_SHARED_DB_USER", v => settings.SharedDbUser = v);
        dirty |= SeedIfEmpty(settings.SharedDbPassword,          "DEVSTRIDER_SHARED_DB_PASSWORD", v => settings.SharedDbPassword = v);
        if (settings.SharedDbPort == 5432)
        {
            var portEnv = ReadEnv("DEVSTRIDER_SHARED_DB_PORT");
            if (portEnv != null && int.TryParse(portEnv, out var pgPort) && pgPort > 0 && pgPort < 65536)
            {
                settings.SharedDbPort = pgPort;
                dirty = true;
            }
        }

        // Cloud storage (R2) — same rule: seeded once into the local settings file, then the
        // Settings UI owns them.
        dirty |= SeedIfEmpty(settings.R2AccountId,       "DEVSTRIDER_R2_ACCOUNT_ID",    v => settings.R2AccountId = v);
        dirty |= SeedIfEmpty(settings.R2Bucket,          "DEVSTRIDER_R2_BUCKET",        v => settings.R2Bucket = v);
        dirty |= SeedIfEmpty(settings.R2AccessKeyId,     "DEVSTRIDER_R2_ACCESS_KEY_ID", v => settings.R2AccessKeyId = v);
        dirty |= SeedIfEmpty(settings.R2SecretAccessKey, "DEVSTRIDER_R2_SECRET_KEY",    v => settings.R2SecretAccessKey = v);

        dirty |= SeedIfEmpty(settings.WordDocPath,       "DEVSTRIDER_WORD_DOC_PATH",    v => settings.WordDocPath = v);
        dirty |= SeedIfMatch(settings.WordHotkey, "F9",  "DEVSTRIDER_WORD_HOTKEY",      v => settings.WordHotkey = v);

        // Int field — accept only well-formed integers in the listening-port range.
        if (settings.ListenerPort == 8765)
        {
            var portEnv = ReadEnv("DEVSTRIDER_LISTENER_PORT");
            if (portEnv != null && int.TryParse(portEnv, out var port) && port > 0 && port < 65536)
            {
                settings.ListenerPort = port;
                dirty = true;
            }
        }

        if (dirty) await settingsService.SaveAsync(settings);
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
