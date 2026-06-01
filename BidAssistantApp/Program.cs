using System.Runtime.InteropServices;
using System.Diagnostics;

namespace BidAssistantApp;

static class Program
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr hProcess, int processInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE processInformation, uint processInformationSize);

    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    private static HttpServer? _server;
    private static NotifyIcon? _trayIcon;
    private static SynchronizationContext? _syncContext;

    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        DisablePowerThrottling();

        var host = Environment.GetEnvironmentVariable("BID_ASSISTANT_HOST") ?? "127.0.0.1";
        var portStr = Environment.GetEnvironmentVariable("BID_ASSISTANT_PORT") ?? "8765";
        var port = int.TryParse(portStr, out var p) ? p : 8765;

        Logger.Info($"Bid Assistant v2.0.0 starting on http://{host}:{port} (local only)");
        Logger.Info("Word path and hotkey are set in the extension popup.");
        Logger.Info("Endpoints: POST /trigger-paste-submit, POST /refresh-word, POST /record-devstrider (forwards Bearer JWT), GET /browse-word, GET /health, GET /metrics");

        _trayIcon = new NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "Bid Assistant",
            Visible = true
        };
        // Click fires on some Windows builds for right-clicks too; use MouseClick + left only.
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OpenDashboard(host, port);
        };

        var menu = new ContextMenuStrip();
        var dashboardItem = new ToolStripMenuItem("Open Dashboard");
        dashboardItem.Click += (_, _) => OpenDashboard(host, port);
        menu.Items.Add(dashboardItem);
        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => Exit();
        menu.Items.Add(exitItem);
        _trayIcon.ContextMenuStrip = menu;

        var syncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _syncContext = syncContext;
        _server = new HttpServer(host, port, syncContext);
        _server.Start();

        Logger.Info("Server started successfully. Left-click tray icon for dashboard; right-click for menu.");

        Application.Run();
    }

    private static Icon CreateTrayIcon()
    {
        try
        {
            // Try embedded .ico first (bundled in the exe)
            using var icoStream = typeof(Program).Assembly.GetManifestResourceStream("BidAssistantApp.icon.ico");
            if (icoStream != null)
                return new Icon(icoStream);

            // Try file on disk next
            var iconPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
            if (File.Exists(iconPath))
                return new Icon(iconPath);

            // Try embedded .png fallback
            using var pngStream = typeof(Program).Assembly.GetManifestResourceStream("BidAssistantApp.icon.png");
            if (pngStream != null)
                return IconFromPngStream(pngStream);
        }
        catch (Exception ex) { Logger.Warning($"Tray icon load failed: {ex.Message}"); }

        return SystemIcons.Application;
    }

    private static Icon IconFromPngStream(Stream stream)
    {
        using var bmp = new Bitmap(stream);
        return IconFromBitmap(bmp);
    }

    private static Icon IconFromBitmap(Bitmap bmp)
    {
        var hIcon = bmp.GetHicon();
        try
        {
            var icon = Icon.FromHandle(hIcon);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static void DisablePowerThrottling()
    {
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = 0
            };
            var size = (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>();
            SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, size);
        }
        catch (Exception ex)
        {
            Logger.Warning($"Could not disable power throttling: {ex.Message}");
        }
    }

    private static void Exit()
    {
        Logger.Info("Application exiting...");
        _server?.Stop();
        _trayIcon?.Dispose();
        Application.Exit();
    }

    private static void OpenDashboard(string host, int port)
    {
        try
        {
            var url = $"http://{host}:{port}/dashboard";
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Warning($"Could not open dashboard: {ex.Message}");
        }
    }

    /// <summary>
    /// Shows a Windows tray balloon notification. Safe to call from any thread.
    /// </summary>
    public static void ShowTrayNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info, int timeoutMs = 4000)
    {
        if (_trayIcon == null) return;
        var tray = _trayIcon; // capture for closure — null already checked above
        try
        {
            void Show()
            {
                tray.BalloonTipTitle = title;
                tray.BalloonTipText = message;
                tray.BalloonTipIcon = icon;
                tray.ShowBalloonTip(timeoutMs);
            }

            // NotifyIcon must be touched on the UI (STA/WinForms) thread.
            if (_syncContext != null)
                _syncContext.Post(_ => Show(), null);
            else
                Show();
        }
        catch (Exception ex)
        {
            Logger.Warning($"Tray notification failed: {ex.Message}");
        }
    }
}
