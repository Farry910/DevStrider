using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// The Manual Bids tab: postings you apply to yourself, with the resume written behind you.
///
/// <para>
/// A separate tab from the Job Browser because it is separate work. An automatic run is a machine
/// driving a form and a person reviewing the result; a manual bid is a person driving the form and
/// the machine writing a resume. The two want different screens, and putting the second inside the
/// first made the Job Browser answer to two jobs at once.
/// </para>
///
/// <para>
/// The two run at the same time, which is the point. They contend for nothing that matters: the
/// run drives its pages through the DevTools input pipeline addressed to its own browser, never
/// the OS input queue, so it does not take focus and cannot type into the form being filled here.
/// Each tab owns its own browser, so neither waits on the other for one. The one shared thing is
/// Word — one warm instance on one COM thread, so two macros queue — and both flows spend minutes
/// with a human in a form, which is what keeps that from being felt.
/// </para>
///
/// <para>
/// Generation is serial within this tab, because the manual ChatGPT lane has one browser: a second
/// request would navigate the first one's pane out from under it. Several bids can be queued and
/// they are written in order, which is why this is a list you can watch rather than one slot that
/// makes you wait.
/// </para>
/// </summary>
public sealed partial class ManualBidsViewModel : ViewModelBase
{
    private readonly ManualBidStore _store;
    private readonly BidBoardService _bids;
    private readonly ActivityLogService _activity;
    private readonly BidTraceService _trace;
    private readonly SettingsService _settings;

    /// <summary>The run trace, for the view to write browser steps into.</summary>
    public BidTraceService Trace => _trace;

    /// <summary>
    /// Settings for this tab's browser environment. Read live, because the environment is built
    /// on first navigation rather than at startup.
    /// </summary>
    public AppSettings? ProxySettings => _settings.Current;

    public ManualBidsViewModel(ManualBidStore store, BidBoardService bids,
        ActivityLogService activity, BidTraceService trace, SettingsService settings)
    {
        _store = store;
        _bids = bids;
        _activity = activity;
        _trace = trace;
        _settings = settings;
        // A link the automatic run gave up on lands in the store, not here. This is how it appears.
        _store.Changed += () => _ = ReloadAsync();
        _ = ReloadAsync();
    }

    /// <summary>The rows on screen. Order is the order they arrived.</summary>
    public ObservableCollection<JobLinkQueueItem> Bids { get; } = new();

    public bool HasBids => Bids.Count > 0;
    public bool IsEmpty => Bids.Count == 0;

    /// <summary>Waiting for the manual ChatGPT lane, oldest first. One is written at a time.</summary>
    private readonly Queue<Guid> _resumeQueue = new();
    private Guid? _resumeInFlight;

    [ObservableProperty] private string _linksInput = "";
    [ObservableProperty] private JobLinkQueueItem? _selected;

    public string Summary
    {
        get
        {
            if (Bids.Count == 0)
                return "Nothing here yet. Paste links below, or let an automatic run hand over what it cannot apply.";
            var parts = new List<string> { $"{Bids.Count} open" };
            if (_resumeInFlight != null) parts.Add("1 resume being written");
            if (_resumeQueue.Count > 0) parts.Add($"{_resumeQueue.Count} waiting");
            var ready = Bids.Count(b => b.Status == JobLinkQueueStatuses.ManualResumeReady);
            if (ready > 0) parts.Add($"{ready} ready to attach");
            return string.Join(" · ", parts) + ".";
        }
    }

    // ── the browser this tab owns ───────────────────────────────────────────

    /// <summary>Open a posting in this tab's own browser. Nothing is driven after the navigation.</summary>
    public event Func<Guid, string, Task>? OpenRequested;

    /// <summary>Attach a finished resume to the form in that bid's browser, when asked.</summary>
    public event Func<Guid, string, Task>? AttachRequested;

    /// <summary>Ask the manual ChatGPT lane for a resume. The shell routes it.</summary>
    public event Action<ManualBidResumeRequest>? ResumeRequested;

    // ── loading ─────────────────────────────────────────────────────────────

    public async Task ReloadAsync()
    {
        var items = await _store.LoadAsync();
        // Generation that was in flight or queued died with the last process; the description on
        // the row survived it, so those drop back to waiting to be started again rather than
        // sitting for ever on a status nothing will move.
        foreach (var item in items.Where(i => i.Status is JobLinkQueueStatuses.ManualResumeRunning
                                                       or JobLinkQueueStatuses.ManualResumeQueued))
            item.Status = JobLinkQueueStatuses.ManualBid;

        Bids.Clear();
        foreach (var item in items) Bids.Add(item);
        Notify();
    }

