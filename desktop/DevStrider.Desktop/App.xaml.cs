using System.Windows;
using DevStrider.Desktop.Data;
using DevStrider.Desktop.Data.Import;
using DevStrider.Desktop.Data.Postgres;
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
            Services = BuildServices();

            // Settings come off disk before anything else, because the login window needs the
            // database credentials that live on them and there is nowhere else to read them from.
            // Run on the pool: blocking the UI thread on an awaited continuation that wants the
            // UI thread back is the classic way to hang before the first window ever appears.
            var settings = Services.GetRequiredService<SettingsService>();
            Task.Run(async () =>
            {
                await settings.LoadAsync();
                await SettingsBootstrap.ApplyAsync(settings);
            }).GetAwaiter().GetResult();

            // Nothing below this line runs without an account. Every repository scopes its queries
            // to SessionContext, and a query issued before login throws rather than quietly
            // reading the whole team's rows.
            var login = new LoginWindow(Services.GetRequiredService<LoginViewModel>());
            MainWindow = login;
            if (login.ShowDialog() != true)
            {
                Shutdown(0);
                return;
            }

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

            // Post-login boot: the profile list and the Bid-Assistant listener. Both need the
            // session, so neither can be started any earlier; both are slow enough that the
            // window should not wait on them.
            _ = Task.Run(StartAfterLoginAsync);
        }
        catch (Exception ex)
        {
            ShowFatal(ex, "Startup failure");
            Shutdown(1);
        }
    }

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<ActivityLogService>();

        // ── settings ────────────────────────────────────────────────────────
        // A file, not a table: it holds the credentials needed to reach the database, so reading
        // it from the database would be circular.
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<SettingsService>();

        // The machine's old local MongoDB, read-only, for the one-time import. Constructed from
        // env vars rather than from settings because SettingsService is what it seeds.
        services.AddSingleton(_ => new LegacyStore(
            SettingsBootstrap.ReadEnv("DEVSTRIDER_MONGO_URI") ?? "mongodb://127.0.0.1:27017",
            SettingsBootstrap.ReadEnv("DEVSTRIDER_DATABASE_NAME") ?? "devstrider"));

        // ── the shared database ─────────────────────────────────────────────
        services.AddSingleton<SharedDbCredentials>();
        services.AddSingleton<SharedDbContext>();
        services.AddSingleton<SessionContext>();
        services.AddSingleton<AuthService>();

        // Repositories are the only thing that talks SQL. Each one reads the account id from
        // SessionContext, so no caller can ask for someone else's rows.
        services.AddSingleton<IAccountRepository, PgAccountRepository>();
        services.AddSingleton<IProfileRepository, PgProfileRepository>();
        services.AddSingleton<IBidRepository, PgBidRepository>();
        services.AddSingleton<IInterviewRepository, PgInterviewRepository>();
        services.AddSingleton<IPeerDirectory, PgPeerDirectory>();

        // ── services ────────────────────────────────────────────────────────
        services.AddSingleton<ProfileService>();      // the ds_users row: goals + account name
        services.AddSingleton<ProfilesService>();     // bidding identities
        services.AddSingleton<ProfileContext>();
        services.AddSingleton<BidBoardService>();
        services.AddSingleton<InterviewService>();
        services.AddSingleton<StatsService>();
        services.AddSingleton<AchievementService>();
        services.AddSingleton<R2StorageService>();
        services.AddSingleton<WordMacroService>();
        services.AddSingleton<LocalApiServer>();

        // ── view-models ─────────────────────────────────────────────────────
        services.AddSingleton<LoginViewModel>();
        services.AddSingleton<BidBoardViewModel>();
        services.AddSingleton<InterviewPanelViewModel>();
        services.AddSingleton<FindBidViewModel>();
        services.AddSingleton<OverviewViewModel>();
        services.AddSingleton<StatsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AboutViewModel>();
        services.AddSingleton<ActivityViewModel>();
        services.AddSingleton<ProfilesViewModel>();
        services.AddSingleton<ProfileViewModel>();
        services.AddSingleton<PeersViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Everything that needs a signed-in account. Failures here are reported to the Activity tab
    /// rather than thrown: a listener that won't bind is a degraded app, not a dead one.
    /// </summary>
    private static async Task StartAfterLoginAsync()
    {
        var activity = Services.GetRequiredService<ActivityLogService>();
        try
        {
            var profiles = Services.GetRequiredService<ProfilesService>();

            // An account signing in for the first time has no bidding identity yet, and every
            // profile-scoped screen would come up empty with no way to fix it — the Profiles tab
            // can create one, but nothing tells you that is what's wrong. Seed one instead.
            if ((await profiles.ListAsync()).Count == 0)
            {
                var seeded = await profiles.CreateAsync("Default");
                activity.Info("Profiles", "Default profile created", seeded.Name);
            }

            await Services.GetRequiredService<ProfileContext>().InitAsync();

            var settings = await Services.GetRequiredService<SettingsService>().GetAsync();
            var server = Services.GetRequiredService<LocalApiServer>();
            await Current.Dispatcher.InvokeAsync(() => server.Start(settings.ListenerPort));
            if (server.IsRunning)
                activity.Success("Listener", "Listener started", $"Listening on http://127.0.0.1:{server.BoundPort}");
            else
                activity.Error("Listener", "Listener failed to start", server.Status);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Post-login boot failed: " + ex.Message);
            activity.Error("Startup", "Post-login boot crashed", SharedDbCredentials.Redact(ex.Message));
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
        // Watchdog: if cleanup itself hangs (slow database write, stuck FileSystemWatcher
        // dispose, etc.), force-terminate after 3 seconds. Runs on a thread-pool thread
        // so the timer fires even if the UI thread is stuck inside StopAsync.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            try { System.Diagnostics.Process.GetCurrentProcess().Kill(); }
            catch { /* already exiting */ }
        });

        try
        {
            var server = Services?.GetService(typeof(LocalApiServer)) as LocalApiServer;
            server?.StopAsync().GetAwaiter().GetResult();
        }
        catch { /* shutting down anyway */ }
        try
        {
            // Must run before the Kill() below: the warm Word instance is invisible, so an
            // orphaned WINWORD.EXE is one the user can only find in Task Manager.
            (Services?.GetService(typeof(WordMacroService)) as WordMacroService)?.Dispose();
        }
        catch { /* ignore */ }
        Tray?.Dispose();
        Tray = null;
        base.OnExit(e);

        // Hard-kill instead of Environment.Exit — the latter waits on managed finalizers
        // (SkiaSharp's GL context, Npgsql's TCP pool, native COM teardown) and can hang
        // indefinitely. Kill() terminates the process immediately, no finalizer wait.
        // This is the normal path; the watchdog above is the safety net for the unhappy one.
        System.Diagnostics.Process.GetCurrentProcess().Kill();
    }
}
