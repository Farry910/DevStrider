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
            services.AddSingleton<ProfileService>();
            services.AddSingleton<BidBoardService>();
            services.AddSingleton<InterviewService>();
            services.AddSingleton<StatsService>();
            services.AddSingleton<AchievementService>();
            services.AddSingleton<ExportService>();
            services.AddSingleton<GitHubSyncService>();

            services.AddSingleton<BidBoardViewModel>();
            services.AddSingleton<InterviewPanelViewModel>();
            services.AddSingleton<OverviewViewModel>();
            services.AddSingleton<StatsViewModel>();
            services.AddSingleton<ProfileViewModel>();
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

            var window = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>()
            };
            MainWindow = window;
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
}
