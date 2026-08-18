using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// The sign-in form. Runs before the main window exists and is the only thing that puts an
/// account into <see cref="SessionContext"/>.
///
/// <para>
/// It also carries the shared-database connection form, which looks like scope creep and isn't:
/// signing in <i>is</i> a database query, and the connection details live behind the Settings tab,
/// which is behind the main window, which is behind this form. On a fresh install that circle has
/// to be broken somewhere, and here is the only place where it can be.
/// </para>
///
/// <para>
/// There is no "remember me". The password is asked for on every start of the app and nothing
/// about the session reaches disk — see <see cref="SessionContext"/>.
/// </para>
/// </summary>
public partial class LoginViewModel : ViewModelBase
{
    private readonly AuthService _auth;
    private readonly SettingsService _settings;
    private readonly SharedDbContext _shared;

    /// <summary>Raised once, on the UI thread, after the session has been established.</summary>
    public event Action? SignedIn;

    public LoginViewModel(AuthService auth, SettingsService settings, SharedDbContext shared)
    {
        _auth = auth;
        _settings = settings;
        _shared = shared;
    }

    private string _email = "";
    public string Email { get => _email; set => SetProperty(ref _email, value); }

    /// <summary>
    /// Pushed from the view's <c>PasswordChanged</c> handler. A <c>PasswordBox</c> can't be bound
    /// — WPF deliberately keeps the plaintext out of the binding engine — so this is the seam.
    /// Cleared after every attempt, successful or not.
    /// </summary>
    public string Password { get; set; } = "";

    private string _error = "";
    /// <summary>Why the last attempt failed. Empty when there is nothing to say.</summary>
    public string Error { get => _error; private set => SetProperty(ref _error, value); }

    private bool _showConnection;
    /// <summary>
    /// Whether the connection panel is open. Starts open when the app has no usable connection
    /// details, because in that state the credential fields cannot do anything.
    /// </summary>
    public bool ShowConnection { get => _showConnection; set => SetProperty(ref _showConnection, value); }

    private bool _isConfigured;
    /// <summary>False until the settings file has enough to attempt a connection.</summary>
    public bool IsConfigured { get => _isConfigured; private set => SetProperty(ref _isConfigured, value); }

    private AppSettings _connection = new();
    /// <summary>
    /// An editable copy of the settings — never the cached instance, so a half-typed host doesn't
    /// become live for the rest of the app before Save.
    /// </summary>
    public AppSettings Connection { get => _connection; set => SetProperty(ref _connection, value); }

    /// <summary>Same "blank means keep what's saved" contract as the Settings tab.</summary>
    public string SharedDbPasswordEntry { get; set; } = "";

    private string _connectionMessage = "";
    public string ConnectionMessage { get => _connectionMessage; private set => SetProperty(ref _connectionMessage, value); }

    /// <summary>
    /// Radio state for the credential mode. Two bools rather than the raw string, because WPF
    /// radio buttons want booleans and the persisted value stays a readable "uri"/"parts".
    /// </summary>
    public bool IsUriMode
    {
        get => !string.Equals(Connection.SharedDbMode, SharedDbCredentials.ModeParts, StringComparison.OrdinalIgnoreCase);
        set
        {
            if (!value) return;                          // only the checked radio drives the change
            Connection.SharedDbMode = SharedDbCredentials.ModeUri;
            OnPropertyChanged(nameof(IsUriMode));
            OnPropertyChanged(nameof(IsPartsMode));
        }
    }

    public bool IsPartsMode
    {
        get => !IsUriMode;
        set
        {
            if (!value) return;
            Connection.SharedDbMode = SharedDbCredentials.ModeParts;
            OnPropertyChanged(nameof(IsUriMode));
            OnPropertyChanged(nameof(IsPartsMode));
        }
    }

    /// <summary>Load the connection form and decide whether to open it. Call once, on window load.</summary>
    public async Task InitializeAsync()
    {
        Connection = await _settings.GetForEditAsync();
        IsConfigured = await _shared.IsConfiguredAsync();
        ShowConnection = !IsConfigured;
        OnPropertyChanged(nameof(IsUriMode));
        OnPropertyChanged(nameof(IsPartsMode));
        if (!IsConfigured)
            ConnectionMessage = "No database configured yet. Fill this in, test it, then sign in.";
    }

    [RelayCommand]
    public async Task SignInAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Error = "";
        try
        {
            var result = await _auth.SignInAsync(Email, Password);
            if (!result.Ok)
            {
                Error = result.Message;
                // A failure that is really about the connection should land the user on the panel
                // that fixes it rather than on a form they will retype correctly and fail again.
                if (!await _shared.IsConfiguredAsync()) ShowConnection = true;
                return;
            }
            SignedIn?.Invoke();
        }
        catch (Exception ex)
        {
            // SignInAsync converts everything it expects into a message; anything reaching here is
            // a genuine surprise and still must not take the window down.
            Error = SharedDbCredentials.Redact(ex.Message);
        }
        finally
        {
            // Whatever happened, the plaintext does not stay in memory waiting for the next click.
            Password = "";
            IsBusy = false;
        }
    }

    /// <summary>Persist the connection form. Shared with Test, which has to save before it probes.</summary>
    [RelayCommand]
    public async Task SaveConnectionAsync()
    {
        if (!string.IsNullOrEmpty(SharedDbPasswordEntry))
        {
            Connection.SharedDbPassword = SharedDbPasswordEntry;
            SharedDbPasswordEntry = "";
        }
        await _settings.SaveAsync(Connection);
        // SaveAsync installed this instance as the shared cache; take a fresh copy so continued
        // editing doesn't mutate what everything else is now reading.
        Connection = await _settings.GetForEditAsync();
        IsConfigured = await _shared.IsConfiguredAsync();
        OnPropertyChanged(nameof(IsUriMode));
        OnPropertyChanged(nameof(IsPartsMode));
    }

    /// <summary>
    /// Save the form, then ping the server. Saving first is not optional: the password sits in
    /// <see cref="SharedDbPasswordEntry"/> until Save copies it across, so a test that skipped it
    /// would probe with no password and report a failure the user cannot explain.
    /// </summary>
    [RelayCommand]
    public async Task TestConnectionAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            ConnectionMessage = "Testing…";
            await SaveConnectionAsync();
            var (ok, message) = await _shared.TestConnectionAsync();
            ConnectionMessage = ok ? $"Connected — {message}" : message;
        }
        catch (Exception ex)
        {
            ConnectionMessage = SharedDbCredentials.Redact(ex.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public void ToggleConnection() => ShowConnection = !ShowConnection;
}
