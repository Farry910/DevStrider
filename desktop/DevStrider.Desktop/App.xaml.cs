using System.Windows;
using DevStrider.Desktop.Data;
using DevStrider.Desktop.Services;
using DevStrider.Desktop.ViewModels;
using DevStrider.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DevStrider.Desktop;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = default!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        // One-shot index creation + connection check.
        var db = Services.GetRequiredService<MongoContext>();
        try
        {
            await db.EnsureIndexesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Couldn't reach MongoDB at mongodb://127.0.0.1:27017.\n\nStart the service " +
                $"(net start MongoDB) and relaunch.\n\nDetails: {ex.Message}",
                "DevStrider", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        var window = new MainWindow
        {
            DataContext = Services.GetRequiredService<MainWindowViewModel>()
        };
        window.Show();
    }
}
