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
    /// Hand the typed R2 secret to the view-model. <see cref="PasswordBox.Password"/> isn't a
    /// DependencyProperty — by design, so the plaintext never enters the binding engine — which
    /// is why this has to be code-behind rather than a binding. The view-model holds it until
    /// Save writes it to the settings file.
    ///
    /// <para>
    /// This is the last of these handlers. The other was the shared PostgreSQL password, and it
    /// went when the app stopped opening its own database connection.
    /// </para>
    /// </summary>
    private void R2SecretBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && sender is PasswordBox box)
            vm.R2SecretEntry = box.Password;
    }
}
