using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using DevStrider.Desktop.Services;
using DevStrider.Desktop.ViewModels;

namespace DevStrider.Desktop.Views;

public partial class BidBoardView : UserControl
{
    private BidBoardViewModel? Vm => DataContext as BidBoardViewModel;

    public BidBoardView()
    {
        InitializeComponent();
        if (App.Services != null)
            DataContext = App.Services.GetService(typeof(BidBoardViewModel));
    }

    /// <summary>
    /// Open the row's URL in the OS default browser. Hyperlink.NavigateUri can't bind cleanly
    /// to a string (needs a Uri); we stash the URL on Tag and open it here.
    /// </summary>
    private void OnUrlClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Hyperlink h) return;
        var url = h.Tag as string;
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine($"[BidBoardView] Open URL failed: {ex.Message}"); }
    }

    /// <summary>
    /// Temporal fast-feed input: small popup that takes a line in the form
    ///   "UID, Company, Role, Stack1, Stack2, …"
    /// and parses it through the same FastFeed.ParseLine the extension uses.
    /// </summary>
    private async void OnFastFeedClick(object sender, RoutedEventArgs e)
    {
        if (Vm == null || sender is not Button btn || btn.Tag is not BoardRow row) return;
        var dlg = new FastFeedDialog
        {
            Owner = Window.GetWindow(this),
            Subject = (row.Link?.Url ?? "").Trim()
        };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Line)) return;
        row.FastFeedDraft = dlg.Line;
        await Vm.ApplyFastFeedAsync(row);
    }

    /// <summary>Open a modal showing this row's job description (private bid JD wins, falls
    /// back to the link's shared JD).</summary>
    private void OnViewJdClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not BoardRow row) return;
        var jd = (row.Bid?.JobDescription ?? "").Trim();
        if (jd.Length == 0) jd = (row.Link?.SharedJobDescription ?? "").Trim();
        if (jd.Length == 0)
        {
            MessageBox.Show("No job description saved for this row.", "JD",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new TextViewerDialog
        {
            Owner = Window.GetWindow(this),
            Title = "Job description",
            Content = jd
        };
        dlg.ShowDialog();
    }

    /// <summary>
    /// Schedule a new interview off this bid. The new Interview captures the bid's
    /// <c>ResumeId</c> + <c>JobDescription</c> so the user has both ready at interview time.
    /// </summary>
    private async void OnScheduleInterviewClick(object sender, RoutedEventArgs e)
    {
        if (Vm == null || sender is not Button btn || btn.Tag is not BoardRow row) return;
        if (row.Bid == null)
        {
            MessageBox.Show("This row has no bid to schedule from. Apply a fast-feed first.",
                "Schedule interview", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new ScheduleInterviewDialog
        {
            Owner = Window.GetWindow(this),
            Company = row.Bid.Company,
            Role = row.Bid.Role,
            ResumeIdLabel = row.Bid.ResumeId,
        };
        if (dlg.ShowDialog() != true) return;
        await Vm.ScheduleInterviewFromBidAsync(row, dlg.ScheduledDate, dlg.ScheduledTime,
                                               dlg.InterviewType, dlg.Recruiter, dlg.MeetingLink);
    }
}
