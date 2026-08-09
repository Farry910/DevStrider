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
    private readonly SharedDbContext _shared;

    public LocalApiServer LocalApi => _localApi;

    public SettingsViewModel(
        SettingsService settings,
        ProfileService profiles,
        LocalApiServer localApi,
        ActivityLogService activity,
        SharedDbContext shared)
    {
        _settings = settings;
        _profiles = profiles;
        _localApi = localApi;
        _activity = activity;
        _shared = shared;
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
    /// Buffer for the shared database password, fed by the <c>PasswordBox</c>'s
    /// <c>PasswordChanged</c> handler and applied to <see cref="Model"/> in
    /// <see cref="SaveAsync"/>. Empty means "leave the saved password alone" — the box always
    /// renders blank on load, so an untouched box must not wipe a working password.
    /// </summary>
    public string SharedDbPasswordEntry { get; set; } = "";

    private string _sharedDbHint = "";
    /// <summary>Whether a password is currently saved, without rendering it into the UI.</summary>
    public string SharedDbHint
    {
        get => _sharedDbHint;
        private set => SetProperty(ref _sharedDbHint, value);
    }

    /// <summary>Same "blank means keep" contract as <see cref="SharedDbPasswordEntry"/>.</summary>
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

    /// <summary>
    /// Radio-button state for the credential mode. Two bools rather than binding the raw string,
    /// because WPF radio buttons want booleans and the persisted value stays a readable
    /// <c>"uri"</c>/<c>"parts"</c> on the settings row.
    /// </summary>
    public bool IsUriMode
    {
        get => !string.Equals(Model.SharedDbMode, SharedDbCredentials.ModeParts, StringComparison.OrdinalIgnoreCase);
        set
        {
            if (!value) return;                          // only the checked radio drives the change
            Model.SharedDbMode = SharedDbCredentials.ModeUri;
            OnPropertyChanged(nameof(IsUriMode));
            OnPropertyChanged(nameof(IsPartsMode));
            RefreshSharedDbHint();
        }
    }

    public bool IsPartsMode
    {
        get => !IsUriMode;
        set
        {
            if (!value) return;
            Model.SharedDbMode = SharedDbCredentials.ModeParts;
            OnPropertyChanged(nameof(IsUriMode));
            OnPropertyChanged(nameof(IsPartsMode));
            RefreshSharedDbHint();
        }
    }

    private void RefreshSharedDbHint()
    {
        if (IsUriMode)
        {
            var (ok, error) = SharedDbCredentials.ValidateUri(Model.SharedDbUri);
            SharedDbHint = string.IsNullOrEmpty(Model.SharedDbUri)
                ? "Paste the service URI your provider gave you — peer sync is off until you do."
                : ok ? "URI looks valid. Use Test connection to confirm the server answers."
                     : $"Can't parse that URI: {error}";
            return;
        }

        SharedDbHint = string.IsNullOrEmpty(Model.SharedDbPassword)
            ? "No password saved — peer sync is disabled until you set one."
            : "A password is saved. Leave blank to keep it; type to replace it.";
    }

    /// <summary>Clear the saved shared database password and disable peer sync.</summary>
    [RelayCommand]
    public async Task ClearSharedPasswordAsync()
    {
        Model.SharedDbPassword = "";
        SharedDbPasswordEntry = "";
        await _settings.SaveAsync(Model);
        RefreshSharedDbHint();
        StatusMessage = "Shared database password cleared — peer sync is now disabled.";
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
            // Model was replaced wholesale, so the mode radios have to be told to re-read it.
            OnPropertyChanged(nameof(IsUriMode));
            OnPropertyChanged(nameof(IsPartsMode));
            RefreshSharedDbHint();
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
            if (!string.IsNullOrEmpty(SharedDbPasswordEntry))
            {
                Model.SharedDbPassword = SharedDbPasswordEntry;
                SharedDbPasswordEntry = "";
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
            RefreshSharedDbHint();
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

    /// <summary>Save the form, then ping the database — surfaces TLS / auth / firewall errors fast.</summary>
    [RelayCommand]
    public async Task TestSharedConnectionAsync()
    {
        IsBusy = true;
        try
        {
            await _settings.SaveAsync(Model);
            var (ok, message) = await _shared.TestConnectionAsync();
            StatusMessage = ok ? $"Shared database reachable — {message}" : $"Shared database unreachable — {message}";
            if (ok) _activity.Success("Peers", "Connection test passed", message);
            else _activity.Error("Peers", "Connection test failed", message);
        }
        finally { IsBusy = false; }
    }
}
