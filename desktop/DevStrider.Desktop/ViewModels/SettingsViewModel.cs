using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly ProfileService _profiles;
    private readonly LocalApiServer _localApi;
    private readonly ActivityLogService _activity;
    private readonly RegistrySyncService _registrySync;
    private readonly AtlasContext _atlas;

    public LocalApiServer LocalApi => _localApi;

    public SettingsViewModel(
        SettingsService settings,
        ProfileService profiles,
        LocalApiServer localApi,
        ActivityLogService activity,
        RegistrySyncService registrySync,
        AtlasContext atlas)
    {
        _settings = settings;
        _profiles = profiles;
        _localApi = localApi;
        _activity = activity;
        _registrySync = registrySync;
        _atlas = atlas;
    }

    private AppSettings _model = new();
    public AppSettings Model { get => _model; set => SetProperty(ref _model, value); }

    private string _username = "me";
    /// <summary>Mirror of <see cref="UserProfile.Username"/> — your filename in the shared cluster.</summary>
    public string Username { get => _username; set => SetProperty(ref _username, value); }

    /// <summary>
    /// Sharing key bound to the TextBox via this property so we can fan out a
    /// <see cref="SharingKeyFingerprint"/> recomputation on every keystroke. Writes through
    /// to <see cref="AppSettings.SharingKey"/>; reads from the same on Load. Kept for the
    /// fingerprint UI and as a placeholder for a future per-row encryption layer.
    /// </summary>
    private string _sharingKeyInput = "";
    public string SharingKeyInput
    {
        get => _sharingKeyInput;
        set
        {
            if (SetProperty(ref _sharingKeyInput, value))
            {
                Model.SharingKey = value ?? "";
                OnPropertyChanged(nameof(SharingKeyFingerprint));
            }
        }
    }

    /// <summary>First 8 hex chars of SHA-256(SharingKey) — members compare out-of-band.</summary>
    public string SharingKeyFingerprint
    {
        get
        {
            var k = _sharingKeyInput ?? "";
            if (string.IsNullOrEmpty(k)) return "(empty)";
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(k));
            return Convert.ToHexString(bytes).Substring(0, 8).ToLowerInvariant();
        }
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            Model = await _settings.GetAsync();
            var profile = await _profiles.GetAsync();
            Username = profile.Username;
            SharingKeyInput = Model.SharingKey ?? "";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            await _settings.SaveAsync(Model);

            // Mirror Sharing key + Word macro into the registry so they outlive Mongo.
            await _registrySync.PushAsync();

            var p = await _profiles.GetAsync();
            p.Username = string.IsNullOrWhiteSpace(Username) ? "me" : Username.Trim();
            await _profiles.SaveAsync(p);

            // Always ensure the listener is running on the (possibly new) saved port.
            if (_localApi.IsRunning && _localApi.BoundPort != Model.ListenerPort)
            {
                await _localApi.StopAsync();
                _localApi.Start(Model.ListenerPort);
            }
            else if (!_localApi.IsRunning)
            {
                _localApi.Start(Model.ListenerPort);
            }

            StatusMessage = "Saved.";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task RestartListenerAsync()
    {
        await _localApi.StopAsync();
        _localApi.Start(Model.ListenerPort);
    }

    /// <summary>
    /// Pull Sharing key + Word macro from the registry into the form (discards unsaved edits
    /// to those three fields).
    /// </summary>
    [RelayCommand]
    public async Task SyncFromRegistryAsync()
    {
        IsBusy = true;
        try
        {
            var changed = await _registrySync.PullAsync();
            Model = await _settings.GetAsync();
            SharingKeyInput = Model.SharingKey ?? "";
            StatusMessage = changed
                ? "Pulled Sharing key + Word macro from registry."
                : "Already in sync with registry.";
            _activity.Success("Registry", "Synced from registry",
                changed ? "Sharing key / Word macro updated from HKCU\\Software\\DevStrider." : "Already in sync.");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Registry sync failed: {ex.Message}";
            _activity.Error("Registry", "Sync from registry failed", ex.Message);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Save current form, then ping the shared cluster — surfaces TLS / auth / DNS errors fast.</summary>
    [RelayCommand]
    public async Task TestSharedConnectionAsync()
    {
        IsBusy = true;
        try
        {
            await _settings.SaveAsync(Model);
            var (ok, message) = await _atlas.TestConnectionAsync();
            StatusMessage = ok ? $"Shared cluster reachable: {message}" : $"Shared cluster unreachable: {message}";
            if (ok) _activity.Success("Atlas", "Connection test passed", message);
            else _activity.Error("Atlas", "Connection test failed", message);
        }
        finally { IsBusy = false; }
    }
}
