using System.Windows;
using DevStrider.Desktop.Data;
using DevStrider.Desktop.Data.Http;
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
            // portal address that lives on them and there is nowhere else to read it from.
            // Run on the pool: blocking the UI thread on an awaited continuation that wants the
            // UI thread back is the classic way to hang before the first window ever appears.
            var settings = Services.GetRequiredService<SettingsService>();
            var auth = Services.GetRequiredService<AuthService>();

            // …and then the saved session, if last week's sign-in is still good. This is the whole
            // of what the token bought: the app opens on the bid board rather than on a password
            // box, every day for a week. A portal that can't be reached is not a rejected session
            // — see AuthService.RestoreAsync.
            var restored = Task.Run(async () =>
            {
                await settings.LoadAsync();
                await SettingsBootstrap.ApplyAsync(settings);
                return await auth.RestoreAsync();
            }).GetAwaiter().GetResult();

            // Word left running by an earlier session belongs to nobody and needs no account to
            // clear, so it happens before the login gate. Resolving the service is all that is
            // needed: its constructor starts the COM thread and kicks off the sweep on a
            // thread-pool thread. Calling EnsureSingleWordInstance here as well ran the same sweep
            // twice over the same processes, and ran the second one *on the UI thread* — which is
            // the one thing the constructor's own comment says it is backgrounded to avoid.
            Services.GetRequiredService<WordMacroService>();

            // With developer tools on, the listener opens here rather than after sign-in, so /dev
            // can see a login window that never got past itself — which is exactly the state worth
            // being able to look at. The endpoints that act as the signed-in user refuse until
            // there is one; see LocalApiServer.HandleAsync. Off, this does nothing and the listener
            // starts after login as it always has.
            if (settings.Current is { DeveloperTools: true } devSettings)
            {
                var early = Services.GetRequiredService<LocalApiServer>();
                early.Start(devSettings.ListenerPort);
                Services.GetRequiredService<ActivityLogService>().Info("Listener",
                    early.IsRunning ? "Developer listener started early" : "Developer listener could not start early",
                    early.Status, silent: true);
            }

            // Nothing below this line runs without an account. Every repository scopes its calls
            // to SessionContext, and one issued before login throws rather than quietly asking the
            // portal for whatever an unauthenticated request would return.
            if (!restored)
            {
                var login = new LoginWindow(Services.GetRequiredService<LoginViewModel>());
                MainWindow = login;
                if (login.ShowDialog() != true)
                {
                    Shutdown(0);
                    return;
                }
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
        services.AddSingleton<BidTraceService>();

        // ── settings ────────────────────────────────────────────────────────
        // A file, not a table: it holds the credentials needed to reach the database, so reading
        // it from the database would be circular.
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<SettingsService>();

        // ── the company portal ──────────────────────────────────────────────
        // The app's only store, reached over HTTP. It used to be a PostgreSQL connection opened
        // from here with a password held on this machine; there is no database credential in this
        // process any more, and no SQL either.
        services.AddSingleton<PortalApi>();
        services.AddSingleton<SessionContext>();
        services.AddSingleton<SessionStore>();
        services.AddSingleton<AuthService>();

        // Repositories are the only thing that calls the portal's data endpoints. Each reads the
        // account from SessionContext to fail fast when nothing is signed in; the server pins
        // every row to the token's own user id, so no caller can ask for someone else's.
        services.AddSingleton<IAccountRepository, ApiAccountRepository>();
        services.AddSingleton<IProfileRepository, ApiProfileRepository>();
        services.AddSingleton<IBidRepository, ApiBidRepository>();
        services.AddSingleton<IInterviewRepository, ApiInterviewRepository>();
        services.AddSingleton<IPeerDirectory, ApiPeerDirectory>();
        services.AddSingleton<IPersonFactRepository, ApiPersonFactRepository>();

        // ── services ────────────────────────────────────────────────────────
        services.AddSingleton<ProfileService>();      // the ds_users row: the account name
        services.AddSingleton<ProfilesService>();     // bidding identities
        services.AddSingleton<ProfileContext>();
        services.AddSingleton<PendingBidQueue>();
        services.AddSingleton<BidBoardService>();
        services.AddSingleton<FolderBidImport>();
        services.AddSingleton<InterviewService>();
        services.AddSingleton<StatsService>();
        services.AddSingleton<PersonFactsService>();
        services.AddSingleton<QuickAnswerService>();
        services.AddSingleton<R2StorageService>();
        services.AddSingleton<ChatGptAccountService>();
        services.AddSingleton<ChatGptConversationRegistry>();
        services.AddSingleton<WordMacroService>();
        services.AddSingleton<DevBridge>();
        services.AddSingleton<DevEndpoints>();
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
        services.AddSingleton<PeersViewModel>();
        // Two Resume Studio workspaces, one per lane. They differ only in which ChatGPT account
        // their browser signs in as and which conversation each remembers — everything else is the
        // same driver. Registered explicitly rather than by type because the lane is a constructor
        // argument, and a container that resolved one of them by type would hand out whichever was
        // registered last to both.
        services.AddSingleton<ResumeStudioWorkspaces>(provider => new ResumeStudioWorkspaces(
            Auto: ActivateResumeStudio(provider, ChatGptLanes.Auto),
            Manual: ActivateResumeStudio(provider, ChatGptLanes.Manual)));
        services.AddSingleton<QuickAnswersViewModel>();
        services.AddSingleton<AssistedAutomationViewModel>();
        services.AddSingleton<JobBrowserViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Builds one Resume Studio for a lane, pulling its dependencies from the container and passing
    /// the lane by hand — the one argument the container cannot supply, because it is what tells
    /// the two instances apart.
    /// </summary>
    private static ResumeStudioViewModel ActivateResumeStudio(IServiceProvider provider, string lane) =>
        new(provider.GetRequiredService<SettingsService>(),
            provider.GetRequiredService<ProfileContext>(),
            provider.GetRequiredService<BidBoardService>(),
            provider.GetRequiredService<WordMacroService>(),
            provider.GetRequiredService<ActivityLogService>(),
            provider.GetRequiredService<BidTraceService>(),
            provider.GetRequiredService<ChatGptAccountService>(),
            provider.GetRequiredService<ChatGptConversationRegistry>(),
            lane);

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
            var settings = await Services.GetRequiredService<SettingsService>().GetAsync();

            StartSessionRenewal();

            // Login already wrote this row. Checking again costs one request and covers the gap
            // where the schema was re-applied between the sign-in and here — without it, the first
            // symptom is a foreign-key violation on whatever the user touches first.
            await Services.GetRequiredService<ProfileService>().EnsureRowAsync();

            // An account signing in for the first time has no bidding identity yet, and every
            // profile-scoped screen would come up empty with no way to fix it — the Profiles tab
            // can create one, but nothing tells you that is what's wrong. Seed one instead, and
            // carry the machine-level Word path onto it: that setting predates profiles and is
            // still what DEVSTRIDER_WORD_DOC_PATH seeds, so this is where it lands.
            if ((await profiles.ListAsync()).Count == 0)
            {
                var seeded = await profiles.CreateAsync("Default", settings.WordDocPath);
                activity.Info("Profiles", "Default profile created", seeded.Name);
            }

            await Services.GetRequiredService<ProfileContext>().InitAsync();

            // Anything a previous run couldn't send is on disk. Recover it before the listener
            // opens, so a crashed session's bids are in the database before new ones arrive.
            var pending = Services.GetRequiredService<PendingBidQueue>();
            await pending.RestoreAsync();
            pending.Start();

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
            activity.Error("Startup", "Post-login boot crashed", Safe.Redact(ex.Message));
        }
    }

    /// <summary>
    /// Keep the week from running out under a window that never closes.
    ///
    /// <para>
    /// Startup renews a token that is inside its last day, which covers everybody who quits the
    /// app at some point. It does not cover the machine that is never restarted — and that is the
    /// one where a token quietly reaching its expiry turns the next ordinary save into a 401 for
    /// no reason the user can see. Six hours is often enough that the last-day window can never be
    /// missed, and rare enough to be free.
    /// </para>
    ///
    /// <para>
    /// A refresh that fails is not reported: the machine is offline, or the portal is down, and
    /// the token in hand is still good until it isn't. The next tick tries again.
    /// </para>
    /// </summary>
    private static void StartSessionRenewal()
    {
        var session = Services.GetRequiredService<SessionContext>();
        var auth = Services.GetRequiredService<AuthService>();

        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromHours(6));
                try
                {
                    if (session.NeedsRefresh) await auth.RefreshAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"session renewal: {Safe.Redact(ex.Message)}");
                }
            }
        });
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
        // dispose, etc.), force-terminate. Runs on a thread-pool thread so the timer fires even
        // if the UI thread is stuck inside StopAsync. Ten seconds rather than three because the
        // final bid flush below is a network round-trip — and losing that race costs nothing,
        // since anything unsent is still in pending-bids-<id>.json for the next launch.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            try { System.Diagnostics.Process.GetCurrentProcess().Kill(); }
            catch { /* already exiting */ }
        });

        try
        {
            // First, before anything that can hang: get queued bids into the database while there
            // is still a process to do it with.
            (Services?.GetService(typeof(PendingBidQueue)) as PendingBidQueue)
                ?.FlushOnExit(TimeSpan.FromSeconds(5));
        }
        catch { /* the journal on disk is the fallback */ }

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
        // (SkiaSharp's GL context, HttpClient's connection pool, native COM teardown) and can hang
        // indefinitely. Kill() terminates the process immediately, no finalizer wait.
        // This is the normal path; the watchdog above is the safety net for the unhappy one.
        System.Diagnostics.Process.GetCurrentProcess().Kill();
    }
}
