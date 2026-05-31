using System.Windows.Controls;
using DevStrider.Desktop.ViewModels;

namespace DevStrider.Desktop.Views;

public partial class BidBoardView : UserControl
{
    public BidBoardView()
    {
        InitializeComponent();
        if (App.Services != null)
        {
            DataContext = App.Services.GetService(typeof(BidBoardViewModel));
        }
    }
}
