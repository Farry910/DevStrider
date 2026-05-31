using System.Windows;
using DevStrider.Desktop.ViewModels;

namespace DevStrider.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, __) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // Boot every screen so navigation is instant later.
                await vm.Profile.LoadAsync();
                await vm.Settings.LoadAsync();
                await vm.Bids.ReloadAsync();
                await vm.Interviews.ReloadAsync();
                await vm.Overview.ReloadAsync();
                await vm.Stats.ReloadAsync();
                await vm.Import.LoadAsync();
            }
        };
    }
}
