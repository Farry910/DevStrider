using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;
using ContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using MouseEventArgs = System.Windows.Forms.MouseEventArgs;
using ToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Owns the single system-tray <see cref="NotifyIcon"/> for the app's lifetime.
/// Created at <c>App.OnStartup</c>, disposed on <c>App.OnExit</c>. The tray icon is the user's
/// only way to bring the window back once they close it (the X button now hides instead of
/// closing) and the only way to actually quit the process.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Func<Window?> _getWindow;
    private bool _disposed;

    public TrayService(Func<Window?> getWindow)
    {
        _getWindow = getWindow;

        _notifyIcon = new NotifyIcon
        {
            Text = "DevStrider",
            Visible = true,
            Icon = LoadIcon()
        };

        var menu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("Show DevStrider");
        showItem.Click += (_, __) => Restore();
        var quitItem = new ToolStripMenuItem("Quit");
        quitItem.Click += (_, __) => Quit();
        menu.Items.Add(showItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quitItem);
        _notifyIcon.ContextMenuStrip = menu;

        // Double-click is the Windows convention to restore from the notification area.
        _notifyIcon.DoubleClick += (_, __) => Restore();
        // Single left-click feels natural too — many modern apps support both.
        _notifyIcon.MouseClick += OnMouseClick;
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) Restore();
    }

    /// <summary>Show the window and re-add it to the taskbar.</summary>
    public void Restore()
    {
        var w = _getWindow();
        if (w == null) return;
        w.Dispatcher.Invoke(() =>
        {
            w.Show();
            w.ShowInTaskbar = true;
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
            w.Topmost = true;       // briefly, so it pops to front…
            w.Topmost = false;      // …then drop the topmost flag.
            w.Focus();
        });
    }

    /// <summary>Real shutdown — disposes the icon and tears the process down.</summary>
    public void Quit()
    {
        _notifyIcon.Visible = false;
        Application.Current.Dispatcher.Invoke(() =>
        {
            // MainWindow.OnClosing redirects close → HideToTray unless this flag is set.
            // Flipping it before Shutdown() lets the window genuinely close on its way out.
            Views.MainWindow.AllowRealClose = true;
            Application.Current.Shutdown();
        });
    }

    private static Icon LoadIcon()
    {
        // The .ico is a packed Resource (<Resource Include="Assets\DevStrider.ico"/> in csproj),
        // so we load it via pack URI rather than a file path — works in both `dotnet run` and a
        // single-file publish.
        var uri = new Uri("pack://application:,,,/Assets/DevStrider.ico", UriKind.Absolute);
        using var stream = Application.GetResourceStream(uri)?.Stream
            ?? throw new FileNotFoundException("Assets/DevStrider.ico not found in resources.");
        return new Icon(stream);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
    }
}
