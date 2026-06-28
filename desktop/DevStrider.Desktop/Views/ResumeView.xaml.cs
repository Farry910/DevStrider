using System.Windows.Controls;
using DevStrider.Desktop.ViewModels;

namespace DevStrider.Desktop.Views;

public partial class ResumeView : UserControl
{
    public ResumeView()
    {
        InitializeComponent();
        if (App.Services != null)
            DataContext = App.Services.GetService(typeof(ResumeViewModel));
    }
}
