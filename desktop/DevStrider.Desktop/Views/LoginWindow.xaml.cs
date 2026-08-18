using System.Windows;
using System.Windows.Controls;
using DevStrider.Desktop.ViewModels;

namespace DevStrider.Desktop.Views;

/// <summary>
/// The sign-in dialog. Shown modally by <see cref="App"/> before the main window is built, so
/// that every repository behind it has an account to scope to.
///
/// <para>
/// <c>DialogResult</c> is the answer: true only when a session was established. Closing the
/// window any other way — the X, Esc, Alt+F4 — leaves it false and the app exits, which is the
/// correct outcome for an app whose every screen is a query against the shared database.
/// </para>
/// </summary>
public partial class LoginWindow : Window
{
    private readonly LoginViewModel _vm;

    public LoginWindow(LoginViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _vm.SignedIn += OnSignedIn;
        Loaded += OnLoaded;
        Closed += (_, _) => _vm.SignedIn -= OnSignedIn;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        EmailBox.Focus();
        try
        {
            await _vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            // An unreadable settings file must not leave a blank window with no explanation.
            MessageBox.Show(this,
                $"Couldn't read the saved connection settings.\n\n{ex.Message}",
                "DevStrider", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnSignedIn()
    {
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// A <c>PasswordBox</c> keeps its plaintext out of the binding engine on purpose, so the value
    /// is handed over here instead. The box is cleared after each attempt by the view-model
    /// resetting its own copy; this pushes whatever is currently typed.
    /// </summary>
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box) _vm.Password = box.Password;
    }

    private void OnDbPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box) _vm.SharedDbPasswordEntry = box.Password;
    }
}
