using DevStrider.Desktop.Models;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Keeps a small set of fields synchronised between <c>HKCU\Software\DevStrider</c> and
/// <see cref="AppSettings"/>. The registry is the long-lived copy (survives Mongo wipes);
/// AppSettings is the working copy used by the rest of the app.
///
/// <para>
/// Synced fields: <c>SharingKey</c> (DPAPI-encrypted in the registry), <c>WordDocPath</c>,
/// <c>WordHotkey</c>. Everything else stays Mongo-only.
/// </para>
/// </summary>
public sealed class RegistrySyncService
{
    public const string SharingKeyValue   = "SharingKey";
    public const string WordDocPathValue  = "WordDocPath";
    public const string WordHotkeyValue   = "WordHotkey";

    private readonly RegistryStore _registry;
    private readonly SettingsService _settings;
    private readonly ActivityLogService _activity;

    public RegistrySyncService(RegistryStore registry, SettingsService settings, ActivityLogService activity)
    {
        _registry = registry;
        _settings = settings;
        _activity = activity;
    }

    /// <summary>Registry → AppSettings. Returns true if anything in Mongo changed.</summary>
    public async Task<bool> PullAsync()
    {
        var s = await _settings.GetAsync();
        var dirty = false;

        var sharing = _registry.ReadProtected(SharingKeyValue);
        if (sharing != null && !string.Equals(sharing, s.SharingKey ?? "", StringComparison.Ordinal))
        {
            s.SharingKey = sharing;
            dirty = true;
        }

        var path = _registry.Read(WordDocPathValue);
        if (path != null && !string.Equals(path, s.WordDocPath ?? "", StringComparison.Ordinal))
        {
            s.WordDocPath = path;
            dirty = true;
        }

        var hotkey = _registry.Read(WordHotkeyValue);
        if (hotkey != null && !string.Equals(hotkey, s.WordHotkey ?? "", StringComparison.Ordinal))
        {
            s.WordHotkey = hotkey;
            dirty = true;
        }

        if (dirty) await _settings.SaveAsync(s);
        return dirty;
    }

    /// <summary>AppSettings → Registry. Always writes; <see cref="RegistryStore.Write"/> deletes empty values.</summary>
    public async Task PushAsync()
    {
        var s = await _settings.GetAsync();
        _registry.WriteProtected(SharingKeyValue, s.SharingKey ?? "");
        _registry.Write(WordDocPathValue, s.WordDocPath ?? "");
        _registry.Write(WordHotkeyValue, s.WordHotkey ?? "");
    }

    /// <summary>
    /// Launch-time reconciliation: if the registry has any of the three values, pull (the
    /// registry wins). Otherwise the AppSettings copy is the only known good state and we
    /// seed the registry from it so future launches survive a Mongo wipe.
    /// </summary>
    public async Task InitialSyncAsync()
    {
        var hasRegistry =
            _registry.Read(SharingKeyValue)  != null ||
            _registry.Read(WordDocPathValue) != null ||
            _registry.Read(WordHotkeyValue)  != null;

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
