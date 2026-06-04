using System.Collections;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;
using DevStrider.Desktop.Views;
using MongoDB.Bson;

namespace DevStrider.Desktop.ViewModels;

public partial class BidBoardViewModel : ViewModelBase
{
    private readonly BidBoardService _service;
    private readonly ProfileService _profiles;
    private readonly InterviewService? _interviews;

    public ObservableCollection<BoardRow> Rows { get; } = new();

    /// <summary>
    /// Day-mode picker. Setting it snaps From/To to that single day. The "Custom range"
    /// toggle below lets the user widen the window past a single day.
    /// </summary>
    private DateTime _selectedDay = DateTime.Today;
    public DateTime SelectedDay
    {
        get => _selectedDay;
        set
        {
            if (SetProperty(ref _selectedDay, value))
            {
                if (!UseCustomRange)
                {
                    _from = value;
                    _to = value;
                    OnPropertyChanged(nameof(From));
                    OnPropertyChanged(nameof(To));
                }
                _ = ReloadAsync();
            }
        }
    }

    private bool _useCustomRange;
    /// <summary>When true the view uses <see cref="From"/>/<see cref="To"/>; otherwise just <see cref="SelectedDay"/>.</summary>
    public bool UseCustomRange
    {
        get => _useCustomRange;
        set { if (SetProperty(ref _useCustomRange, value)) _ = ReloadAsync(); }
    }

    private DateTime _from = DateTime.Today;
    public DateTime From
    {
        get => _from;
        set { if (SetProperty(ref _from, value)) _ = ReloadAsync(); }
    }

    private DateTime _to = DateTime.Today;
    public DateTime To
    {
        get => _to;
        set { if (SetProperty(ref _to, value)) _ = ReloadAsync(); }
    }

    private string _newLinkUrl = "";
    public string NewLinkUrl { get => _newLinkUrl; set => SetProperty(ref _newLinkUrl, value); }

    private string _newLinkSharedJd = "";
    public string NewLinkSharedJd { get => _newLinkSharedJd; set => SetProperty(ref _newLinkSharedJd, value); }

    /// <summary>How many DataGrid rows are currently selected. Pushed by the view's SelectionChanged handler.</summary>
    private int _selectedCount;
    public int SelectedCount
    {
        get => _selectedCount;
        set
        {
            if (SetProperty(ref _selectedCount, value))
                OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => SelectedCount > 0;

    /// <summary>Bulk-status target — the ComboBox in the toolbar two-way binds here.</summary>
    private string _bulkStatus = BidStatuses.Applied;
    public string BulkStatus { get => _bulkStatus; set => SetProperty(ref _bulkStatus, value); }

    /// <summary>The full list of statuses the bulk picker offers. Exposed so the view can bind <c>ItemsSource</c>.</summary>
    public IReadOnlyList<string> AllBidStatuses { get; } = BidStatuses.All;

    public BidBoardViewModel(BidBoardService service, ProfileService profiles, InterviewService interviews, LocalApiServer localApi, ProfileContext profileContext)
    {
        _service = service;
        _profiles = profiles;
        _interviews = interviews;

        // Auto-refresh when the extension records a bid via the listener — otherwise the
        // user sees the Activity balloon but the Bid board stays stale until they click refresh.
        // Event fires on a thread-pool thread, so marshal back to the UI thread.
        localApi.OnExtensionBidRecorded += () =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                new Action(async () => { try { await ReloadAsync(); } catch { /* ignore */ } }));

        // Reload when active profile changes — workspace data is profile-scoped.
        profileContext.ProfileChanged += () =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                new Action(async () => { try { await ReloadAsync(); } catch { /* ignore */ } }));
    }

