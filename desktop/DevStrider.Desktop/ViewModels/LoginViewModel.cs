using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// The sign-in form. Runs before the main window exists and is the only thing that puts an
/// account into <see cref="SessionContext"/>.
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
    /// Cleared after every attempt, successful or not; it goes to the portal and nowhere else,
    /// and in particular it is never written to disk.
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
            Error = Safe.Redact(ex.Message);
        }
        finally
        {
            // Whatever happened, the plaintext does not stay in memory waiting for the next click.
            Password = "";
            IsBusy = false;
        }
    }
}
