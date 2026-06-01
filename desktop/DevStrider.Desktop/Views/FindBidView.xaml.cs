using System.Windows;
using System.Windows.Controls;
using DevStrider.Desktop.ViewModels;

namespace DevStrider.Desktop.Views;

public partial class FindBidView : UserControl
{
    private FindBidViewModel? Vm => DataContext as FindBidViewModel;

    public FindBidView()
    {
        InitializeComponent();
        if (App.Services != null)
            DataContext = App.Services.GetService(typeof(FindBidViewModel));
    }

    private async void OnScheduleClick(object sender, RoutedEventArgs e)
    {
        if (Vm == null || sender is not Button btn || btn.Tag is not FindBidRow row) return;
        var dlg = new ScheduleInterviewDialog
        {
            Owner = Window.GetWindow(this),
            Company = row.Bid.Company,
            Role = row.Bid.Role,
            ResumeIdLabel = row.Bid.ResumeId
        };
        if (dlg.ShowDialog() != true) return;
        await Vm.ScheduleAsync(row, dlg.ScheduledDate, dlg.ScheduledTime,
                               dlg.InterviewType, dlg.Recruiter, dlg.MeetingLink);
    }
}
