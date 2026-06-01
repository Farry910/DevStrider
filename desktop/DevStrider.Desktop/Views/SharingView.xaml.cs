using System.Windows.Controls;
using DevStrider.Desktop.ViewModels;

namespace DevStrider.Desktop.Views;

public partial class SharingView : UserControl
{
    public SharingView()
    {
        InitializeComponent();
        if (App.Services != null)
            DataContext = App.Services.GetService(typeof(SharingViewModel));
    }
}
