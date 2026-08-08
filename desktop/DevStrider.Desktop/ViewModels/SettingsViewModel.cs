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
    private readonly AtlasContext _atlas;

    public LocalApiServer LocalApi => _localApi;

    public SettingsViewModel(
        SettingsService settings,
        ProfileService profiles,
        LocalApiServer localApi,
        ActivityLogService activity,
        AtlasContext atlas)
    {
        _settings = settings;
        _profiles = profiles;
        _localApi = localApi;
        _activity = activity;
        _atlas = atlas;
    }

    private AppSettings _model = new();
    public AppSettings Model { get => _model; set => SetProperty(ref _model, value); }

    private string _username = "me";
    /// <summary>Mirror of <see cref="UserProfile.Username"/> — the key your rows are filed under.</summary>
    public string Username { get => _username; set => SetProperty(ref _username, value); }

    private string _email = "";
    /// <summary>Mirror of <see cref="UserProfile.PersonalEmail"/> — published to teammates.</summary>
    public string Email { get => _email; set => SetProperty(ref _email, value); }

    /// <summary>
    /// Buffer for the shared-cluster password, fed by the <c>PasswordBox</c>'s
    /// <c>PasswordChanged</c> handler and applied to <see cref="Model"/> in
    /// <see cref="SaveAsync"/>. Empty means "leave the saved password alone" — the box always
    /// renders blank on load, so an untouched box must not wipe a working password.
    /// </summary>
    public string SharedMongoPasswordEntry { get; set; } = "";

    private string _sharedPasswordHint = "";
    /// <summary>Whether a password is currently saved, without rendering it into the UI.</summary>
    public string SharedPasswordHint
    {
        get => _sharedPasswordHint;
        private set => SetProperty(ref _sharedPasswordHint, value);
    }

    /// <summary>Same "blank means keep" contract as <see cref="SharedMongoPasswordEntry"/>.</summary>
    public string R2SecretEntry { get; set; } = "";

    private string _r2SecretHint = "";
    public string R2SecretHint { get => _r2SecretHint; private set => SetProperty(ref _r2SecretHint, value); }

    private string _r2EndpointDisplay = "";
    /// <summary>Read-only echo of the endpoint derived from the account id, so typos are visible.</summary>
    public string R2EndpointDisplay { get => _r2EndpointDisplay; private set => SetProperty(ref _r2EndpointDisplay, value); }

    private void RefreshR2Hints()
    {
        R2SecretHint = !string.IsNullOrEmpty(Model.R2SecretAccessKey)
            ? "A secret key is saved. Leave blank to keep it; type to replace it."
            : "No secret key saved — resume upload is disabled until you set one.";
        R2EndpointDisplay = string.IsNullOrEmpty(Model.R2Endpoint)
            ? "Endpoint: (set an account ID)"
            : $"Endpoint: {Model.R2Endpoint}/{Model.R2Bucket}";
    }

    private void RefreshSharedPasswordHint() =>
        SharedPasswordHint = !string.IsNullOrEmpty(Model.SharedMongoPassword)
            ? "A password is saved. Leave blank to keep it; type to replace it."
            : "No password saved — peer sync is disabled until you set one.";

    /// <summary>Clear the saved shared-cluster password and disable peer sync.</summary>
    [RelayCommand]
    public async Task ClearSharedPasswordAsync()
    {
        Model.SharedMongoPassword = "";
        SharedMongoPasswordEntry = "";
        await _settings.SaveAsync(Model);
        RefreshSharedPasswordHint();
        StatusMessage = "Shared-cluster password cleared — peer sync is now disabled.";
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            // A copy, not the shared cached instance — otherwise every keystroke in this form
            // would be live for the listener and sync services before the user hits Save.
            Model = await _settings.GetForEditAsync();
            var profile = await _profiles.GetAsync();
            Username = profile.Username;
            Email = profile.PersonalEmail ?? "";
            RefreshSharedPasswordHint();
            RefreshR2Hints();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            // Apply the typed password before the save. Blank means "keep what's there" — the
            // box renders empty on every load, so treating blank as "clear it" would silently
            // disable peer sync for anyone who saved an unrelated setting.
            if (!string.IsNullOrEmpty(SharedMongoPasswordEntry))
            {
                Model.SharedMongoPassword = SharedMongoPasswordEntry;
                SharedMongoPasswordEntry = "";
            }
            if (!string.IsNullOrEmpty(R2SecretEntry))
            {
                Model.R2SecretAccessKey = R2SecretEntry;
                R2SecretEntry = "";
            }

            await _settings.SaveAsync(Model);
            // Saving installed Model as the shared cache; take a fresh copy so continued
            // editing doesn't mutate what every other service is now reading.
            Model = await _settings.GetForEditAsync();
            RefreshSharedPasswordHint();
            RefreshR2Hints();

            var p = await _profiles.GetAsync();
            // Lowercase + no spaces: this is the join key on every pushed row, and peers match
            // it exactly. Normalising here beats discovering the mismatch after a sync.
            p.Username = string.IsNullOrWhiteSpace(Username)
                ? "me"
                : Username.Trim().ToLowerInvariant().Replace(' ', '-');
            p.PersonalEmail = (Email ?? "").Trim();
            await _profiles.SaveAsync(p);
            Username = p.Username;

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
