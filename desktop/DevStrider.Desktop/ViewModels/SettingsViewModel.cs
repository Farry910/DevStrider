using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly ProfileService _profiles;
    private readonly GitHubSyncService _sync;
    private readonly LocalApiServer _localApi;
    private readonly ActivityLogService _activity;
    private readonly RegistrySyncService _registrySync;

    public LocalApiServer LocalApi => _localApi;

    public SettingsViewModel(
        SettingsService settings,
        ProfileService profiles,
        GitHubSyncService sync,
        LocalApiServer localApi,
        ActivityLogService activity,
        RegistrySyncService registrySync)
    {
        _settings = settings;
        _profiles = profiles;
        _sync = sync;
        _localApi = localApi;
        _activity = activity;
        _registrySync = registrySync;
    }

    private AppSettings _model = new();
    public AppSettings Model { get => _model; set => SetProperty(ref _model, value); }

    private string _username = "me";
    /// <summary>Mirror of <see cref="UserProfile.Username"/> — what your file is named in the team repo.</summary>
    public string Username { get => _username; set => SetProperty(ref _username, value); }

    /// <summary>Plaintext PAT bound to the password box; protected on save.</summary>
    private string _githubTokenPlain = "";
    public string GitHubTokenPlain { get => _githubTokenPlain; set => SetProperty(ref _githubTokenPlain, value); }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            Model = await _settings.GetAsync();
            var profile = await _profiles.GetAsync();
            Username = profile.Username;
            GitHubTokenPlain = SecretStore.Unprotect(Model.GitHubTokenProtected);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            Model.GitHubTokenProtected = SecretStore.Protect(GitHubTokenPlain ?? "");
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
    /// to those three fields). Use after editing the registry from outside DevStrider, or to
    /// restore values after wiping Mongo.
    /// </summary>
    [RelayCommand]
    public async Task SyncFromRegistryAsync()
    {
        IsBusy = true;
        try
        {
            var changed = await _registrySync.PullAsync();
            Model = await _settings.GetAsync();
            GitHubTokenPlain = SecretStore.Unprotect(Model.GitHubTokenProtected);
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

    // BrowseWordPath moved to ProfilesViewModel — the field is per-profile now.


    [RelayCommand]
    public async Task PushTodayAsync()
    {
        IsBusy = true;
        try
        {
            var profile = await _profiles.GetAsync();
            await _sync.PushTodayAsync(profile.Username);
            StatusMessage = $"Pushed snapshot to GitHub ({profile.Username}.json under today).";
            _activity.Success("GitHub", "Snapshot pushed", $"{profile.Username}.json · today");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Push failed: {ex.Message}";
            _activity.Error("GitHub", "Snapshot push failed", ex.Message);
        }
        finally { IsBusy = false; }
    }
}
