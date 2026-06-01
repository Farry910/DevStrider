using System.Windows;
using System.Windows.Controls;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.ViewModels;

namespace DevStrider.Desktop.Views;

public partial class InterviewPanelView : UserControl
{
    private InterviewPanelViewModel? Vm => DataContext as InterviewPanelViewModel;

    public InterviewPanelView()
    {
        InitializeComponent();
        if (App.Services != null)
            DataContext = App.Services.GetService(typeof(InterviewPanelViewModel));
    }

    private void OnViewJdClick(object sender, RoutedEventArgs e)
    {
        if (Vm == null || sender is not Button btn || btn.Tag is not Interview iv) return;
        var jd = Vm.GetJdFor(iv);
        if (jd.Length == 0)
        {
            MessageBox.Show("No JD attached to this interview.", "JD",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new TextViewerDialog
        {
            Owner = Window.GetWindow(this),
            Title = $"JD · {iv.Company} · {iv.Role}",
            Content = jd
        };
        dlg.ShowDialog();
    }

    private async void OnScheduleNextClick(object sender, RoutedEventArgs e)
    {
        if (Vm == null || sender is not Button btn || btn.Tag is not Interview iv) return;
        var dlg = new ScheduleInterviewDialog
        {
            Owner = Window.GetWindow(this),
            Company = iv.Company,
            Role = iv.Role,
            ResumeIdLabel = iv.ResumeId,
        };
        if (dlg.ShowDialog() != true) return;
        await Vm.ScheduleNextStepAsync(iv, dlg.ScheduledDate, dlg.ScheduledTime,
                                       dlg.InterviewType, dlg.Recruiter, dlg.MeetingLink);
    }
}
