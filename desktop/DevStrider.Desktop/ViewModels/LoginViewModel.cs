using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// The sign-in form. Runs before the main window exists and is the only thing that puts an
/// account into <see cref="SessionContext"/> when a saved session couldn't be restored silently
/// (see <see cref="AuthService.TryRestoreSessionAsync"/>, called by <c>App</c> before this window
/// is even shown).
/// </summary>
public partial class LoginViewModel : ViewModelBase
{
    private readonly AuthService _auth;

    /// <summary>Raised once, on the UI thread, after the session has been established.</summary>
    public event Action? SignedIn;

    public LoginViewModel(AuthService auth)
    {
        _auth = auth;
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
                return;
            }
            SignedIn?.Invoke();
        }
        catch (Exception ex)
        {
            // SignInAsync converts everything it expects into a message; anything reaching here is
            // a genuine surprise and still must not take the window down.
            Error = ex.Message;
        }
        finally
        {
            // Whatever happened, the plaintext does not stay in memory waiting for the next click.
            Password = "";
            IsBusy = false;
        }
    }
}
