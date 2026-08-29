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
    /// The folder back door — record a day's bids from the resume folders on disk. Lives in the
    /// code-behind rather than the view-model because it owns a modal window, and the view-model
    /// has no business constructing one.
    /// </summary>
    private async void OnImportFolderClick(object sender, RoutedEventArgs e)
    {
        if (Vm == null || App.Services == null) return;

        var importer = App.Services.GetService(typeof(FolderBidImport)) as FolderBidImport;
        var profiles = App.Services.GetService(typeof(ProfileContext)) as ProfileContext;
        if (importer == null || profiles == null) return;

        var dlg = new FolderBidDialog(importer, profiles.All, profiles.Current?.Id ?? default)
        {
            Owner = Window.GetWindow(this),
        };
        if (dlg.ShowDialog() != true) return;

        Vm.StatusMessage = $"Recorded {dlg.Recorded} bid{(dlg.Recorded == 1 ? "" : "s")} from folders.";
        await Vm.ReloadAsync();
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

        // ShellExecute on an arbitrary string is not "open a link" — it is "run whatever this
        // names". The row's URL is not ours: it arrives over the local listener, from a folder
        // import, or from a teammate, since every ds_bids row is team-readable and team-writable.
        // A value of \\host\share\x.exe, file:///…, or a registered protocol handler would launch
        // on click. Only the two schemes a job posting can legitimately have get through.
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var target)
            || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
        {
            Debug.WriteLine($"[BidBoardView] Refused to open non-web URL: {url}");
            MessageBox.Show(
                $"That row's link is not a web address, so it was not opened:\n\n{url}",
                "DevStrider · Link not opened", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try { Process.Start(new ProcessStartInfo { FileName = target.AbsoluteUri, UseShellExecute = true }); }
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
            Subject = (row.Bid?.Url ?? "").Trim()
        };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Line)) return;
        row.FastFeedDraft = dlg.Line;
        await Vm.ApplyFastFeedAsync(row);
    }

    /// <summary>Open a modal showing this row's job description. The posting and the bid are
    /// one row, so there is one JD and no fallback to reach for.</summary>
    private void OnViewJdClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not BoardRow row) return;
        var jd = (row.Bid?.JobDescription ?? "").Trim();
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
    /// Push the grid's selection count into the VM so the bulk-actions toolbar's
    /// <c>Visibility</c> trigger (bound to <c>HasSelection</c>) and the "N selected" label
    /// can update without the VM needing a reference to the WPF control.
    /// </summary>
    private void OnBidGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Vm != null && sender is DataGrid grid)
            Vm.SelectedCount = grid.SelectedItems.Count;
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
