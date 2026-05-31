using System.Windows;
using DevStrider.Desktop.ViewModels;

namespace DevStrider.Desktop.Views;

public partial class MainWindow : Window
{
    /// <summary>Segoe MDL2 Assets glyph codepoints for the caption buttons.</summary>
    private const string GlyphMaximize = "\uE922";
    private const string GlyphRestore  = "\uE923";

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        StateChanged += (_, __) => SyncMaximizeGlyph();
    }

    private void SyncMaximizeGlyph()
    {
        MaxBtn.Content = WindowState == WindowState.Maximized ? GlyphRestore : GlyphMaximize;
        MaxBtn.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        // Each screen's load is independent — wrap individually so one broken page (e.g. mongo
        // down) doesn't take down the others.
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
