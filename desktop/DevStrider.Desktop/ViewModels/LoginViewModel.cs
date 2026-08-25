using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// The sign-in form. Runs before the main window exists and is the only thing that puts an
/// account into <see cref="SessionContext"/>.
///
/// <para>
/// It also carries the portal-address field, which looks like scope creep and isn't: signing in
/// <i>is</i> a call to the portal, and the address lives behind the Settings tab, which is behind
/// the main window, which is behind this form. On a fresh install that circle has to be broken
/// somewhere, and here is the only place it can be.
/// </para>
///
/// <para>
/// This form used to carry a whole database connection panel — host, port, database, user,
/// password, an SSL toggle and two credential modes — because the app opened its own PostgreSQL
/// connection and could not sign anyone in without one. All of that is one URL now, and the URL is
/// not a secret.
/// </para>
///
/// <para>
/// There is a "remember me", and it isn't optional: the portal answers a sign-in with a token good
/// for a week, and <see cref="SessionStore"/> keeps it. This window is what somebody sees on the
/// first launch of the week, not on every launch.
/// </para>
/// </summary>
public partial class LoginViewModel : ViewModelBase
{
    private readonly AuthService _auth;
    private readonly SettingsService _settings;
    private readonly PortalApi _api;

    /// <summary>Raised once, on the UI thread, after the session has been established.</summary>
    public event Action? SignedIn;

    public LoginViewModel(AuthService auth, SettingsService settings, PortalApi api)
    {
        _auth = auth;
        _settings = settings;
        _api = api;
    }

    private string _email = "";
    public string Email { get => _email; set => SetProperty(ref _email, value); }

    /// <summary>
    /// Pushed from the view's <c>PasswordChanged</c> handler. A <c>PasswordBox</c> can't be bound
    /// — WPF deliberately keeps the plaintext out of the binding engine — so this is the seam.
    /// Cleared after every attempt, successful or not; it goes to the portal and nowhere else,
    /// and in particular it is never written to disk.
    /// </summary>
    public string Password { get; set; } = "";

    private string _error = "";
    /// <summary>Why the last attempt failed. Empty when there is nothing to say.</summary>
    public string Error { get => _error; private set => SetProperty(ref _error, value); }

    private bool _showConnection;
    /// <summary>
    /// Whether the address panel is open. Starts open when the app has no portal address, because
    /// in that state the credential fields cannot do anything.
    /// </summary>
    public bool ShowConnection { get => _showConnection; set => SetProperty(ref _showConnection, value); }

    private bool _isConfigured;
    /// <summary>False until the settings file names a portal to sign in against.</summary>
    public bool IsConfigured { get => _isConfigured; private set => SetProperty(ref _isConfigured, value); }

    private AppSettings _connection = new();
    /// <summary>
    /// An editable copy of the settings — never the cached instance, so a half-typed address
    /// doesn't become live for the rest of the app before Save.
    /// </summary>
    public AppSettings Connection { get => _connection; set => SetProperty(ref _connection, value); }

    private string _connectionMessage = "";
    public string ConnectionMessage { get => _connectionMessage; private set => SetProperty(ref _connectionMessage, value); }

    /// <summary>Load the address form and decide whether to open it. Call once, on window load.</summary>
    public async Task InitializeAsync()
    {
        Connection = await _settings.GetForEditAsync();
        IsConfigured = await _api.IsConfiguredAsync();
        ShowConnection = !IsConfigured;
        if (!IsConfigured)
            ConnectionMessage = "No portal address yet. Fill this in, test it, then sign in.";
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
                // A failure that is really about the address should land the user on the panel
                // that fixes it, rather than on a form they will retype correctly and fail again.
                if (!await _api.IsConfiguredAsync()) ShowConnection = true;
                return;
            }
            SignedIn?.Invoke();
        }
        catch (Exception ex)
        {
            // SignInAsync converts everything it expects into a message; anything reaching here is
            // a genuine surprise and still must not take the window down.
            Error = Safe.Redact(ex.Message);
        }
        finally
        {
            // Whatever happened, the plaintext does not stay in memory waiting for the next click.
            Password = "";
            IsBusy = false;
        }
    }

    /// <summary>Persist the address. Shared with Test, which has to save before it probes.</summary>
    [RelayCommand]
    public async Task SaveConnectionAsync()
    {
        await _settings.SaveAsync(Connection);
        // SaveAsync installed this instance as the shared cache; take a fresh copy so continued
        // editing doesn't mutate what everything else is now reading.
        Connection = await _settings.GetForEditAsync();
        IsConfigured = await _api.IsConfiguredAsync();
    }

    /// <summary>
    /// Save the form, then ping the portal. Saving first is not optional: <see cref="PortalApi"/>
    /// reads the address out of the settings file, so a test that skipped the save would probe
    /// whatever was there before and report on an address the user is no longer looking at.
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
            var (ok, message) = await _api.TestAsync();
            ConnectionMessage = ok ? $"Connected — {message}" : message;
        }
        catch (Exception ex)
        {
            ConnectionMessage = Safe.Redact(ex.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public void ToggleConnection() => ShowConnection = !ShowConnection;
}
