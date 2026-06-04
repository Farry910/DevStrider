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
    private readonly ThemeService _themeService;

    public LocalApiServer LocalApi => _localApi;

    public SettingsViewModel(
        SettingsService settings,
        ProfileService profiles,
        LocalApiServer localApi,
        ActivityLogService activity,
        RegistrySyncService registrySync,
        AtlasContext atlas,
        ThemeService themeService)
    {
        _settings = settings;
        _profiles = profiles;
        _localApi = localApi;
        _activity = activity;
        _registrySync = registrySync;
        _atlas = atlas;
        _themeService = themeService;
    }

    /// <summary>"System", "Light", or "Dark" — applies live on change, persists on next Save (but the active palette swap is immediate so Save isn't required just to preview).</summary>
    private string _themeChoice = "System";
    public string ThemeChoice
    {
        get => _themeChoice;
        set
        {
            if (SetProperty(ref _themeChoice, value) &&
                Enum.TryParse<ThemePreference>(value, ignoreCase: true, out var pref))
            {
                _ = _themeService.SetPreferenceAsync(pref);
            }
        }
    }

    private AppSettings _model = new();
    public AppSettings Model { get => _model; set => SetProperty(ref _model, value); }

    private string _username = "me";
    /// <summary>Mirror of <see cref="UserProfile.Username"/> — your filename in the shared cluster.</summary>
    public string Username { get => _username; set => SetProperty(ref _username, value); }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            Model = await _settings.GetAsync();
            var profile = await _profiles.GetAsync();
            Username = profile.Username;
            // Direct field-set so the SetPreferenceAsync side-effect doesn't fire on load.
            _themeChoice = string.IsNullOrWhiteSpace(Model.ThemePreference) ? "System" : Model.ThemePreference;
            OnPropertyChanged(nameof(ThemeChoice));
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
    /// Pull Word macro (path + hotkey) from the registry into the form (discards unsaved
    /// edits to those fields).
    /// </summary>
    [RelayCommand]
    public async Task SyncFromRegistryAsync()
    {
        IsBusy = true;
        try
        {
            var changed = await _registrySync.PullAsync();
            Model = await _settings.GetAsync();
            StatusMessage = changed
                ? "Pulled Word macro from registry."
                : "Already in sync with registry.";
            _activity.Success("Registry", "Synced from registry",
                changed ? "Word macro updated from HKCU\\Software\\DevStrider." : "Already in sync.");
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
