using System.Windows.Controls;
using DevStrider.Desktop.ViewModels;

namespace DevStrider.Desktop.Views;

public partial class InterviewPanelView : UserControl
{
    public InterviewPanelView()
    {
        InitializeComponent();
        if (App.Services != null)
            DataContext = App.Services.GetService(typeof(InterviewPanelViewModel));
    }
}
