using System.Collections;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;
using DevStrider.Desktop.Views;

namespace DevStrider.Desktop.ViewModels;

public partial class BidBoardViewModel : ViewModelBase
{
    private readonly BidBoardService _service;
    private readonly InterviewService? _interviews;

    /// <summary>
    /// Debounce for externally-triggered reloads (extension records a bid, profile switches).
    /// A batch run across several Chrome profiles fires <c>OnExtensionBidRecorded</c> once per
    /// bid, and each reload rebuilds the whole board and repopulates <see cref="Rows"/> on the
    /// UI thread — so a burst of ten bids used to mean ten full rebuilds and a visibly janky
    /// grid. One rebuild after the burst settles shows the same end state.
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer? _reloadDebounce;
    private static readonly TimeSpan ReloadDebounceInterval = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Guards against overlapping rebuilds: <see cref="ReloadAsync"/> clears and refills
    /// <see cref="Rows"/>, so two interleaved runs can publish a half-built board. A request
    /// arriving mid-flight sets <see cref="_reloadPending"/> and is served by one more pass
    /// afterwards rather than being dropped. UI-thread only — no locking needed.
    /// </summary>
    private bool _reloadInFlight;
    private bool _reloadPending;

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

    private string _newFastFeed = "";
    /// <summary>
    /// The capture box: a fast-feed line, which is the folder name the Word macro produced —
    /// <c>UID, Company, Role, Stack1, Stack2, …</c>. Paste it and the bid is on the board.
    /// </summary>
    public string NewFastFeed { get => _newFastFeed; set => SetProperty(ref _newFastFeed, value); }

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

    /// <summary>
    /// How many bids are written but not yet sent, as a sentence — empty when there are none, so
    /// the indicator only exists when it has something to say.
    /// </summary>
    private string _pendingDisplay = "";
    public string PendingDisplay { get => _pendingDisplay; private set => SetProperty(ref _pendingDisplay, value); }

    private void RefreshPending()
    {
        var n = _service.PendingCount;
        PendingDisplay = n == 0
            ? ""
            : $"{n} bid{(n == 1 ? "" : "s")} queued — sent automatically, or press Submit now.";
    }

    /// <summary>Send the queue immediately rather than waiting for the batch to trip.</summary>
    [RelayCommand]
    public async Task SubmitPendingAsync()
    {
        IsBusy = true;
        try
        {
            var sent = await _service.SubmitPendingAsync();
            StatusMessage = sent == 0
                ? "Nothing queued."
                : $"Submitted {sent} bid{(sent == 1 ? "" : "s")}.";
            await ReloadAsync();
        }
        finally { IsBusy = false; }
    }

