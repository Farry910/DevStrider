using System.Windows;
using DevStrider.Desktop.ViewModels;

namespace DevStrider.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        // Each screen's load is independent — wrap individually so one broken page (e.g. mongo
        // down) doesn't take down the others. Errors land on each VM's StatusMessage and the
        // shell still navigates fine.
        await TryAsync("Profile",     () => vm.Profile.LoadAsync());
        await TryAsync("Settings",    () => vm.Settings.LoadAsync());
        await TryAsync("Bids",        () => vm.Bids.ReloadAsync());
        await TryAsync("Interviews",  () => vm.Interviews.ReloadAsync());
        await TryAsync("Overview",    () => vm.Overview.ReloadAsync());
        await TryAsync("Stats",       () => vm.Stats.ReloadAsync());
        await TryAsync("Import",      () => vm.Import.LoadAsync());

        async Task TryAsync(string label, Func<Task> work)
        {
            try { await work(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[{label}] {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
