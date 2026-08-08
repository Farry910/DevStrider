using System.Windows;
using System.Windows.Controls;
using DevStrider.Desktop.ViewModels;

namespace DevStrider.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        if (App.Services != null)
            DataContext = App.Services.GetService(typeof(SettingsViewModel));
    }

    /// <summary>
    /// Hand the typed password to the view-model. <see cref="PasswordBox.Password"/> isn't a
    /// DependencyProperty — by design, so the plaintext never enters the binding engine — which
    /// is why this has to be code-behind rather than a binding. The view-model holds it only
    /// until Save encrypts it; nothing persists the cleartext.
    /// </summary>
    private void SharedMongoPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && sender is PasswordBox box)
            vm.SharedMongoPasswordEntry = box.Password;
    }

    /// <summary>Same contract as above, for the R2 secret access key.</summary>
    private void R2SecretBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && sender is PasswordBox box)
            vm.R2SecretEntry = box.Password;
    }
}