    private Task SaveAsync() => _store.SaveAsync(Bids);

    private void Notify()
    {
        OnPropertyChanged(nameof(HasBids));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
    }

    private void SetStatus(JobLinkQueueItem item, string status)
    {
        item.Status = status;
        item.UpdatedAt = DateTime.UtcNow;
        Notify();
    }

    // ── commands ────────────────────────────────────────────────────────────

    /// <summary>Adds pasted links. One per line; anything that is not a web address is refused.</summary>
    [RelayCommand]
    private async Task AddLinksAsync()
    {
        var lines = (LinksInput ?? "")
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (lines.Count == 0) { StatusMessage = "Paste one or more job links first."; return; }

        var added = 0;
        var refused = 0;
        foreach (var line in lines)
        {
            if (!Uri.TryCreate(line, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                refused++;
                continue;
            }
            if (Bids.Any(b => UrlNorm.Normalize(b.Url) == UrlNorm.Normalize(line))) continue;
            Bids.Add(new JobLinkQueueItem
            {
                Url = line,
                Intent = JobWorkItemIntents.Manual,
                Status = JobLinkQueueStatuses.ManualBid,
            });
            added++;
        }

        LinksInput = "";
        await SaveAsync();
        Notify();
        StatusMessage = refused == 0
            ? $"Added {added} link{(added == 1 ? "" : "s")}."
            : $"Added {added}; {refused} were not web addresses and were skipped.";
    }

    [RelayCommand]
    private async Task OpenAsync(JobLinkQueueItem? item)
    {
        if (item == null) return;
        Selected = item;
        if (OpenRequested != null) await OpenRequested(item.Id, item.Url);
    }

    /// <summary>
    /// Queues this row's resume. The form does not have to be open, or started — a description is
    /// all a resume needs, so several can be lined up and then worked through in any order.
    /// </summary>
    [RelayCommand]
    private async Task QueueResumeAsync(JobLinkQueueItem? item)
    {
        if (item == null) return;
        var text = (item.JobDescription ?? "").Trim();
        var rejection = JobPostingExtractor.RejectSupplied(text);
        if (rejection.Length > 0)
        {
            item.Error = "That does not look like a job description: " + rejection;
            StatusMessage = item.Error;
            return;
        }
        if (_resumeInFlight == item.Id || _resumeQueue.Contains(item.Id))
        {
            StatusMessage = "That resume is already queued.";
            return;
        }

        item.Error = "";
        item.ResumeFilePath = "";
        _resumeQueue.Enqueue(item.Id);
        SetStatus(item, JobLinkQueueStatuses.ManualResumeQueued);
        await SaveAsync();
        _trace.Step("Manual", "resume queued", $"{text.Length} chars for {item.Url}");
        await PumpAsync();
    }

    /// <summary>Starts the next queued resume if the lane is free. One at a time, in order.</summary>
    private async Task PumpAsync()
    {
        if (_resumeInFlight != null) return;
        while (_resumeQueue.Count > 0)
        {
            var next = _resumeQueue.Dequeue();
            var item = Bids.FirstOrDefault(b => b.Id == next);
            if (item == null) continue;   // removed while it waited

            _resumeInFlight = next;
            SetStatus(item, JobLinkQueueStatuses.ManualResumeRunning);
            await SaveAsync();
            _activity.Info("Manual Bids", "Resume generating",
                $"{item.Url} — the form is yours to fill while this runs.");
            ResumeRequested?.Invoke(
                new ManualBidResumeRequest(item.Id, item.Url, (item.JobDescription ?? "").Trim()));
            return;
        }
        Notify();
    }

    /// <summary>
    /// Attaches a finished resume to the form in this bid's browser. Asked for, never volunteered —
    /// a controlled form can rerender when its file input changes, and doing that underneath
    /// somebody mid-field is how a half-filled application is lost.
    /// </summary>
    [RelayCommand]
    private async Task AttachAsync(JobLinkQueueItem? item)
    {
        if (item == null) return;
        if (string.IsNullOrWhiteSpace(item.ResumeFilePath))
        {
            StatusMessage = "That bid's resume is not ready yet.";
            return;
        }
        if (AttachRequested != null) await AttachRequested(item.Id, item.ResumeFilePath);
    }

    [RelayCommand]
    private void RevealResume(JobLinkQueueItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.ResumeFilePath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",
                $"/select,\"{item.ResumeFilePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { StatusMessage = $"Couldn't open the folder: {ex.Message}"; }
    }

    /// <summary>
    /// You submitted it. The app is not driving that page and cannot watch you press submit, so it
    /// is told — and the bid the resume recorded moves from draft to applied.
    /// </summary>
    [RelayCommand]
    private async Task MarkSubmittedAsync(JobLinkQueueItem? item)
    {
        if (item == null) return;
        Drop(item.Id);
        if (ObjectId.TryParse(item.BidId, out var bidId))
        {
            try { await _bids.UpdateAsync(bidId, bid => bid.Status = BidStatuses.Applied); }
            catch (Exception ex)
            {
                _activity.Warning("Manual Bids", "Bid stayed a draft",
                    $"{item.Url} — {Safe.Redact(ex.Message)}. Change it on the Bid Board.");
            }
        }
        Bids.Remove(item);
        await SaveAsync();
        _activity.Success("Manual Bids", "Submitted", item.Url);
        _trace.Ok("Manual", "manual bid marked submitted", item.Url);
        StatusMessage = "Recorded as applied. It is on the Bid Board.";
        Notify();
        await PumpAsync();
    }

    /// <summary>Takes a row off the list without submitting. The posting is simply dropped.</summary>
    [RelayCommand]
    private async Task RemoveAsync(JobLinkQueueItem? item)
    {
        if (item == null) return;
        Drop(item.Id);
        Bids.Remove(item);
        await SaveAsync();
        StatusMessage = "Removed.";
        Notify();
        await PumpAsync();
    }

    private void Drop(Guid id)
    {
        if (_resumeInFlight == id) _resumeInFlight = null;
        if (!_resumeQueue.Contains(id)) return;
        var kept = _resumeQueue.Where(x => x != id).ToList();
        _resumeQueue.Clear();
        foreach (var x in kept) _resumeQueue.Enqueue(x);
    }

    // ── results from the manual ChatGPT lane ────────────────────────────────

    /// <summary>
    /// A finished background resume. Recorded and offered; nothing is driven, nothing moves on
    /// screen, because the person is still typing into the form.
    /// </summary>
    public async Task AcceptResumeAsync(ResumeAutomationResult result)
    {
        var item = Bids.FirstOrDefault(b => b.Id == result.WorkItemId);
        if (item == null)
        {
            // The row left the list while its resume was being written. The bid was still recorded
            // by the generator, so nothing is lost — but say so rather than dropping it silently.
            _activity.Warning("Manual Bids", "A resume finished for a bid that is no longer listed",
                result.JobUrl);
            _resumeInFlight = null;
            await PumpAsync();
            return;
        }

        item.ResumeFilePath = result.ResumeFilePath ?? "";
        item.BidId = result.BidId;
        SetStatus(item, JobLinkQueueStatuses.ManualResumeReady);
        if (_resumeInFlight == item.Id) _resumeInFlight = null;
        await SaveAsync();

        _trace.Ok("Manual", "background resume ready", item.ResumeFilePath);
        _activity.Success("Manual Bids", "Resume ready",
            $"{item.Url} — attach it when the form is ready for it.");
        StatusMessage = "A resume is ready. Attach it when you want it.";
        Notify();
        await PumpAsync();
    }

    /// <summary>
    /// A background resume failed. The row stays — its form is still filled in and still the
    /// person's — and the lane is freed so one failure does not stall the rest of the list.
    /// </summary>
    public async Task MarkResumeFailedAsync(Guid workItemId, string message)
    {
        var item = Bids.FirstOrDefault(b => b.Id == workItemId);
        if (_resumeInFlight == workItemId) _resumeInFlight = null;
        if (item != null)
        {
            item.Error = message;
            SetStatus(item, JobLinkQueueStatuses.ManualBid);
            await SaveAsync();
            _activity.Error("Manual Bids", "Resume failed",
                $"{item.Url} — {message} The form is untouched; queue it again to retry.");
            StatusMessage = "A resume failed. That form is untouched — queue it again to retry.";
        }
        Notify();
        await PumpAsync();
    }
}