    public BidBoardViewModel(
        BidBoardService service,
        InterviewService interviews,
        LocalApiServer localApi,
        ProfileContext profileContext,
        PendingBidQueue queue)
    {
        _service = service;
        _interviews = interviews;

        // The count changes from the listener's threads and the batch timer as well as from here.
        queue.Changed += () =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) RefreshPending();
            else dispatcher.BeginInvoke(new Action(RefreshPending));
        };

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null)
        {
            _reloadDebounce = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background, dispatcher)
            {
                Interval = ReloadDebounceInterval
            };
            _reloadDebounce.Tick += async (_, _) =>
            {
                _reloadDebounce!.Stop();
                await RunCoalescedReloadAsync();
            };
        }

        // Auto-refresh when the extension records a bid via the listener — otherwise the
        // user sees the Activity balloon but the Bid board stays stale until they click refresh.
        // Event fires on a thread-pool thread, so marshal back to the UI thread.
        localApi.OnExtensionBidRecorded += RequestReloadDebounced;

        // Reload when active profile changes — workspace data is profile-scoped.
        profileContext.ProfileChanged += RequestReloadDebounced;
    }

    /// <summary>
    /// Ask for a reload without committing to one per call — see <see cref="_reloadDebounce"/>.
    /// Safe to call from any thread; the timer is restarted on the UI thread so a steady stream
    /// of events keeps pushing the rebuild out until the stream stops.
    /// </summary>
    private void RequestReloadDebounced()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || _reloadDebounce == null)
        {
            // No dispatcher (design-time / headless): fall back to reloading directly.
            _ = RunCoalescedReloadAsync();
            return;
        }
        dispatcher.BeginInvoke(new Action(() =>
        {
            _reloadDebounce.Stop();
            _reloadDebounce.Start();
        }));
    }

    /// <summary>Run exactly one rebuild, then one more if requests arrived while it was running.</summary>
    private async Task RunCoalescedReloadAsync()
    {
        if (_reloadInFlight)
        {
            _reloadPending = true;
            return;
        }
        _reloadInFlight = true;
        try
        {
            do
            {
                _reloadPending = false;
                try { await ReloadAsync(); }
                catch { /* transient database/UI error — the next request retries */ }
            }
            while (_reloadPending);
        }
        finally { _reloadInFlight = false; }
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
            AttachedJobDescription = (row.Bid.JobDescription ?? "").Trim(),
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

    /// <summary>
    /// Record a bid from a pasted folder name. The macro names its output folder with the
    /// fast-feed line, so the folder name is the bid — which makes pasting it the shortest path
    /// from "the resume exists" to "the bid is tracked".
    /// </summary>
    [RelayCommand]
    public async Task AddFastFeedAsync()
    {
        var line = (NewFastFeed ?? "").Trim();
        if (line.Length == 0) return;

        var parsed = Services.FastFeed.ParseLine(line);
        if (parsed == null)
        {
            // Deliberately specific about the UID: the parser rejects anything whose first segment
            // isn't a short alphanumeric id, which is what stops a pasted sentence full of commas
            // from being recorded as a bid at company "QA".
            StatusMessage = "Not a folder name — expected 'UID, Company, Role, Stack1, …' "
                          + "starting with the 5-character resume id.";
            return;
        }

        try
        {
            await _service.AddFromFastFeedAsync(parsed);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't record that: {SharedDbCredentials.Redact(ex.Message)}";
            return;
        }

        NewFastFeed = "";
        StatusMessage = $"Recorded: {parsed.Company} · {parsed.Role}";
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
        if (param is not BoardRow row || row.Bid == null) return;
        var edited = row.Bid;
        await _service.UpdateAsync(edited.Id, b =>
        {
            b.ResumeId = edited.ResumeId;
            b.Company = edited.Company;
            b.Role = edited.Role;
            b.PrimaryStacks = edited.PrimaryStacks;
            b.Status = string.IsNullOrEmpty(edited.Status) ? BidStatuses.Draft : edited.Status;
            b.Origin = edited.Origin;
            b.JobDescription = edited.JobDescription;
            b.GptResumeContent = edited.GptResumeContent;
            b.Comment = edited.Comment;
        });
        await ReloadAsync();
    }

    /// <summary>
    /// "Delete" removes the whole row — the posting and the bid are one. Refuses if interviews
    /// are attached, because losing interview history quietly is the kind of bug people only
    /// notice weeks later. Always confirms before touching the database.
    /// </summary>
    [RelayCommand]
    public async Task DeleteBidAsync(object? param)
    {
        if (param is not BoardRow row || row.Bid == null) return;

        if (_interviews != null && await _interviews.HasForBidAsync(row.Bid.Id))
        {
            ConfirmDialog.Ask(
                System.Windows.Application.Current?.MainWindow,
                "Can't delete this bid",
                $"Interviews are scheduled against {Label(row)}. " +
                "Delete the interviews first, then try again.",
                okText: "OK",
                cancelText: "Close",
                danger: false);
            return;
        }

        var label = Label(row);
        var ok = ConfirmDialog.Ask(
            System.Windows.Application.Current?.MainWindow,
            "Delete bid?",
            $"{label}\n\nThis removes the posting and the bid from the shared database. " +
            "It can't be undone.");
        if (!ok) return;

        await _service.DeleteAsync(row.Bid.Id);
        await ReloadAsync();
        StatusMessage = $"Deleted: {label}";
    }

    /// <summary>Company · role once they're known, and the URL until then.</summary>
    private static string Label(BoardRow row)
    {
        var named = $"{row.Bid.Company} · {row.Bid.Role}".Trim(' ', '·');
        return string.IsNullOrWhiteSpace(named) ? row.Bid.Url : named;
    }

    /// <summary>
    /// Bulk-set status across every selected row. <paramref name="selection"/> comes from the
    /// DataGrid's <c>SelectedItems</c>. Confirms once, then patches each row.
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
            await _service.UpdateAsync(row.Bid.Id, b => { b.Status = status; });

        StatusMessage = $"Set {rows.Count} bid{(rows.Count == 1 ? "" : "s")} → '{status}'.";
        await ReloadAsync();
    }

    /// <summary>
    /// Bulk-delete every selected row. Refuses if any selected bid has interviews attached
    /// (delete those first). One confirm dialog covers the whole batch.
    /// </summary>
    [RelayCommand]
    public async Task BulkDeleteAsync(object? selection)
    {
        var rows = ExtractSelectedRows(selection);
        if (rows.Count == 0) { StatusMessage = "Select rows first."; return; }

        if (_interviews != null)
        {
            var blocked = new List<BoardRow>();
            foreach (var r in rows)
            {
                if (await _interviews.HasForBidAsync(r.Bid.Id)) blocked.Add(r);
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
            "This removes the posting and the bid for each row from the shared database. Can't be undone.",
            okText: "Delete");
        if (!ok) return;

        foreach (var r in rows) await _service.DeleteAsync(r.Bid.Id);

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
        return list.OfType<BoardRow>().Where(r => r.Bid != null).ToList();
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
        if (param is not BoardRow row || row.Bid == null)
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
        await _service.UpdateAsync(row.Bid.Id, b =>
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
