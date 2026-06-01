using DevStrider.Desktop.Models;

namespace DevStrider.Desktop.Services;

/// <summary>
/// One-time env-var seeding for empty/default settings. Each DEVSTRIDER_* variable feeds
/// one AppSettings or UserProfile field, but only when that field is still at its
/// hardcoded default. After seeding, the Settings UI is the single source of truth and
/// env vars stop mattering — clear them once you've launched the app at least once.
///
/// Supported variables:
///   DEVSTRIDER_MONGO_URI         → AppSettings.MongoUri              (when default "mongodb://127.0.0.1:27017")
///   DEVSTRIDER_DATABASE_NAME     → AppSettings.DatabaseName           (when default "devstrider")
///   DEVSTRIDER_USERNAME          → UserProfile.Username               (when default "me")
///   DEVSTRIDER_GITHUB_REPO_URL   → AppSettings.GitHubRepoUrl          (when empty)
///   DEVSTRIDER_GITHUB_BRANCH     → AppSettings.GitHubBranch           (when default "main")
///   DEVSTRIDER_GITHUB_PAT        → AppSettings.GitHubTokenProtected   (DPAPI-encrypted, when empty)
///   DEVSTRIDER_LISTENER_PORT     → AppSettings.ListenerPort           (when default 8765)
///   DEVSTRIDER_WORD_DOC_PATH     → AppSettings.WordDocPath            (when empty)
///   DEVSTRIDER_WORD_HOTKEY       → AppSettings.WordHotkey             (when default "F9")
///   DEVSTRIDER_SHARING_KEY       → AppSettings.SharingKey             (when empty)
///
/// Note: MongoUri / DatabaseName get seeded into AppSettings here for the UI to display
/// them, but the live MongoContext is constructed from the same env vars at startup
/// (App.OnStartup) — so a runtime change in the UI won't actually re-point the connection.
/// </summary>
public static class SettingsBootstrap
{
    public static async Task ApplyAsync(SettingsService settingsService, ProfileService profileService)
    {
        var settings = await settingsService.GetAsync();
        var profile = await profileService.GetAsync();
        var settingsDirty = false;
        var profileDirty = false;

        settingsDirty |= SeedIfMatch(settings.MongoUri,     "mongodb://127.0.0.1:27017", "DEVSTRIDER_MONGO_URI",       v => settings.MongoUri = v);
        settingsDirty |= SeedIfMatch(settings.DatabaseName, "devstrider",                "DEVSTRIDER_DATABASE_NAME",   v => settings.DatabaseName = v);
        settingsDirty |= SeedIfEmpty(settings.GitHubRepoUrl,                              "DEVSTRIDER_GITHUB_REPO_URL", v => settings.GitHubRepoUrl = v);
        settingsDirty |= SeedIfMatch(settings.GitHubBranch, "main",                      "DEVSTRIDER_GITHUB_BRANCH",   v => settings.GitHubBranch = v);
        settingsDirty |= SeedIfEmpty(settings.WordDocPath,                                "DEVSTRIDER_WORD_DOC_PATH",   v => settings.WordDocPath = v);
        settingsDirty |= SeedIfMatch(settings.WordHotkey,   "F9",                        "DEVSTRIDER_WORD_HOTKEY",     v => settings.WordHotkey = v);
        settingsDirty |= SeedIfEmpty(settings.SharingKey,                                 "DEVSTRIDER_SHARING_KEY",     v => settings.SharingKey = v);

        // PAT goes through DPAPI before it touches Mongo, so the env var only needs to be set
        // for the first launch — afterwards, the encrypted blob in Mongo is the source.
        if (string.IsNullOrEmpty(settings.GitHubTokenProtected))
        {
            var pat = ReadEnv("DEVSTRIDER_GITHUB_PAT");
            if (pat != null)
            {
                settings.GitHubTokenProtected = SecretStore.Protect(pat);
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

        if (string.Equals(profile.Username, "me", StringComparison.OrdinalIgnoreCase))
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
