using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;
using DevStrider.Desktop.Services.HrApi;

namespace DevStrider.Desktop.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly SessionContext _session;
    private readonly LocalApiServer _localApi;
    private readonly ActivityLogService _activity;
    private readonly HrApiClient _hrApi;
    private readonly R2StorageService _storage;

    public LocalApiServer LocalApi => _localApi;

    public SettingsViewModel(
        SettingsService settings,
        SessionContext session,
        LocalApiServer localApi,
        ActivityLogService activity,
        HrApiClient hrApi,
        R2StorageService storage)
    {
        _settings = settings;
        _session = session;
        _localApi = localApi;
        _activity = activity;
        _hrApi = hrApi;
        _storage = storage;
    }

    /// <summary>
    /// The signed-in portal address. Read-only on purpose: hr-system owns accounts, and a second
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

    /// <summary>Same "blank means keep" contract as <see cref="R2SecretEntry"/>.</summary>
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

    private string _hrTokenHint = "";
    /// <summary>Whether a session token is currently saved, without rendering it into the UI.</summary>
    public string HrTokenHint { get => _hrTokenHint; private set => SetProperty(ref _hrTokenHint, value); }

    private void RefreshHrTokenHint()
    {
        HrTokenHint = Model.HrTokenExpiresAt is { } exp && !string.IsNullOrEmpty(Model.HrToken)
            ? $"Signed in — session good until {exp.ToLocalTime():g}."
            : "Not signed in.";
    }

    /// <summary>
    /// Drop the saved hr-system session. The next launch stops at the login window instead of
    /// signing back in silently — the point of the button, so it is not a small thing to click.
    /// </summary>
    [RelayCommand]
    public async Task SignOutAsync()
    {
        await _hrApi.ClearTokenAsync();
        Model = await _settings.GetForEditAsync();
        RefreshHrTokenHint();
        StatusMessage = "Signed out — you'll be asked to sign in again at the next launch.";
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
            RefreshHrTokenHint();
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
            if (!string.IsNullOrEmpty(R2SecretEntry))
            {
                Model.R2SecretAccessKey = R2SecretEntry;
                R2SecretEntry = "";
            }
            Model.HrApiBaseUrl = (Model.HrApiBaseUrl ?? "").Trim().TrimEnd('/');
            await _settings.SaveAsync(Model);
            // Saving installed Model as the shared cache; take a fresh copy so continued
            // editing doesn't mutate what every other service is now reading.
            Model = await _settings.GetForEditAsync();
            RefreshHrTokenHint();
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
}
