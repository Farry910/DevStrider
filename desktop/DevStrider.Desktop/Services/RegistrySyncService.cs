using DevStrider.Desktop.Models;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Keeps the Word-macro settings synchronised between <c>HKCU\Software\DevStrider</c> and
/// <see cref="AppSettings"/> / the active <see cref="Profile"/>. The registry is the
/// long-lived copy (survives Mongo wipes); AppSettings + Profile are the working copies.
///
/// <para>
/// Synced fields: <c>WordHotkey</c> (global) + <c>WordDocPath</c> (active profile's path —
/// registry holds what's effective right now). Everything else stays Mongo-only.
/// </para>
/// </summary>
public sealed class RegistrySyncService
{
    public const string WordDocPathValue = "WordDocPath";
    public const string WordHotkeyValue  = "WordHotkey";

    private readonly RegistryStore _registry;
    private readonly SettingsService _settings;
    private readonly ActivityLogService _activity;
    private readonly ProfileContext _profileContext;
    private readonly ProfilesService _profiles;

    public RegistrySyncService(
        RegistryStore registry,
        SettingsService settings,
        ActivityLogService activity,
        ProfileContext profileContext,
        ProfilesService profiles)
    {
        _registry = registry;
        _settings = settings;
        _activity = activity;
        _profileContext = profileContext;
        _profiles = profiles;
    }

    /// <summary>
    /// Registry → AppSettings + active profile. Returns true if anything changed.
    /// </summary>
    public async Task<bool> PullAsync()
    {
        var s = await _settings.GetAsync();
        var dirty = false;
        var profileDirty = false;

        var hotkey = _registry.Read(WordHotkeyValue);
        if (hotkey != null && !string.Equals(hotkey, s.WordHotkey ?? "", StringComparison.Ordinal))
        {
            s.WordHotkey = hotkey;
            dirty = true;
        }

        // Word doc path applies to the active profile only.
        var active = _profileContext.Current;
        if (active != null)
        {
            var path = _registry.Read(WordDocPathValue);
            if (path != null && !string.Equals(path, active.WordDocPath ?? "", StringComparison.Ordinal))
            {
                active.WordDocPath = path;
                profileDirty = true;
            }
        }

        if (dirty) await _settings.SaveAsync(s);
        if (profileDirty && active != null)
        {
            await _profiles.UpdateAsync(active);
            await _profileContext.RefreshListAsync();
        }
        return dirty || profileDirty;
    }

    /// <summary>
    /// AppSettings + active profile → Registry. Empty values are deleted from the registry.
    /// </summary>
    public async Task PushAsync()
    {
        var s = await _settings.GetAsync();
        _registry.Write(WordHotkeyValue, s.WordHotkey ?? "");
        _registry.Write(WordDocPathValue, _profileContext.Current?.WordDocPath ?? "");
    }

    /// <summary>
    /// Launch-time reconciliation: if the registry has any of the values, pull (the
    /// registry wins). Otherwise the AppSettings copy is the only known good state and we
    /// seed the registry from it so future launches survive a Mongo wipe.
    /// </summary>
    public async Task InitialSyncAsync()
    {
        var hasRegistry =
            _registry.Read(WordHotkeyValue)  != null ||
            _registry.Read(WordDocPathValue) != null;

        try
        {
            if (hasRegistry)
            {
                var changed = await PullAsync();
                if (changed) _activity.Info("Registry", "Synced settings from registry", silent: true);
            }
            else
            {
                await PushAsync();
                _activity.Info("Registry", "Seeded registry from current settings", silent: true);
            }
        }
        catch (Exception ex)
        {
            _activity.Warning("Registry", "Registry sync failed", ex.Message);
        }
    }
}