    /// <summary>
    /// Create an interview off the given bid, carrying the bid's <c>ResumeId</c> and JD into
    /// the interview row so the user has both ready at interview time. Called from the
    /// "Schedule interview" dialog on a bid row.
    /// </summary>
    public async Task ScheduleInterviewFromBidAsync(
        BoardRow row, DateTime? scheduledDate, string scheduledTime,
        string interviewType, string recruiter, string meetingLink)
    {
        if (_interviews == null || row?.Bid == null) return;
        var jd = (row.Bid.JobDescription ?? "").Trim();
        if (jd.Length == 0) jd = (row.Link?.SharedJobDescription ?? "").Trim();

        await _interviews.CreateAsync(new Models.Interview
        {
            BidId = row.Bid.Id,
            ScheduledDate = scheduledDate,
            ScheduledTime = scheduledTime,
            InterviewType = string.IsNullOrWhiteSpace(interviewType) ? Models.InterviewTypes.Interview : interviewType,
            Recruiter = recruiter,
            MeetingLink = meetingLink,
            Company = row.Bid.Company,
            Role = row.Bid.Role,
            ResumeId = row.Bid.ResumeId,
            AttachedJobDescription = jd,
            Status = Models.InterviewStatuses.Scheduled,
            Origin = "BidBoard"
        });
        StatusMessage = $"Interview scheduled for {row.Bid.Company} · {row.Bid.Role}.";
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            // Range when in custom mode, otherwise just the single SelectedDay.
            var fromDay = UseCustomRange ? From.Date : SelectedDay.Date;
            var toDay = UseCustomRange ? To.Date : SelectedDay.Date;
            if (toDay < fromDay) toDay = fromDay;
            var fromUtc = new DateTime(fromDay.Year, fromDay.Month, fromDay.Day, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();
            var toUtc = new DateTime(toDay.Year, toDay.Month, toDay.Day, 0, 0, 0, DateTimeKind.Local).ToUniversalTime().AddDays(1);
            var rows = await _service.BuildAsync(fromUtc, toUtc);
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
            StatusMessage = $"{rows.Count} row{(rows.Count == 1 ? "" : "s")}.";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task AddLinkAsync()
    {
        var url = (NewLinkUrl ?? "").Trim();
        if (url.Length == 0) return;
        await _service.AddLinkAsync(url, NewLinkSharedJd);
        NewLinkUrl = "";
        NewLinkSharedJd = "";
        await ReloadAsync();
    }

    /// <summary>
    /// Parameters arrive as <c>object?</c> on purpose: WPF passes <c>DependencyProperty.UnsetValue</c>
    /// (a <c>MS.Internal.NamedObject</c>) during early binding evaluation, and a strongly-typed
    /// <c>RelayCommand&lt;BoardRow&gt;</c> would throw <c>ArgumentException</c> in <c>CanExecute</c>.
    /// Casting inside the body sidesteps that.
    /// </summary>
    [RelayCommand]
    public async Task SaveBidAsync(object? param)
    {
        if (param is not BoardRow row || row.Link == null) return;
        await _service.UpsertBidAsync(row.Link.Id, b =>
        {
            if (row.Bid != null)
            {
                b.ResumeId = row.Bid.ResumeId;
                b.Company = row.Bid.Company;
                b.Role = row.Bid.Role;
                b.PrimaryStacks = row.Bid.PrimaryStacks;
                b.Status = string.IsNullOrEmpty(row.Bid.Status) ? BidStatuses.Draft : row.Bid.Status;
                b.Origin = row.Bid.Origin;
                b.JobDescription = row.Bid.JobDescription;
                b.GptResumeContent = row.Bid.GptResumeContent;
                b.Comment = row.Bid.Comment;
            }
        });
        await ReloadAsync();
    }

    /// <summary>
    /// "Delete" on a bid row removes the whole row (bid + link). Refuses if interviews are
    /// attached to the bid — those have to be cleared first so we don't leave orphans.
    /// Always shows a confirmation dialog before touching Mongo.
    /// </summary>
    [RelayCommand]
    public async Task DeleteBidAsync(object? param)
    {
        if (param is not BoardRow row || row.Link == null) return;

        // Block when interviews would be orphaned. The user has to delete those first —
        // we don't cascade because losing interview history quietly is the kind of bug
        // people only notice weeks later.
        if (row.Bid != null && _interviews != null &&
            await _interviews.HasForBidAsync(row.Bid.Id))
        {
            ConfirmDialog.Ask(
                System.Windows.Application.Current?.MainWindow,
                "Can't delete this bid",
                $"Interviews are scheduled against {row.Bid.Company ?? row.Link.Url}. " +
                "Delete the interviews first, then try again.",
                okText: "OK",
                cancelText: "Close",
                danger: false);
            return;
        }

        var label = row.Bid != null
            ? ($"{row.Bid.Company} · {row.Bid.Role}".Trim(' ', '·'))
            : row.Link.Url;
        if (string.IsNullOrWhiteSpace(label)) label = row.Link.Url;

        var ok = ConfirmDialog.Ask(
            System.Windows.Application.Current?.MainWindow,
            "Delete bid?",
            $"{label}\n\nThis removes the link and the bid from your local database. " +
            "It can't be undone.");
        if (!ok) return;

        if (row.Bid != null) await _service.DeleteBidAsync(row.Bid.Id);
        await _service.DeleteLinkAsync(row.Link.Id);
        await ReloadAsync();
        StatusMessage = $"Deleted: {label}";
    }

    /// <summary>
    /// Bulk-set status across every selected row. <paramref name="selection"/> comes from the
    /// DataGrid's <c>SelectedItems</c>. Confirms once, then upserts each row's bid (creating
    /// the bid on URL-only rows if necessary).
    /// </summary>
    [RelayCommand]
    public async Task BulkApplyStatusAsync(object? selection)
    {
        var rows = ExtractSelectedRows(selection);
        if (rows.Count == 0) { StatusMessage = "Select rows first."; return; }
        var status = string.IsNullOrWhiteSpace(BulkStatus) ? BidStatuses.Applied : BulkStatus;

        var ok = ConfirmDialog.Ask(
            System.Windows.Application.Current?.MainWindow,
            "Set status?",
            $"{rows.Count} bid{(rows.Count == 1 ? "" : "s")} → '{status}'.",
            okText: "Set status", danger: false);
        if (!ok) return;

        foreach (var row in rows)
        {
            await _service.UpsertBidAsync(row.Link.Id, b => { b.Status = status; });
        }
        StatusMessage = $"Set {rows.Count} bid{(rows.Count == 1 ? "" : "s")} → '{status}'.";
        await ReloadAsync();
    }

    /// <summary>
    /// Bulk-delete the bid + link for every selected row. Refuses if any selected bid has
    /// interviews attached (delete those first). One confirm dialog covers the whole batch.
    /// </summary>
    [RelayCommand]
    public async Task BulkDeleteAsync(object? selection)
    {
        var rows = ExtractSelectedRows(selection);
        if (rows.Count == 0) { StatusMessage = "Select rows first."; return; }

        if (_interviews != null)
        {
            var blocked = new List<BoardRow>();
            foreach (var r in rows.Where(r => r.Bid != null))
            {
                if (await _interviews.HasForBidAsync(r.Bid!.Id)) blocked.Add(r);
            }
            if (blocked.Count > 0)
            {
                ConfirmDialog.Ask(
                    System.Windows.Application.Current?.MainWindow,
                    "Some bids have interviews",
                    $"{blocked.Count} of the {rows.Count} selected bid{(blocked.Count == 1 ? " has" : "s have")} " +
                    "interviews scheduled. Delete those interviews first, then try again.",
                    okText: "OK", cancelText: "Close", danger: false);
                return;
            }
        }

        var ok = ConfirmDialog.Ask(
            System.Windows.Application.Current?.MainWindow,
            $"Delete {rows.Count} bid{(rows.Count == 1 ? "" : "s")}?",
            "This removes both the bid and the link for each row from your local database. Can't be undone.",
            okText: "Delete");
        if (!ok) return;

        foreach (var r in rows)
        {
            if (r.Bid != null) await _service.DeleteBidAsync(r.Bid.Id);
            await _service.DeleteLinkAsync(r.Link.Id);
        }
        StatusMessage = $"Deleted {rows.Count} bid{(rows.Count == 1 ? "" : "s")}.";
        await ReloadAsync();
    }

    /// <summary>
    /// Materialize the WPF <c>SelectedItems</c> into a stable list — the live collection mutates
    /// while we're iterating so we always copy first.
    /// </summary>
    private static List<BoardRow> ExtractSelectedRows(object? selection)
    {
        if (selection is not IList list) return new List<BoardRow>();
        return list.OfType<BoardRow>().Where(r => r.Link != null).ToList();
    }

    /// <summary>
    /// Parse the row's manually-typed fast-feed line and apply it: sets resumeId/company/role/
    /// stacks on the bid and flips status to <c>applied</c>. Mirrors what the extension does
    /// for an auto-fed line, just driven by hand. The draft buffer is cleared after a
    /// successful save so the same line isn't reapplied on the next click.
    /// </summary>
    [RelayCommand]
    public async Task ApplyFastFeedAsync(object? param)
    {
        if (param is not BoardRow row || row.Link == null)
        {
            StatusMessage = "Pick a row first.";
            return;
        }
        var parsed = Services.FastFeed.ParseLine(row.FastFeedDraft);
        if (parsed == null)
        {
            StatusMessage = "Fast feed needs at least: UID, Company, Role";
            return;
        }
        await _service.UpsertBidAsync(row.Link.Id, b =>
        {
            b.ResumeId = parsed.ResumeId;
            b.Company = parsed.Company;
            b.Role = parsed.Role;
            b.PrimaryStacks = parsed.PrimaryStacks.ToList();
            b.Status = BidStatuses.Applied;
        });
        row.FastFeedDraft = "";
        StatusMessage = $"Applied: {parsed.Company} · {parsed.Role}";
        await ReloadAsync();
    }
}
