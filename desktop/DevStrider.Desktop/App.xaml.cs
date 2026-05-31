using System.Windows;
using System.Windows.Threading;
using DevStrider.Desktop.Data;
using DevStrider.Desktop.Services;
using DevStrider.Desktop.ViewModels;
using DevStrider.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DevStrider.Desktop;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = default!;

    /// <summary>Lives for the whole process. Holds the tray icon and the Quit handler.</summary>
    public static TrayService? Tray { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Surface any otherwise-silent crash from async void handlers / background tasks.
        DispatcherUnhandledException += (_, args) =>
        {
            ShowFatal(args.Exception, "Dispatcher exception");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                ShowFatal(ex, "Domain exception");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ShowFatal(args.Exception, "Background task exception");
            args.SetObserved();
        };

        try
        {
            var services = new ServiceCollection();
            services.AddSingleton(_ => new MongoContext(
                "mongodb://127.0.0.1:27017", "devstrider"));

            services.AddSingleton<SettingsService>();
            // ProfileService is kept (no longer surfaced in the UI but still used by
            // GitHubSyncService for the local username + by older code paths).
            services.AddSingleton<ProfileService>();
            services.AddSingleton<BidBoardService>();
            services.AddSingleton<InterviewService>();
            services.AddSingleton<StatsService>();
            services.AddSingleton<AchievementService>();
            services.AddSingleton<ExportService>();
            services.AddSingleton<ResumeService>();
            services.AddSingleton<GitHubSyncService>();
            services.AddSingleton<LocalApiServer>();

            services.AddSingleton<BidBoardViewModel>();
            services.AddSingleton<InterviewPanelViewModel>();
            services.AddSingleton<OverviewViewModel>();
            services.AddSingleton<StatsViewModel>();
            services.AddSingleton<ResumesViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<ImportViewModel>();
            services.AddSingleton<MainWindowViewModel>();

            Services = services.BuildServiceProvider();

            // Fire-and-forget index creation so a missing/unreachable MongoDB doesn't block
            // the window from showing. The user sees a clear MessageBox if it fails.
            _ = Task.Run(async () =>
            {
                try
                {
                    var db = Services.GetRequiredService<MongoContext>();
                    await db.EnsureIndexesAsync();
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => MessageBox.Show(
                        $"Couldn't reach MongoDB at mongodb://127.0.0.1:27017.\n\n" +
                        $"Make sure the MongoDB service is running (Services.msc → MongoDB → Start).\n\n" +
                        $"Until that's fixed, every page will show an error when it loads.\n\n" +
                        $"Details: {ex.Message}",
                        "DevStrider · MongoDB unreachable",
                        MessageBoxButton.OK, MessageBoxImage.Warning));
                }
            });

            // Start the Bid-Assistant HTTP listener if the saved settings have it enabled.
            // Loopback-only, no auth — the extension POSTs bids straight to /record-bid here.
            _ = Task.Run(async () =>
            {
                try
                {
                    var settingsService = Services.GetRequiredService<SettingsService>();
                    var settings = await settingsService.GetAsync();
                    if (settings.ListenerEnabled)
                    {
                        var server = Services.GetRequiredService<LocalApiServer>();
                        Dispatcher.Invoke(() => server.Start(settings.ListenerPort));
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Listener boot failed: " + ex.Message);
                }
            });

            var window = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>()
            };
            MainWindow = window;

            // Tray service knows how to fetch the live MainWindow (it may be hidden when
            // the user clicks the X). Created before Show() so the icon is present from
            // the moment the user can interact with the app.
            Tray = new TrayService(() => MainWindow);

            window.Show();
        }
        catch (Exception ex)
        {
            ShowFatal(ex, "Startup failure");
            Shutdown(1);
        }
    }

    private static void ShowFatal(Exception ex, string title)
    {
        MessageBox.Show(
            $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
            $"DevStrider · {title}",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            var server = Services?.GetService(typeof(LocalApiServer)) as LocalApiServer;
            server?.StopAsync().GetAwaiter().GetResult();
        }
        catch { /* shutting down anyway */ }
        Tray?.Dispose();
        Tray = null;
        base.OnExit(e);
    }
}
