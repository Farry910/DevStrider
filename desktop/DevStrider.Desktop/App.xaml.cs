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
            // MongoContext is constructed before SettingsService can read anything from Mongo,
            // so env-var overrides for the connection itself have to be consulted here directly.
            // SettingsBootstrap below mirrors the same values into AppSettings so the Settings
            // UI shows what's actually in use.
            var mongoUri = SettingsBootstrap.ReadEnv("DEVSTRIDER_MONGO_URI") ?? "mongodb://127.0.0.1:27017";
            var mongoDb  = SettingsBootstrap.ReadEnv("DEVSTRIDER_DATABASE_NAME") ?? "devstrider";
            services.AddSingleton(_ => new MongoContext(mongoUri, mongoDb));

            services.AddSingleton<SettingsService>();
            services.AddSingleton<ProfileService>();      // singleton Username row (legacy name)
            services.AddSingleton<ProfilesService>();     // multi-profile CRUD
            services.AddSingleton<ProfileContext>();
            services.AddSingleton<ProfileMigrationService>();
            services.AddSingleton<BidBoardService>();
            services.AddSingleton<InterviewService>();
            services.AddSingleton<StatsService>();
            services.AddSingleton<AchievementService>();
            services.AddSingleton<ResumeService>();
            services.AddSingleton<ActivityLogService>();
            services.AddSingleton<RegistryStore>();
            services.AddSingleton<RegistrySyncService>();
            services.AddSingleton<AtlasContext>();
            services.AddSingleton<AtlasSyncService>();
            services.AddSingleton<LocalApiServer>();
            services.AddSingleton<ResumeAutoIngestService>();

            services.AddSingleton<BidBoardViewModel>();
            services.AddSingleton<InterviewPanelViewModel>();
            services.AddSingleton<FindBidViewModel>();
            services.AddSingleton<OverviewViewModel>();
            services.AddSingleton<StatsViewModel>();
            services.AddSingleton<SharingViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<AboutViewModel>();
            services.AddSingleton<ActivityViewModel>();
            services.AddSingleton<ProfilesViewModel>();
            services.AddSingleton<PeersViewModel>();
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

            // Seed empty/default settings from DEVSTRIDER_* env vars on first launch, then
            // start the Bid-Assistant HTTP listener. Bootstrap runs before the listener boots
            // so a seeded port takes effect immediately. Loopback-only, no auth.
            _ = Task.Run(async () =>
            {
                var activity = Services.GetRequiredService<ActivityLogService>();
                try
                {
                    var settingsService = Services.GetRequiredService<SettingsService>();
                    var profileService = Services.GetRequiredService<ProfileService>();
                    await SettingsBootstrap.ApplyAsync(settingsService, profileService);

                    // Multi-profile migration: seeds a Default profile + backfills ProfileId
                    // on legacy data. Idempotent — no-op once the seed exists.
                    var migration = Services.GetRequiredService<ProfileMigrationService>();
                    await migration.RunAsync();
                    var profileContext = Services.GetRequiredService<ProfileContext>();
                    await profileContext.InitAsync();

                    // Registry sync runs AFTER bootstrap + profile init: if env vars seeded a
                    // value but registry already has a different one, registry wins (long-lived).
                    var registrySync = Services.GetRequiredService<RegistrySyncService>();
                    await registrySync.InitialSyncAsync();

                    var settings = await settingsService.GetAsync();
                    var server = Services.GetRequiredService<LocalApiServer>();
                    Dispatcher.Invoke(() => server.Start(settings.ListenerPort));
                    if (server.IsRunning)
                        activity.Success("Listener", "Listener started", $"Listening on http://127.0.0.1:{server.BoundPort}");
                    else
                        activity.Error("Listener", "Listener failed to start", server.Status);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Listener boot failed: " + ex.Message);
                    activity.Error("Listener", "Listener boot crashed", ex.Message);
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

            // Fan out every Activity entry to the tray as a balloon. Silent entries are
            // logged in the Activity tab but suppressed from notifications (e.g. paste-submit,
            // which fires on every Ctrl+V and would spam the user).
            var activityLog = Services.GetRequiredService<ActivityLogService>();
            activityLog.OnEntry += entry =>
            {
                if (entry.Silent) return;
                Tray?.ShowBalloon(entry.Title, entry.Detail, entry.Level);
            };

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
        try
        {
            (Services?.GetService(typeof(ResumeAutoIngestService)) as ResumeAutoIngestService)?.Dispose();
        }
        catch { /* ignore */ }
        Tray?.Dispose();
        Tray = null;
        base.OnExit(e);

        // Belt-and-braces: WPF's Shutdown() unwinds the dispatcher but leaves background
        // threads (LiveCharts/SkiaSharp render thread, MongoClient's connection pool, the
        // FileSystemWatcher in ResumeAutoIngestService, etc.) alive. Without this the
        // tray icon disappears but DevStrider.exe lingers in Task Manager, which then
        // blocks the next `dotnet run` with a file-lock on bin\…\DevStrider.exe.
        Environment.Exit(e.ApplicationExitCode);
    }
}
