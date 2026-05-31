using System.ComponentModel;
using System.Windows;
using DevStrider.Desktop.ViewModels;

namespace DevStrider.Desktop.Views;

public partial class MainWindow : Window
{
    /// <summary>Segoe MDL2 Assets glyph codepoints for the caption buttons.</summary>
    private const string GlyphMaximize = "\uE922";
    private const string GlyphRestore  = "\uE923";

    /// <summary>
    /// Flips to true only when the user picked Quit from the tray menu \u2014 see App.Tray.Quit().
    /// Used by OnClosing to know "this is a real shutdown, let it through" instead of the
    /// default "hide to tray, keep the process alive" behaviour.
    /// </summary>
    public static bool AllowRealClose { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        StateChanged += (_, __) =>
        {
            SyncMaximizeGlyph();
            SyncRoundedCorners();
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The user's title-bar X button + Alt+F4 + System Menu Close all funnel through
        // here. Cancel them and hide-to-tray; Quit from the tray menu has already set
        // AllowRealClose so it can pass through.
        if (!AllowRealClose)
        {
            e.Cancel = true;
            HideToTray();
        }
        base.OnClosing(e);
    }

    private void HideToTray()
    {
        // ShowInTaskbar=false makes the taskbar entry disappear; Hide() removes the visible
        // window. The NotifyIcon stays alive in App.Tray and is the user's way back in.
        ShowInTaskbar = false;
        Hide();
    }

    private void SyncMaximizeGlyph()
    {
        MaxBtn.Content = WindowState == WindowState.Maximized ? GlyphRestore : GlyphMaximize;
        MaxBtn.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
    }

    /// <summary>
    /// 5-px rounded corners look great in the normal state but leave dead transparent pixels
    /// at the screen corners when maximized (the window touches the monitor edge). Flatten
    /// to 0 while maximized; restore on return to normal.
    /// </summary>
    private void SyncRoundedCorners()
    {
        var max = WindowState == WindowState.Maximized;
        RootBorder.CornerRadius     = max ? new CornerRadius(0) : new CornerRadius(5);
        TitleBarBorder.CornerRadius = max ? new CornerRadius(0) : new CornerRadius(5, 5, 0, 0);
        SidebarBorder.CornerRadius  = max ? new CornerRadius(0) : new CornerRadius(0, 0, 0, 5);
        ContentBorder.CornerRadius  = max ? new CornerRadius(0) : new CornerRadius(0, 0, 5, 0);
        // Drop the 1-px outer rim too — otherwise it shows along the screen edges.
        RootBorder.BorderThickness  = max ? new Thickness(0) : new Thickness(1);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    /// <summary>
    /// X button in the title bar. Goes through Close() which fires OnClosing, where we
    /// cancel and redirect to HideToTray. AllowRealClose stays false here.
    /// </summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        // Each screen's load is independent — wrap individually so one broken page (e.g. mongo
        // down) doesn't take down the others.
        await TryAsync("Resumes",     () => vm.Resumes.ReloadAsync());
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
