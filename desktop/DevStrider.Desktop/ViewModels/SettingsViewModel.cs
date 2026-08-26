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
    private readonly PortalApi _api;
    private readonly AuthService _auth;
    private readonly R2StorageService _storage;

    public LocalApiServer LocalApi => _localApi;

    public SettingsViewModel(
        SettingsService settings,
        SessionContext session,
        LocalApiServer localApi,
        ActivityLogService activity,
        PortalApi api,
        AuthService auth,
        R2StorageService storage)
    {
        _settings = settings;
        _session = session;
        _localApi = localApi;
        _activity = activity;
        _api = api;
        _auth = auth;
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

    private string _sessionHint = "";
    /// <summary>How much of the week is left. The one place the session's lifetime is visible.</summary>
    public string SessionHint { get => _sessionHint; private set => SetProperty(ref _sessionHint, value); }

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

    private void RefreshSessionHint()
    {
        SessionHint = _session.IsAuthenticated
            ? $"Signed in as {_session.Email}. This machine stays signed in until "
              + $"{_session.ExpiresAt.ToLocalTime():dddd d MMMM, HH:mm} — {(int)Math.Ceiling(_session.Remaining.TotalDays)} day(s) away. "
              + "It renews itself whenever you open DevStrider inside the last day, so in ordinary use you are never asked again."
            : "Not signed in on this machine.";
    }

    /// <summary>
    /// Forget the session on this machine.
    ///
    /// <para>
    /// This is what "clear the saved password" used to be, and it is a much smaller thing than it
    /// was: the token it deletes is scoped to DevStrider, dies on its own within a week, and can
    /// be declined by the portal at any point. What it does not do is remove a credential that
    /// could reach the whole team's database directly — there is no longer one on this machine to
    /// remove.
    /// </para>
    ///
    /// <para>
    /// Takes effect at the next launch, which then stops at the sign-in window. The current
    /// session stays usable so nobody loses a half-finished bid to a mis-click.
    /// </para>
    /// </summary>
    /// <summary>Proxy scope choices, for the picker.</summary>
    public IReadOnlyList<string> ProxyScopeOptions { get; } = [ProxyScopes.ChatGpt, ProxyScopes.All];

    private string _proxyHint = "";

    /// <summary>What the proxy settings currently amount to, in a sentence.</summary>
    public string ProxyHint { get => _proxyHint; private set => SetProperty(ref _proxyHint, value); }

    private void RefreshProxyHint()
    {
        if (!Model.ProxyEnabled) { ProxyHint = "Off — both browsers connect directly."; return; }
        var rejection = ProxyConfiguration.Reject(Model.ProxyAddress);
        if (rejection.Length > 0) { ProxyHint = rejection; return; }

        var proxy = new ProxyConfiguration(Model);
        var scope = proxy.AppliesToJobSites ? "ChatGPT and the job sites" : "ChatGPT only";
        var bypass = proxy.BypassList();
        ProxyHint = $"{scope} via {proxy.Address}. Bypassing: {bypass}. " +
                    "Takes effect the next time DevStrider starts — a browser cannot be moved " +
                    "onto a proxy once it is running.";
    }

    /// <summary>
    /// Asks the proxy for a ChatGPT URL and reports what came back.
    ///
    /// <para>
    /// Reaching ChatGPT is the whole point of the setting, so that is what gets asked for rather
    /// than some neutral host: a proxy that answers but still cannot reach ChatGPT is a different
    /// problem, and it is better to learn which one before a run does.
    /// </para>
    /// </summary>
    [RelayCommand]
    public async Task TestProxyAsync()
    {
        IsBusy = true;
        try
        {
            StatusMessage = "Testing the proxy...";
            StatusMessage = await new ProxyConfiguration(Model).TestAsync();
        }
        finally { IsBusy = false; }
    }
    [RelayCommand]
    public void SignOut()
    {
        _auth.SignOut();
        RefreshSessionHint();
        StatusMessage = "Signed out on this machine — you'll be asked for your password at the next launch.";
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
            RefreshSessionHint();
            RefreshR2Hints();
            RefreshProxyHint();
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
            Model.MaxReviewTabs = Math.Clamp(Model.MaxReviewTabs, 1, 8);
            // Blank means "keep what's there" — the box renders empty on every load, so treating
            // blank as "clear it" would silently disconnect anyone who saved an unrelated setting.
            if (!string.IsNullOrEmpty(R2SecretEntry))
            {
                Model.R2SecretAccessKey = R2SecretEntry;
                R2SecretEntry = "";
            }
            await _settings.SaveAsync(Model);
            // Saving installed Model as the shared cache; take a fresh copy so continued
            // editing doesn't mutate what every other service is now reading.
            Model = await _settings.GetForEditAsync();
            RefreshSessionHint();
            RefreshR2Hints();
            RefreshProxyHint();

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
    /// Save the form, then ping the portal — surfaces a typo, a DNS failure or a proxy sitting in
    /// front of the app while the person is still looking at the field they changed.
    ///
    /// <para>
    /// This must be the view-model's <see cref="SaveAsync"/> rather than
    /// <c>_settings.SaveAsync(Model)</c>: <see cref="PortalApi"/> reads the address out of the
    /// settings file, so a test that skipped the save would probe the previous address and report
    /// on something the user is no longer looking at.
    /// </para>
    /// </summary>
    [RelayCommand]
    public async Task TestPortalAsync()
    {
        IsBusy = true;
        try
        {
            await SaveAsync();
            var (ok, message) = await _api.TestAsync();
            StatusMessage = ok ? $"Portal reachable — {message}" : $"Portal unreachable — {message}";
            if (ok) _activity.Success("Portal", "Connection test passed", message);
            else _activity.Error("Portal", "Connection test failed", message);
        }
        finally { IsBusy = false; }
    }
}
