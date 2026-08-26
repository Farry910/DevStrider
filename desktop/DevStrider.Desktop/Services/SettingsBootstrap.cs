namespace DevStrider.Desktop.Services;

/// <summary>
/// One-time env-var seeding for empty/default settings. Each DEVSTRIDER_* variable feeds one
/// <see cref="Models.AppSettings"/> field, but only when that field is still at its hardcoded
/// default. After seeding, the Settings UI is the single source of truth and env vars stop
/// mattering — clear them once you've launched at least once.
///
/// Supported variables:
///   DEVSTRIDER_R2_ACCOUNT_ID      → R2AccountId         (when empty)
///   DEVSTRIDER_R2_BUCKET          → R2Bucket            (when empty)
///   DEVSTRIDER_R2_ACCESS_KEY_ID   → R2AccessKeyId       (when empty)
///   DEVSTRIDER_R2_SECRET_KEY      → R2SecretAccessKey   (when empty)
///   DEVSTRIDER_LISTENER_PORT      → ListenerPort        (when default 8765)
///   DEVSTRIDER_WORD_DOC_PATH      → WordDocPath         (when empty)
///   DEVSTRIDER_WORD_HOTKEY        → WordHotkey          (when default "F9")
///
/// There is no portal URL variable: the address is compiled in as <see cref="PortalApi.Url"/>.
/// </summary>
public static class SettingsBootstrap
{
    public static async Task ApplyAsync(SettingsService settingsService)
    {
        var settings = await settingsService.GetForEditAsync();
        var dirty = false;


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
