using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly SessionContext _session;
    private readonly LocalApiServer _localApi;
    private readonly ActivityLogService _activity;
    private readonly SharedDbContext _shared;
    private readonly R2StorageService _storage;

    public LocalApiServer LocalApi => _localApi;

    public SettingsViewModel(
        SettingsService settings,
        SessionContext session,
        LocalApiServer localApi,
        ActivityLogService activity,
        SharedDbContext shared,
        R2StorageService storage)
    {
        _settings = settings;
        _session = session;
        _localApi = localApi;
        _activity = activity;
        _shared = shared;
        _storage = storage;
    }

    /// <summary>
    /// The signed-in portal address. Read-only on purpose: the portal owns accounts, and a second
    /// editable copy of who you are is a second answer waiting to disagree with the first.
    /// </summary>
    public string SignedInAs => _session.Email;

    private string _r2TestResult = "";
    public string R2TestResult { get => _r2TestResult; private set => SetProperty(ref _r2TestResult, value); }

    /// <summary>
    /// Prove the R2 credentials from Settings. Without this the first sign of a bad token is a
    /// failed upload, long after the fields were filled in and forgotten about.
    /// </summary>
    [RelayCommand]
    public async Task TestR2Async()
    {
        IsBusy = true;
        try
        {
            R2TestResult = "Testing…";
            // Save first: the service reads credentials from the settings file, so an untested
            // edit sitting in the text boxes would otherwise be invisible to it.
            await SaveAsync();
            var result = await _storage.TestAsync();
            R2TestResult = result.Message;
            if (result.Ok) _activity.Success("Settings", "Cloud storage test passed", result.Message);
            else _activity.Warning("Settings", "Cloud storage test failed", result.Message);
        }
        finally { IsBusy = false; }
    }

    private AppSettings _model = new();
    public AppSettings Model { get => _model; set => SetProperty(ref _model, value); }

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
    /// <c>"uri"</c>/<c>"parts"</c> in the settings file.
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
                ? "Paste the service URI your provider gave you — nothing works until this is set."
                : ok ? "URI looks valid. Use Test connection to confirm the server answers."
                     : $"Can't parse that URI: {error}";
            return;
        }

        SharedDbHint = string.IsNullOrEmpty(Model.SharedDbPassword)
            ? "No password saved — the app can't reach the database until you set one."
            : "A password is saved. Leave blank to keep it; type to replace it.";
    }

    /// <summary>
    /// Clear the saved shared database password.
    ///
    /// <para>
    /// This disconnects the app from its only store, so the next launch stops at the login window
    /// with nothing to sign in against. That is the point of the button — it exists to get a
    /// credential off a machine — but it is not a small thing to click.
    /// </para>
    /// </summary>
    [RelayCommand]
    public async Task ClearSharedPasswordAsync()
    {
        Model.SharedDbPassword = "";
        SharedDbPasswordEntry = "";
        await _settings.SaveAsync(Model);
        Model = await _settings.GetForEditAsync();
        RefreshSharedDbHint();
        StatusMessage = "Shared database password cleared — you'll have to re-enter it at the next sign-in.";
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            // A copy, not the shared cached instance — otherwise every keystroke in this form
            // would be live for the listener and every other service before the user hits Save.
            Model = await _settings.GetForEditAsync();
            OnPropertyChanged(nameof(SignedInAs));
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
            Model.ResumeGenerationsPerChat = Math.Clamp(Model.ResumeGenerationsPerChat, 1, 50);
            Model.ResumeOutputRoot = (Model.ResumeOutputRoot ?? "").Trim();
            Model.ResumeOutputFileBase = string.IsNullOrWhiteSpace(Model.ResumeOutputFileBase)
                ? "Resume"
                : Model.ResumeOutputFileBase.Trim();
            Model.SalaryExpectation = (Model.SalaryExpectation ?? "").Trim();
            // Apply the typed password before the save. Blank means "keep what's there" — the
            // box renders empty on every load, so treating blank as "clear it" would silently
            // disconnect anyone who saved an unrelated setting.
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
            // This must be the view-model's SaveAsync, not _settings.SaveAsync(Model).
            //
            // A PasswordBox can't be data-bound — the plaintext never enters the binding engine
            // — so a typed password sits in SharedDbPasswordEntry until SaveAsync copies it onto
            // Model. Saving Model directly skipped that: the test ran with no password ("No
            // password has been provided but the backend requires one") and, worse, persisted
            // the empty one over a working password.
            await SaveAsync();
            var (ok, message) = await _shared.TestConnectionAsync();
            StatusMessage = ok ? $"Shared database reachable — {message}" : $"Shared database unreachable — {message}";
            if (ok) _activity.Success("Database", "Connection test passed", message);
            else _activity.Error("Database", "Connection test failed", message);
        }
        finally { IsBusy = false; }
    }
}
