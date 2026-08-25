using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// Owns the durable application queue. Browser code extracts and fills the page; this class
/// advances each work item and stops only when a missing JD or an ambiguous/unresolved form needs
/// human attention. Confirmed application submissions advance the queue automatically.
/// </summary>
public sealed partial class JobBrowserViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly ProfileContext _profiles;
    private readonly ActivityLogService _activity;
    private readonly BidBoardService _bids;
    private readonly PersonFactsService _person;
    private readonly QuickAnswerService _questions;
    private readonly BidTraceService _trace;

    /// <summary>The run trace, so the view can log the browser steps it owns.</summary>
    public BidTraceService Trace => _trace;

    /// <summary>
    /// Personal reference data for the active profile. Cached because the fill script needs it
    /// synchronously, and reloaded whenever the profile changes or its personal data is saved.
    /// </summary>
    private Dictionary<string, string> _reference = new(StringComparer.OrdinalIgnoreCase);

    private string _address = "";
    public string Address { get => _address; set => SetProperty(ref _address, value); }

    private string _queueLinksInput = "";
    public string QueueLinksInput { get => _queueLinksInput; set => SetProperty(ref _queueLinksInput, value); }

    private JobLinkQueueItem? _currentQueueItem;
    public JobLinkQueueItem? CurrentQueueItem
    {
        get => _currentQueueItem;
        private set
        {
            if (!SetProperty(ref _currentQueueItem, value)) return;
            NotifyQueueState();
        }
    }

    private bool _isAutomaticQueueRunning;
    public bool IsAutomaticQueueRunning
    {
        get => _isAutomaticQueueRunning;
        private set
        {
            if (!SetProperty(ref _isAutomaticQueueRunning, value)) return;
            OnPropertyChanged(nameof(QueueInputReadOnly));
            OnPropertyChanged(nameof(IsAutomaticQueueIdle));
            OnPropertyChanged(nameof(ShowManualTools));
        }
    }

    private string _jobDescription = "";
    public string JobDescription { get => _jobDescription; set => SetProperty(ref _jobDescription, value); }

    private string _fallbackJobDescription = "";
    public string FallbackJobDescription { get => _fallbackJobDescription; set => SetProperty(ref _fallbackJobDescription, value); }

    private string _adapterName = "Default (generic)";
    public string AdapterName { get => _adapterName; set => SetProperty(ref _adapterName, value); }

    private string _formQuestionsJson = "[]";
    public string FormQuestionsJson { get => _formQuestionsJson; set => SetProperty(ref _formQuestionsJson, value); }

    private string _currentAnswersJson = "{}";
    public string CurrentAnswersJson { get => _currentAnswersJson; set => SetProperty(ref _currentAnswersJson, value); }

    private string _savedAnswersJson = "{}";
    public string SavedAnswersJson { get => _savedAnswersJson; set => SetProperty(ref _savedAnswersJson, value); }

    private string _selectedResumePath = "";
    public string SelectedResumePath { get => _selectedResumePath; set => SetProperty(ref _selectedResumePath, value); }

    public ObservableCollection<JobLinkQueueItem> JobQueue { get; } = new();

    /// <summary>
    /// The open applications, one per browser. The run drives exactly one of these at a time; the
    /// rest are filled forms waiting for a person.
    /// </summary>
    public ObservableCollection<ApplicationTabViewModel> Tabs { get; } = new();

    private ApplicationTabViewModel? _selectedTab;
    public ApplicationTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!SetProperty(ref _selectedTab, value)) return;
            foreach (var tab in Tabs) tab.IsSelected = ReferenceEquals(tab, value);
            TabSelectionChanged?.Invoke(value);
            NotifyTabState();
        }
    }

    /// <summary>
    /// How many filled applications may wait for review at once.
    ///
    /// <para>
    /// Each one is a live browser holding a rendered page, so this is a memory ceiling as much as an
    /// attention one. When it is reached the run pauses rather than dropping work, and resumes as
    /// soon as a tab is closed.
    /// </para>
    /// </summary>
    public int MaxReviewTabs { get; private set; } = 4;

    public int ParkedReviewCount => Tabs.Count(tab => tab.IsAwaitingReview);
    public bool HasReviewBacklog => ParkedReviewCount > 0;
    public bool ShowTabStrip => Tabs.Count > 1;
    public bool IsReviewCapacityFull => ParkedReviewCount >= MaxReviewTabs;

    public string ReviewBacklogSummary => ParkedReviewCount switch
    {
        0 => "",
        1 => "1 application waiting for review",
        _ => $"{ParkedReviewCount} applications waiting for review",
    };

    private void NotifyTabState()
    {
        OnPropertyChanged(nameof(ParkedReviewCount));
        OnPropertyChanged(nameof(HasReviewBacklog));
        OnPropertyChanged(nameof(ShowTabStrip));
        OnPropertyChanged(nameof(IsReviewCapacityFull));
        OnPropertyChanged(nameof(ReviewBacklogSummary));
        OnPropertyChanged(nameof(IsReadyForReview));
        OnPropertyChanged(nameof(ShowManualTools));
    }

    /// <summary>
    /// The operator pressed Stop and has not started anything since.
    ///
    /// <para>
    /// Stop used to be a single flag that said whether a next link should be opened, and three
    /// places set that flag back to true on their own - a fill finishing, a tab closing, an
    /// application being marked submitted. Any step already in flight when Stop was pressed
    /// therefore restarted the queue the moment it finished, and the run carried on as though
    /// nothing had been asked. An intention outlives the step that was running when it was
    /// expressed, so this stays set until the operator starts something themselves.
    /// </para>
    /// </summary>
    private bool _stopRequested;

    private const int ConsecutiveFailureLimit = 3;
    private int _consecutiveFailures;
    private int _submittedCount;

    public IEnumerable<JobLinkQueueItem> FailedLinks =>
        JobQueue.Where(item => item.Status == JobLinkQueueStatuses.Failed);
    public int FailedCount => FailedLinks.Count();
    public bool HasFailedLinks => FailedCount > 0;

    private bool _isManualJobDescriptionPhase;

    /// <summary>
    /// True once the automatic pass is done and the links it could not read a description from are
    /// being worked through by hand.
    /// </summary>
    public bool IsManualJobDescriptionPhase
    {
        get => _isManualJobDescriptionPhase;
        private set
        {
            if (!SetProperty(ref _isManualJobDescriptionPhase, value)) return;
            OnPropertyChanged(nameof(ShowManualTools));
        }
    }

    public IEnumerable<JobLinkQueueItem> DeferredJdLinks =>
        JobQueue.Where(item => item.Status == JobLinkQueueStatuses.NeedsJobDescription);
    public int DeferredJdCount => DeferredJdLinks.Count();
    public bool HasDeferredJdLinks => DeferredJdCount > 0;

    /// <summary>"Manual JD 2 of 5" - where this link sits in the deferred pass.</summary>
    public string ManualJobDescriptionProgress
    {
        get
        {
            if (!IsManualJobDescriptionPhase || CurrentQueueItem == null) return "";
            var remaining = DeferredJdCount;
            return remaining <= 1 ? "Last link needing a description by hand"
                : $"{remaining} link(s) still need a description by hand";
        }
    }

    public bool QueueInputReadOnly => IsAutomaticQueueRunning;
    public bool IsAutomaticQueueIdle => !IsAutomaticQueueRunning;
    public bool NeedsJobDescription => CurrentQueueItem?.Status == JobLinkQueueStatuses.NeedsJobDescription;
    /// <summary>
    /// True when the tab in front of the person is a filled application waiting on them. The run may
    /// well be filling another one behind it - that is the point of the tabs - so this deliberately
    /// follows the selection rather than whatever the run is currently doing.
    /// </summary>
    public bool IsReadyForReview => SelectedTab?.IsAwaitingReview == true;
    public bool ShowManualTools => !IsAutomaticQueueRunning || NeedsJobDescription || IsReadyForReview;
    public string CurrentStep => CurrentQueueItem?.Status ?? "Idle";
    public string QueueSummary
    {
        get
        {
            if (CurrentQueueItem != null) return $"{CurrentQueueItem.Status}: {CurrentQueueItem.Url}";
            var parts = new List<string> { $"{JobQueue.Count(item => item.Status == JobLinkQueueStatuses.Queued)} waiting" };
            // Submitted links leave the queue, so the count is this session's rather than a row tally.
            if (_submittedCount > 0) parts.Add($"{_submittedCount} submitted");
            var skipped = JobQueue.Count(item => item.Status == JobLinkQueueStatuses.Skipped);
            if (skipped > 0) parts.Add($"{skipped} skipped");
            if (FailedCount > 0) parts.Add($"{FailedCount} failed");
            return string.Join(", ", parts) + ".";
        }
    }

    public event Action? QueueNavigationRequested;

    /// <summary>
    /// Give the run a fresh browser for this item, because the last one is parked.
    ///
    /// <para>
    /// Returns a task, and the caller awaits it. Creating a WebView2 is genuinely slow - a cold
    /// environment takes a moment to come up - and as a plain event the handler returned at its
    /// first await, so navigation could start before any browser existed and fail with "the job
    /// browser is unavailable".
    /// </para>
    /// </summary>
    public event Func<Guid, Task>? AutomationTabRequested;

    /// <summary>This tab is finished with; dispose its browser.</summary>
    public event Action<Guid>? TabCloseRequested;

    /// <summary>Show this tab and hide the rest.</summary>
    public event Action<ApplicationTabViewModel?>? TabSelectionChanged;

    /// <summary>Raised for the manual pass: open this link, but do not try to read the JD from it.</summary>
    public event Action? ManualJobDescriptionRequested;
    public event Action<JobResumePreparation>? ResumeGenerationRequested;
    public event Action<ResumeAutomationResult>? ApplicationFillRequested;
    public event Action<ChatGptAnswerCorrectionRequest>? AnswerCorrectionRequested;
    public event Action<Guid>? ApplicationRefillRequested;

    public JobBrowserViewModel(SettingsService settings, ProfileContext profiles, ActivityLogService activity,
        BidBoardService bids, PersonFactsService person, QuickAnswerService questions, BidTraceService trace)
    {
        _settings = settings;
        _profiles = profiles;
        _activity = activity;
        _bids = bids;
        _person = person;
        _questions = questions;
        _trace = trace;
        _profiles.ProfileChanged += () =>
        {
            IsAutomaticQueueRunning = false;
            _ = LoadSavedAnswersAsync();
            _ = LoadQueueAsync();
        };
        _ = LoadSavedAnswersAsync();
        _ = LoadQueueAsync();
    }

    [RelayCommand]
    private async Task AddLinksToQueueAsync()
    {
        if (QueueInputReadOnly) return;
        var profile = _profiles.Current;
        if (profile == null) { StatusMessage = "No active profile."; return; }

        var candidates = QueueLinksInput
            .Split(new[] { '\r', '\n', ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(text => Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
                            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? NormalizeUrl(uri) : null)
            .Where(url => url != null).Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (candidates.Count == 0)
        {
            StatusMessage = "Paste one or more valid HTTP(S) job links.";
            return;
        }

        var known = JobQueue.Select(item => item.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = candidates.Where(known.Add).Select(url => new JobLinkQueueItem { Url = url }).ToList();
        foreach (var item in added) JobQueue.Add(item);
        QueueLinksInput = "";
        await SaveQueueAsync();
        NotifyQueueState();
        StatusMessage = added.Count == 0 ? "Those links are already in the queue." : $"Queued {added.Count} job link(s).";
        if (added.Count > 0) _activity.Success("Job Browser", "Job links queued", $"{added.Count} for {profile.Name}");
    }

    [RelayCommand]
    private async Task StartAutomaticQueueAsync()
    {
        if (IsAutomaticQueueRunning) return;
        _stopRequested = false;
        _consecutiveFailures = 0;
        IsAutomaticQueueRunning = true;
        StatusMessage = "Automatic application flow approved. DevStrider will stop before final submission.";
        await OpenNextWorkItemAsync();
    }

    /// <summary>Raised so whatever is mid-flight for this run gets torn down, not just the queue flag.</summary>
    public event Action? RunCancellationRequested;

    /// <summary>
    /// Stops the queue and everything it currently has running.
    ///
    /// <para>
    /// This used to set the flag and nothing else, which only decided whether a <em>next</em> link
    /// would be opened. Anything already in flight carried on - and the longest of those is the wait
    /// for a ChatGPT reply, which runs to a three-minute timeout. Stop appeared to do nothing for
    /// three minutes and then produced a timeout alert for a run the person had already abandoned.
    /// </para>
    /// </summary>
    [RelayCommand]
    private void StopAutomaticQueue()
    {
        _stopRequested = true;
        IsAutomaticQueueRunning = false;
        IsManualJobDescriptionPhase = false;
        RunCancellationRequested?.Invoke();
        _trace.Warn("Queue", "stopped by the operator", "cancelling anything still in flight");
        StatusMessage = "Automatic queue stopped. The current page and recovery controls remain available.";
        NotifyQueueState();
    }

    [RelayCommand]
    private async Task OpenNextQueuedLinkAsync()
    {
        _stopRequested = false;
        IsAutomaticQueueRunning = false;
        await OpenNextWorkItemAsync();
    }

    private async Task OpenNextWorkItemAsync()
    {
        if (_stopRequested)
        {
            IsAutomaticQueueRunning = false;
            CurrentQueueItem = null;
            _trace.Warn("Queue", "not opening the next link", "the operator stopped the run");
            StatusMessage = "Automation is stopped. Use Approve & start to continue.";
            return;
        }

        var item = CurrentQueueItem;
        if (item == null || item.Status is JobLinkQueueStatuses.Submitted or JobLinkQueueStatuses.Skipped or JobLinkQueueStatuses.Failed)
            item = JobQueue.FirstOrDefault(candidate => candidate.Status == JobLinkQueueStatuses.Queued);

        // The automatic pass is over. Anything it set aside for want of a readable description is
        // now worked through with a person present, one link at a time, in the same window.
        if (item == null && HasDeferredJdLinks)
        {
            await BeginManualJobDescriptionPassAsync();
            return;
        }

        if (item == null)
        {
            IsAutomaticQueueRunning = false;
            IsManualJobDescriptionPhase = false;
            CurrentQueueItem = null;
            StatusMessage = "No queued job links remain.";
            return;
        }

        IsManualJobDescriptionPhase = false;
        CurrentQueueItem = item;
        await EnsureAutomationTabAsync(item);
        _trace.Begin(item.Id, item.Url);
        _trace.Step("Queue", "opened work item",
            $"status={item.Status}, attempts={item.Attempts}, auto={IsAutomaticQueueRunning}");
        SetStatus(item, JobLinkQueueStatuses.Loading);
        Address = item.Url;
        JobDescription = item.JobDescription;
        FormQuestionsJson = item.FormQuestionsJson;
        CurrentAnswersJson = item.AnswersJson;
        SelectedResumePath = item.ResumeFilePath;
        FallbackJobDescription = "";
        await SaveQueueAsync();
        _trace.Step("Queue", "navigation requested", item.Url);
        QueueNavigationRequested?.Invoke();
        _activity.Info("Job Browser", "Opening queued job", item.Url);
    }

    /// <summary>
    /// Opens the next link that needs a description by hand and waits for the person to point at it.
    ///
    /// <para>
    /// No extraction runs here. The adapters already looked and did not find one, so looking again
    /// would only produce the same wrong answer; the page is put on screen and the person selects
    /// the description or pastes it.
    /// </para>
    /// </summary>
    /// <summary>
    /// Gives the run a tab to work in, reusing the current one when it is still free.
    ///
    /// <para>
    /// The previous tab is only reused when it was never parked. Once an application is waiting for
    /// review its browser belongs to the reviewer, and driving a new job into it would wipe the very
    /// thing they were asked to look at.
    /// </para>
    /// </summary>
    private async Task EnsureAutomationTabAsync(JobLinkQueueItem item)
    {
        var existing = Tabs.FirstOrDefault(tab => tab.WorkItemId == item.Id);
        if (existing != null)
        {
            existing.IsAutomation = true;
            SelectedTab = existing;
            if (AutomationTabRequested != null) await AutomationTabRequested(item.Id);
            return;
        }

        var free = Tabs.FirstOrDefault(tab => tab.IsAutomation);
        if (free != null)
        {
            // The run had a tab and never parked it - that job ended without an application to keep.
            TabCloseRequested?.Invoke(free.WorkItemId);
            Tabs.Remove(free);
        }

        var tab = new ApplicationTabViewModel(item.Id, item.Url, TabTitleFor(item)) { IsAutomation = true };
        Tabs.Add(tab);
        SelectedTab = tab;
        NotifyTabState();
        // Awaited: nothing may navigate until the browser behind this tab actually exists.
        if (AutomationTabRequested != null) await AutomationTabRequested(item.Id);
    }

    /// <summary>
    /// A short name for a tab: the employer, wherever the URL happens to keep it.
    ///
    /// <para>
    /// This used to be the host plus the last path segment, which on Ashby is the posting's UUID -
    /// "jobs.ashbyhq.com · e976ca86-4a93-449f-8f9c-882679988473", sixty characters saying nothing,
    /// and the reason a row of tabs spanned the pane. The employer is almost always the first path
    /// segment instead (ashbyhq.com/absci, lever.co/acme), and on a Greenhouse embed it is the
    /// "for" parameter. The host is the last resort rather than the first thing shown.
    /// </para>
    /// </summary>
    private static string TabTitleFor(JobLinkQueueItem item) => TabTitleForUrl(item.Url);

    /// <summary>The URL half of <see cref="TabTitleFor"/>, separated so it can be exercised alone.</summary>
    public static string TabTitleForUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "Application";

        // Path words that belong to the board rather than to an employer.
        string[] boilerplate =
        [
            "embed", "job_app", "jobs", "job", "careers", "career", "application", "apply",
            "o", "p", "postings", "positions", "vacancy", "en", "us",
        ];
        static bool IsIdentifier(string value) =>
            value.Length >= 16 && value.Count(char.IsDigit) + value.Count(c => c == '-') > value.Length / 3
            || value.All(char.IsDigit);

        var owner = uri.Segments
            .Select(segment => Uri.UnescapeDataString(segment.Trim('/')))
            .FirstOrDefault(segment => segment.Length is > 1 and < 40 && !IsIdentifier(segment) &&
                                       !boilerplate.Contains(segment, StringComparer.OrdinalIgnoreCase))
            ?? "";

        if (owner.Length == 0)
        {
            // Greenhouse embeds name the board here and nowhere else.
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            owner = (query["for"] ?? query["company"] ?? "").Trim();
        }
        if (owner.Length == 0)
            return uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);

        // "empirical-security" reads as a slug; "Empirical Security" reads as a company.
        var words = owner.Replace('_', ' ').Replace('-', ' ').Split(' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", words.Select(word =>
            word.Length == 1 ? word.ToUpperInvariant() : char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private async Task BeginManualJobDescriptionPassAsync()
    {
        var item = DeferredJdLinks.FirstOrDefault();
        if (item == null)
        {
            IsManualJobDescriptionPhase = false;
            IsAutomaticQueueRunning = false;
            StatusMessage = "No queued job links remain.";
            return;
        }

        IsAutomaticQueueRunning = false;
        IsManualJobDescriptionPhase = true;
        CurrentQueueItem = item;
        _trace.Begin(item.Id, item.Url);
        _trace.Step("Queue", "manual description pass", $"{DeferredJdCount} link(s) waiting");
        Address = item.Url;
        JobDescription = "";
        FallbackJobDescription = "";
        FormQuestionsJson = item.FormQuestionsJson;
        SelectedResumePath = item.ResumeFilePath;
        await SaveQueueAsync();
        NotifyQueueState();
        OnPropertyChanged(nameof(ManualJobDescriptionProgress));
        StatusMessage = "The automatic pass is finished. Select the job description on this page, " +
                        "then choose Use selection - or paste it below.";
        _activity.Info("Job Browser", "Manual description pass started", item.Url);
        ManualJobDescriptionRequested?.Invoke();
    }

    [RelayCommand]
    private async Task ContinueWithPastedJdAsync()
    {
        var item = CurrentQueueItem;
        if (item == null || item.Status != JobLinkQueueStatuses.NeedsJobDescription) return;
        if (string.IsNullOrWhiteSpace(FallbackJobDescription))
        {
            StatusMessage = "Paste the job description to continue.";
            return;
        }
        JobDescription = FallbackJobDescription.Trim();
        await StartResumeGenerationAsync(item, JobDescription, FormQuestionsJson);
    }

    [RelayCommand]
    private async Task MarkSubmittedAndContinueAsync()
    {
        var tab = SelectedTab;
        var item = tab == null ? null : JobQueue.FirstOrDefault(x => x.Id == tab.WorkItemId);
        if (tab == null || item == null || !tab.IsAwaitingReview)
        {
            StatusMessage = "There is no reviewed application waiting to be marked submitted.";
            return;
        }
        await CompleteSubmittedItemAsync(item, "Marked submitted after human review.", automatic: false);
    }

    /// <summary>Closes a reviewed tab without submitting it, freeing its slot for the run.</summary>
    [RelayCommand]
    private async Task CloseSelectedTabAsync()
    {
        var tab = SelectedTab;
        if (tab == null) { StatusMessage = "There is no tab selected."; return; }
        if (tab.IsAutomation)
        {
            StatusMessage = "That tab is being worked on. Stop the queue first if you want it closed.";
            return;
        }
        var item = JobQueue.FirstOrDefault(x => x.Id == tab.WorkItemId);
        if (item != null)
        {
            SetStatus(item, JobLinkQueueStatuses.Skipped);
            _activity.Info("Job Browser", "Application closed without submitting", item.Url);
        }
        await ReleaseTabAsync(tab);
    }

    /// <summary>
    /// Disposes a tab and lets the run have the slot back.
    ///
    /// <para>
    /// This is the other half of the review ceiling. A run that stopped because every slot was full
    /// starts again here, without anybody having to notice that it had stopped.
    /// </para>
    /// </summary>
    private async Task ReleaseTabAsync(ApplicationTabViewModel tab)
    {
        TabCloseRequested?.Invoke(tab.WorkItemId);
        Tabs.Remove(tab);
        if (ReferenceEquals(SelectedTab, tab))
            SelectedTab = Tabs.FirstOrDefault(candidate => candidate.IsAwaitingReview) ?? Tabs.FirstOrDefault();
        NotifyTabState();
        await SaveQueueAsync();

        var moreWork = JobQueue.Any(x => x.Status == JobLinkQueueStatuses.Queued) || HasDeferredJdLinks;
        if (_stopRequested || !moreWork || IsAutomaticQueueRunning || IsReviewCapacityFull)
        {
            if (!moreWork && ParkedReviewCount == 0) StatusMessage = "Everything is reviewed and the queue is empty.";
            return;
        }
        _trace.Step("Queue", "resumed after a review slot freed", $"{ParkedReviewCount} still parked");
        IsAutomaticQueueRunning = true;
        StatusMessage = "A review slot freed up. Continuing with the next link.";
        await OpenNextWorkItemAsync();
    }

    public async Task MarkSubmittedAutomaticallyAsync(string detail)
    {
        var item = CurrentQueueItem;
        if (item == null) return;
        await CompleteSubmittedItemAsync(item,
            string.IsNullOrWhiteSpace(detail) ? "The job site confirmed submission." : detail,
            automatic: true);
    }

    private async Task CompleteSubmittedItemAsync(JobLinkQueueItem item, string detail, bool automatic)
    {
        SetStatus(item, JobLinkQueueStatuses.Submitted);
        if (ObjectId.TryParse(item.BidId, out var bidId))
            await _bids.UpdateAsync(bidId, bid => bid.Status = BidStatuses.Applied);
        _activity.Success("Job Browser", automatic ? "Application submitted automatically" : "Application submitted",
            $"{item.Url} | {detail}");
        _trace.Step("Queue", automatic ? "automatic submission confirmed" : "marked submitted", item.Url);
        // The bid row and the Activity entry are the durable record by this point, so the queue
        // entry is spent. Drop it instead of letting submitted links pile up in the working list.
        JobQueue.Remove(item);
        _submittedCount++;
        if (CurrentQueueItem?.Id == item.Id) CurrentQueueItem = null;
        await SaveQueueAsync();

        var tab = Tabs.FirstOrDefault(candidate => candidate.WorkItemId == item.Id);
        if (tab != null)
        {
            // A reviewed tab going away is what frees a slot; ReleaseTabAsync restarts the run if it
            // had stopped for want of one. Restarting it unconditionally here would start a second
            // one on top of a run that never paused.
            await ReleaseTabAsync(tab);
            return;
        }

        if (_stopRequested)
        {
            IsAutomaticQueueRunning = false;
            StatusMessage = "Marked submitted. Automation is stopped; nothing further was started.";
            return;
        }
        IsAutomaticQueueRunning = true;
        await OpenNextWorkItemAsync();
    }

    [RelayCommand]
    private async Task SkipCurrentQueuedLinkAsync()
    {
        if (CurrentQueueItem == null) { StatusMessage = "Open a queued job first."; return; }
        var continueAutomatically = IsAutomaticQueueRunning || IsReadyForReview;
        SetStatus(CurrentQueueItem, JobLinkQueueStatuses.Skipped);
        _activity.Info("Job Browser", "Queued job skipped", CurrentQueueItem.Url);
        CurrentQueueItem = null;
        await SaveQueueAsync();
        if (continueAutomatically)
        {
            IsAutomaticQueueRunning = true;
            await OpenNextWorkItemAsync();
        }
    }

    [RelayCommand]
    private async Task RetryCurrentAsync()
    {
        if (CurrentQueueItem == null) return;
        _stopRequested = false;
        SetStatus(CurrentQueueItem, JobLinkQueueStatuses.Queued);
        CurrentQueueItem.Error = "";
        _consecutiveFailures = 0;
        IsAutomaticQueueRunning = true;
        await SaveQueueAsync();
        await OpenNextWorkItemAsync();
    }

    /// <summary>Puts every collected failure back in line. Attempt counts are kept so a link that
    /// keeps failing stays identifiable after several rounds of this.</summary>
    [RelayCommand]
    private async Task RequeueFailedLinksAsync()
    {
        var failed = FailedLinks.ToList();
        if (failed.Count == 0) { StatusMessage = "There are no failed links to requeue."; return; }
        foreach (var item in failed)
        {
            item.Error = "";
            SetStatus(item, JobLinkQueueStatuses.Queued);
        }
        _consecutiveFailures = 0;
        if (CurrentQueueItem is { Status: JobLinkQueueStatuses.Queued }) CurrentQueueItem = null;
        await SaveQueueAsync();
        NotifyQueueState();
        StatusMessage = $"Requeued {failed.Count} failed link(s). Approve and start the automatic flow to work through them.";
        _activity.Info("Job Browser", "Failed links requeued", $"{failed.Count} link(s)");
    }

    [RelayCommand]
    private void CopyFailedLinks()
    {
        var failed = FailedLinks.ToList();
        if (failed.Count == 0) { StatusMessage = "There are no failed links to copy."; return; }
        Clipboard.SetText(string.Join(Environment.NewLine, failed.Select(item =>
            string.IsNullOrWhiteSpace(item.Error) ? item.Url : $"{item.Url}\t{item.Error}")));
        StatusMessage = $"Copied {failed.Count} failed link(s) with their errors.";
    }

    [RelayCommand]
    private async Task ClearFailedLinksAsync()
    {
        var failed = FailedLinks.ToList();
        if (failed.Count == 0) { StatusMessage = "There are no failed links to remove."; return; }
        if (CurrentQueueItem != null && failed.Contains(CurrentQueueItem)) CurrentQueueItem = null;
        foreach (var item in failed) JobQueue.Remove(item);
        await SaveQueueAsync();
        NotifyQueueState();
        StatusMessage = $"Removed {failed.Count} failed link(s) from the queue.";
        _activity.Info("Job Browser", "Failed links removed", $"{failed.Count} link(s)");
    }

    public void BeginPageExtraction()
    {
        if (CurrentQueueItem == null) return;
        SetStatus(CurrentQueueItem, JobLinkQueueStatuses.ExtractingJobDescription);
        StatusMessage = "Extracting the job description and application questions...";
        _ = SaveQueueAsync();
    }

    public async Task AcceptExtractedPageAsync(string jobDescription, string questionsJson)
    {
        var item = CurrentQueueItem;
        if (item == null) return;
        JobDescription = jobDescription.Trim();
        FormQuestionsJson = string.IsNullOrWhiteSpace(questionsJson) ? "[]" : questionsJson;
        item.FormQuestionsJson = FormQuestionsJson;
        _trace.Step("Extract", "page accepted",
            $"jd={JobDescription.Length} chars, questions={CountQuestions(FormQuestionsJson)}");
        _trace.Payload("Extract", "questions", FormQuestionsJson);

        if (string.IsNullOrWhiteSpace(JobDescription))
        {
            await DeferForManualJobDescriptionAsync(item.Id, "the page exposed no job description");
            return;
        }
        await StartResumeGenerationAsync(item, JobDescription, FormQuestionsJson);
    }

    /// <summary>Hands the current tab over to the reviewer and takes it off the run.</summary>
    private void ParkTabForReview(JobLinkQueueItem item, string summary)
    {
        var tab = Tabs.FirstOrDefault(candidate => candidate.WorkItemId == item.Id);
        if (tab == null)
        {
            tab = new ApplicationTabViewModel(item.Id, item.Url, TabTitleFor(item));
            Tabs.Add(tab);
        }
        tab.IsAutomation = false;
        tab.Status = JobLinkQueueStatuses.ReadyForReview;
        tab.Summary = summary;
        NotifyTabState();
    }

    /// <summary>
    /// Decides what the run does once an application has been parked.
    ///
    /// <para>
    /// It carries on into the next link unless there is nowhere to put the result. Every parked tab
    /// is a live browser holding a rendered page, so they cannot accumulate without limit; at the
    /// ceiling the run stops and says so, and closing any one of them starts it again.
    /// </para>
    /// </summary>
    private async Task ContinueAfterParkingAsync(JobLinkQueueItem parked)
    {
        CurrentQueueItem = null;
        var moreWork = JobQueue.Any(x => x.Status == JobLinkQueueStatuses.Queued) || HasDeferredJdLinks;

        if (!moreWork)
        {
            IsAutomaticQueueRunning = false;
            StatusMessage = ParkedReviewCount == 1
                ? "All links are done. One application is waiting for review."
                : $"All links are done. {ParkedReviewCount} applications are waiting for review.";
            return;
        }

        if (IsReviewCapacityFull)
        {
            IsAutomaticQueueRunning = false;
            StatusMessage = $"{ParkedReviewCount} applications are waiting for review, which is the limit. " +
                            "Submit or close one and the queue continues on its own.";
            _trace.Step("Queue", "paused on the review limit", $"{ParkedReviewCount} parked");
            return;
        }

        if (_stopRequested)
        {
            IsAutomaticQueueRunning = false;
            StatusMessage = "Stopped. This application is parked for review; nothing further was started.";
            return;
        }
        IsAutomaticQueueRunning = true;
        StatusMessage = $"{parked.Url} is ready for review. Starting the next link; review it whenever you like.";
        await OpenNextWorkItemAsync();
    }

    public async Task StartManualBidFromCurrentPageAsync(string jobUrl, string jobDescription, string questionsJson)
    {
        var normalized = NormalizeUrl(new Uri(jobUrl));
        var item = JobQueue.FirstOrDefault(x => x.Url.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            item = new JobLinkQueueItem { Url = normalized };
            JobQueue.Add(item);
        }
        CurrentQueueItem = item;
        _stopRequested = false;
        IsAutomaticQueueRunning = true;
        await StartResumeGenerationAsync(item, jobDescription, questionsJson);
    }

    private async Task StartResumeGenerationAsync(JobLinkQueueItem item, string jobDescription, string questionsJson)
    {
        item.JobDescription = jobDescription.Trim();
        item.FormQuestionsJson = string.IsNullOrWhiteSpace(questionsJson) ? "[]" : questionsJson;
        item.Error = "";
        item.AnswerCorrectionAttempts = 0;
        item.PendingCorrectionQuestionsJson = "[]";
        SetStatus(item, JobLinkQueueStatuses.GeneratingResume);
        await SaveQueueAsync();
        StatusMessage = "Generating the tailored resume in ChatGPT...";
        // Read the profile's personal data now rather than trusting the cache. It was only ever
        // filled on construction — before sign-in, so with no active profile and nothing to read —
        // and on a profile switch, which meant editing Personal info never reached ChatGPT at all.
        await LoadSavedAnswersAsync();
        _trace.Step("Reference", "personal data loaded", $"{_reference.Count} field(s)");
        _trace.Payload("Reference", "values", SavedAnswersJson);
        _trace.Step("ChatGPT", "resume requested", item.Url);
        ResumeGenerationRequested?.Invoke(new JobResumePreparation(
            item.Id, item.Url, item.JobDescription, item.FormQuestionsJson,
            JsonSerializer.Serialize(BuildKnownValues()), item.AnswerConversationUrl));
    }

    public async Task AcceptResumeResultAsync(ResumeAutomationResult result)
    {
        // A recruiter resume has no queue entry by design and stops here.
        if (result.ResumeOnly) return;

        var item = JobQueue.FirstOrDefault(x => x.Id == result.WorkItemId);
        if (item == null)
        {
            // The other end of the same silence: the resume was generated, but its queue row had
            // gone — so nothing switched to the job browser and nothing filled the form. It looked
            // exactly like the app hanging in Resume Studio.
            StatusMessage = "A resume finished for a job link that is no longer in the queue, so " +
                            "there was nothing to fill. Re-add the link and run it again.";
            RecordFailure("Resume result had no queue item", result.JobUrl);
            return;
        }

        CurrentQueueItem = item;
        item.AnswersJson = result.AnswersJson;
        item.ResumeFilePath = result.ResumeFilePath;
        item.BidId = result.BidId;
        if (!string.IsNullOrWhiteSpace(result.AnswerConversationUrl))
        {
            item.AnswerConversationUrl = result.AnswerConversationUrl;
            item.AnswerConversationId = result.AnswerConversationId;
        }
        CurrentAnswersJson = result.AnswersJson;
        SelectedResumePath = result.ResumeFilePath;
        _trace.Step("ChatGPT", "result received",
            $"answers={CountQuestions(result.AnswersJson)}, resumeFile=" +
            (string.IsNullOrWhiteSpace(result.ResumeFilePath) ? "(none)" : result.ResumeFilePath) +
            $", bidId={result.BidId}");
        _trace.Payload("ChatGPT", "answers", result.AnswersJson);

        // The answers object is the only thing that can fill this form s screening questions. If the
        // form asked and nothing parseable came back, filling would leave every required question
        // empty and the run would end at human review having achieved nothing. That is a format
        // failure, not a slow step, so the link is recorded and the queue moves on.
        var asked = CountQuestions(item.FormQuestionsJson);
        var answered = CountQuestions(result.AnswersJson);
        if (asked > 0 && answered <= 0)
        {
            var summary = $"Gave up on format at answers (output): expected answers to {asked} " +
                          (answered < 0 ? "question(s); got unparseable JSON." : "question(s); got none.");
            _trace.Fail("Contract", "answers output rejected", summary);
            await MarkAutomationFailureAsync(item.Id, summary);
            return;
        }
        _trace.Ok("Contract", "answers output ok", $"{answered} answer(s) for {asked} question(s)");

        WarnAboutBlankAnswers(item, result.AnswersJson);
        // Again before filling: an answer saved in Quick answers mid-run is a new personal fact,
        // and the fill script reads this cache synchronously.
        await LoadSavedAnswersAsync();

        // A resume that lands after Stop must not start filling a form. The generation is finished
        // and saved either way, so the link goes back to the queue rather than being lost.
        if (_stopRequested)
        {
            SetStatus(item, JobLinkQueueStatuses.Queued);
            _trace.Warn("Fill", "not filling", "the run was stopped while the resume was generating");
            StatusMessage = "Stopped. The resume finished and was saved, but no form was filled. " +
                            "That link is back in the queue.";
            await SaveQueueAsync();
            return;
        }

        SetStatus(item, JobLinkQueueStatuses.FillingApplication);
        await SaveQueueAsync();
        StatusMessage = "Resume ready. Filling the application and uploading the resume...";
        _trace.Step("Fill", "requested", item.Url);
        ApplicationFillRequested?.Invoke(result);
    }

    /// <summary>
    /// Persists the answer chat as soon as ChatGPT assigns its /c/ id. This intentionally happens
    /// before Word runs, so a crash during document generation cannot orphan the conversation that
    /// a field-validation retry must continue.
    /// </summary>
    public async Task RememberAnswerConversationAsync(Guid workItemId, string url, string id)
    {
        var item = JobQueue.FirstOrDefault(candidate => candidate.Id == workItemId);
        if (item == null || string.IsNullOrWhiteSpace(url)) return;
        item.AnswerConversationUrl = url;
        item.AnswerConversationId = id;
        item.UpdatedAt = DateTime.UtcNow;
        await SaveQueueAsync();
        _trace.Step("ChatGPT", "answer conversation persisted", id.Length > 0 ? id : url);
    }

    /// <summary>
    /// Starts the one targeted second answer pass for primary-fill misses, dynamic choices, or
    /// validation errors returned when the application adapter tries the site's final Submit button.
    /// </summary>
    public async Task<bool> RequestAnswerCorrectionAsync(string correctionQuestionsJson)
    {
        var item = CurrentQueueItem;
        if (item == null || CountQuestions(correctionQuestionsJson) == 0 || item.AnswerCorrectionAttempts >= 1)
            return false;
        item.AnswerCorrectionAttempts++;
        item.PendingCorrectionQuestionsJson = correctionQuestionsJson;
        SetStatus(item, JobLinkQueueStatuses.ResolvingApplicationFields);
        await SaveQueueAsync();
        StatusMessage = "Some application fields failed validation. Asking ChatGPT once for corrected answers...";
        _trace.Step("Fill", "second pass requested",
            $"questions={CountQuestions(correctionQuestionsJson)}, answerChat={item.AnswerConversationId}");
        _trace.Payload("Fill", "second-pass questions", correctionQuestionsJson);
        AnswerCorrectionRequested?.Invoke(new ChatGptAnswerCorrectionRequest(
            item.Id, item.AnswerConversationUrl, item.AnswerConversationId,
            correctionQuestionsJson, JsonSerializer.Serialize(BuildKnownValues()),
            item.AnswersJson, item.JobDescription));
        return true;
    }

    public async Task AcceptAnswerCorrectionAsync(ChatGptAnswerCorrectionResult result)
    {
        var item = JobQueue.FirstOrDefault(candidate => candidate.Id == result.WorkItemId);
        if (item == null)
        {
            // Same shape as the resume dead-end 9.5.5 closed: corrections came back for a link that
            // had left the queue, and returning quietly left the run with no refill, no error and
            // nothing to retry — indistinguishable from a hang.
            StatusMessage = "Corrected answers arrived for a job link that is no longer in the queue.";
            _trace.Warn("Fill", "correction result had no queue item", result.WorkItemId.ToString());
            RecordFailure("Correction result had no queue item", result.WorkItemId.ToString());
            return;
        }
        CurrentQueueItem = item;
        item.AnswersJson = MergeAnswers(item.AnswersJson, result.AnswersJson);
        CurrentAnswersJson = item.AnswersJson;
        if (!string.IsNullOrWhiteSpace(result.ConversationUrl))
        {
            item.AnswerConversationUrl = result.ConversationUrl;
            item.AnswerConversationId = result.ConversationId;
        }
        SetStatus(item, JobLinkQueueStatuses.FillingApplication);
        await SaveQueueAsync();
        StatusMessage = "Corrected application answers received. Refilling the failed fields...";
        _trace.Payload("Fill", "merged second-pass answers", item.AnswersJson);
        ApplicationRefillRequested?.Invoke(item.Id);
    }

    public async Task MarkAnswerCorrectionFailureAsync(Guid workItemId, string message)
    {
        var item = JobQueue.FirstOrDefault(candidate => candidate.Id == workItemId);
        if (item == null) return;
        CurrentQueueItem = item;
        SetStatus(item, JobLinkQueueStatuses.FillingApplication);
        await SaveQueueAsync();
        StatusMessage = message + " Continuing with the primary answers; unresolved results will be shown for review.";
        _trace.Warn("Fill", "second pass unavailable", message);
        ApplicationRefillRequested?.Invoke(item.Id);
    }

    /// <summary>
    /// Clears the persisted correction inventory only after the browser has physically recommitted
    /// those controls. Until then it is also the restart-safe list of fields that must not be skipped
    /// merely because their old values are still painted in the DOM.
    /// </summary>
    public async Task CompleteAnswerCorrectionRefillAsync(Guid workItemId)
    {
        var item = JobQueue.FirstOrDefault(candidate => candidate.Id == workItemId);
        if (item == null) return;
        item.PendingCorrectionQuestionsJson = "[]";
        item.UpdatedAt = DateTime.UtcNow;
        await SaveQueueAsync();
    }

    public async Task MarkReadyForReviewAsync(
        string adapter,
        int filled,
        int skipped,
        IReadOnlyCollection<string> touched,
        bool resumeUploaded,
        string note = "",
        IReadOnlyCollection<string>? unfilled = null)
    {
        var item = CurrentQueueItem;
        if (item == null) return;
        item.AdapterName = adapter;
        _trace.Ok("Fill", "ready for review",
            $"adapter={adapter}, filled={filled}, skipped={skipped}, uploaded={resumeUploaded}, " +
            $"unfilled={(unfilled?.Count ?? 0)}");
        if (touched.Count > 0) _trace.Step("Fill", "fields filled", string.Join(" | ", touched));
        if (unfilled is { Count: > 0 }) _trace.Step("Fill", "still empty", string.Join(" | ", unfilled));
        // Straight to the Quick answers tab, while the form is still on screen next door.
        _questions.Publish(Uri.TryCreate(item.Url, UriKind.Absolute, out var host) ? host.Host : "",
            unfilled ?? Array.Empty<string>());
        SetStatus(item, JobLinkQueueStatuses.ReadyForReview);
        _consecutiveFailures = 0;
        await SaveQueueAsync();
        var upload = resumeUploaded ? "Resume uploaded." : "Resume upload needs manual attention.";
        RecordFill(new Uri(item.Url).Host, adapter, filled, skipped, touched);
        _activity.Info("Job Browser", "Human review required", item.Url);
        _trace.End("ready for review", $"filled={filled}, skipped={skipped}");

        // Park this application in its own tab and keep going. The reviewer picks it up when they
        // are ready; the run does not wait for them, because the next resume can be generated while
        // this form sits on screen.
        ParkTabForReview(item, $"{adapter}: filled {filled}, skipped {skipped}. {upload} {note}".Trim());
        await ContinueAfterParkingAsync(item);
    }

    public async Task BeginValidationRepairAsync(IReadOnlyCollection<string> errors)
    {
        var item = CurrentQueueItem;
        if (item == null) return;
        SetStatus(item, JobLinkQueueStatuses.FillingApplication);
        IsAutomaticQueueRunning = false;
        await SaveQueueAsync();
        var detail = string.Join("; ", errors.Take(8));
        StatusMessage = "The job site rejected fields after the Submit attempt. Rechecking them now: " + detail;
        _trace.Warn("Validate", "submit errors detected", detail);
        _activity.Warning("Job Browser", "Application needs corrections", detail);
    }

    /// <summary>
    /// A failed link stays in the queue as a recoverable record instead of ending the batch: the
    /// automatic flow moves to the next link and the failures collect for a later retry. A run of
    /// consecutive failures does stop the queue, because at that point the machine, the network, or
    /// the profile is usually the problem rather than any individual link.
    /// </summary>
    /// <summary>
    /// Sets a link aside because its description could not be read, and carries straight on.
    ///
    /// <para>
    /// This is not a failure and is deliberately kept out of the consecutive-failure count. Nothing
    /// is wrong with the machine, the network or the profile - one page did not expose a description
    /// where the adapters look for one, and a person can point at it in a few seconds. Stopping the
    /// batch to ask, or burning the link, both cost more than deferring it: the automatic pass runs
    /// to the end untouched, and these links are worked through together afterwards.
    /// </para>
    /// </summary>
    public async Task DeferForManualJobDescriptionAsync(Guid workItemId, string reason)
    {
        var item = JobQueue.FirstOrDefault(x => x.Id == workItemId);
        if (item == null) return;
        item.Error = reason;
        SetStatus(item, JobLinkQueueStatuses.NeedsJobDescription);
        _trace.Warn("Queue", "deferred for a manual description", $"{item.Url}: {reason}");
        _trace.End("deferred", reason);
        _activity.Info("Job Browser", "Job description needs a human", $"{item.Url} | {reason}");
        await SaveQueueAsync();
        NotifyQueueState();

        if (!IsAutomaticQueueRunning)
        {
            StatusMessage = "Set aside for a manual description: " + reason;
            return;
        }
        StatusMessage = $"No readable description here, so it is set aside for later. {DeferredJdCount} waiting.";
        CurrentQueueItem = null;
        await OpenNextWorkItemAsync();
    }

    /// <summary>
    /// Takes a description the person picked out on the page and rejoins the normal flow with it.
    /// </summary>
    public async Task AcceptManualJobDescriptionAsync(string jobDescription, string questionsJson)
    {
        var item = CurrentQueueItem;
        if (item == null) return;
        item.Error = "";
        JobDescription = jobDescription.Trim();
        FormQuestionsJson = string.IsNullOrWhiteSpace(questionsJson) ? "[]" : questionsJson;
        item.FormQuestionsJson = FormQuestionsJson;
        _trace.Ok("Extract", "description supplied by hand",
            $"{JobDescription.Length} chars, questions={CountQuestions(FormQuestionsJson)}");
        await StartResumeGenerationAsync(item, JobDescription, FormQuestionsJson);
    }

    /// <summary>Gives up on the link in front of the manual pass and moves to the next one.</summary>
    [RelayCommand]
    private async Task SkipManualJobDescriptionAsync()
    {
        var item = CurrentQueueItem;
        if (item == null) return;
        SetStatus(item, JobLinkQueueStatuses.Skipped);
        _activity.Info("Job Browser", "Skipped a link needing a manual description", item.Url);
        CurrentQueueItem = null;
        await SaveQueueAsync();
        await OpenNextWorkItemAsync();
    }

    public async Task MarkAutomationFailureAsync(Guid workItemId, string message)
    {
        var item = JobQueue.FirstOrDefault(x => x.Id == workItemId);
        if (item == null) return;
        CurrentQueueItem = item;
        item.Error = message;
        item.Attempts++;
        SetStatus(item, JobLinkQueueStatuses.Failed);
        _trace.Fail("Run", "work item failed", message);
        _trace.End("failed", message);
        await SaveQueueAsync();
        RecordFailure("Application automation failed", $"{item.Url}: {message}");

        _consecutiveFailures++;
        var keepGoing = IsAutomaticQueueRunning && _consecutiveFailures < ConsecutiveFailureLimit &&
                        JobQueue.Any(x => x.Status == JobLinkQueueStatuses.Queued);
        if (keepGoing)
        {
            StatusMessage = $"Collected a failed link and moved on: {message}";
            CurrentQueueItem = null;
            await OpenNextWorkItemAsync();
            return;
        }
        var stoppedOnStreak = IsAutomaticQueueRunning && _consecutiveFailures >= ConsecutiveFailureLimit;
        IsAutomaticQueueRunning = false;
        StatusMessage = stoppedOnStreak
            ? $"Automation stopped after {_consecutiveFailures} failures in a row. Last error: {message}"
            : "Automation paused: " + message;
    }

    [RelayCommand]
    private void CopyExtractedJobDescription()
    {
        if (string.IsNullOrWhiteSpace(JobDescription)) { StatusMessage = "Extract a JD first."; return; }
        Clipboard.SetText("Job description:\n\n" + JobDescription.Trim());
        StatusMessage = "JD copied.";
    }

    [RelayCommand]
    private void CopyQuestionsForChatGpt()
    {
        if (string.IsNullOrWhiteSpace(FormQuestionsJson) || FormQuestionsJson == "[]")
        { StatusMessage = "Extract questions first."; return; }
        Clipboard.SetText(
            "Answer these job-application questions from the reference data below, and nothing else. " +
            "Return an empty string rather than inventing anything. Never answer a question asking for a " +
            "government ID, social-security number, passport, driver's licence, or bank or card details.\n\n" +
            "A required question with options must not come back empty: where the reference data does " +
            "not settle it, choose the option that keeps the application eligible. Consent, availability, " +
            "willingness to relocate and acknowledgements are the applicant's own choice. Read the " +
            "direction first — consent to a background check is yes, requiring visa sponsorship is no. " +
            "Never assert an unstated checkable claim (work authorisation, a degree, a licence, a " +
            "clearance, employment dates); leave those empty.\n\n" +
            "Where a question carries \"options\", the answer must be exactly one of them, copied character " +
            "for character; with \"multiple\": true, a comma-separated subset. Where it is marked " +
            "\"type\": \"dropdown\" with no options, answer with the short value most likely to be in the list.\n\n" +
            "Return ONLY {\"answers\":{\"exact question text\":\"answer\"}}, keyed on the question text.\n\n" +
            "Reference data:\n" + JsonSerializer.Serialize(BuildKnownValues(),
                new JsonSerializerOptions { WriteIndented = true }) +
            "\n\nQuestions:\n" + FormQuestionsJson);
        StatusMessage = "Question prompt copied for manual recovery.";
    }

    public Dictionary<string, string> BuildFillValues()
    {
        var values = BuildKnownValues();
        foreach (var pair in ParseAnswers(CurrentAnswersJson))
        {
            // An empty answer is ChatGPT saying it had nothing to go on, not an instruction to
            // clear the field. The lookup is case-insensitive, so a blank "Email" from the model
            // used to land on top of the profile's own email and take it out of the fill entirely.
            if (string.IsNullOrWhiteSpace(pair.Value)) continue;
            values[pair.Key] = pair.Value;
        }
        return values.Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }


    /// <summary>
    /// The whole reference picture: the profile's own columns plus its education, careers and
    /// custom fields. <see cref="PersonFactsService"/> assembles both halves so the fill script and
    /// the ChatGPT prompt are answering from exactly the same facts.
    /// </summary>
    private Dictionary<string, string> BuildKnownValues()
    {
        var values = new Dictionary<string, string>(_reference, StringComparer.OrdinalIgnoreCase);
        var salary = _profiles.Current?.SalaryExpectation?.Trim() ?? "";
        if (salary.Length > 0) values["Salary expectation"] = salary;
        return values;
    }

    /// <summary>
    /// Loads the profile's personal reference data — the profile row plus its education, careers
    /// and custom fields. Public so the Profiles editor can refresh it the moment it is saved.
    /// </summary>
    public async Task LoadSavedAnswersAsync()
    {
        var profile = _profiles.Current;
        if (profile == null) return;
        _reference = await _person.BuildReferenceAsync(profile);
        SavedAnswersJson = JsonSerializer.Serialize(BuildKnownValues(),
            new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task LoadQueueAsync()
    {
        var profile = _profiles.Current;
        if (profile == null) return;
        var settings = await _settings.GetAsync();
        // Clamped: one tab is the old one-at-a-time behaviour, and past a handful the browsers cost
        // more memory than the overlap saves attention.
        MaxReviewTabs = Math.Clamp(settings.MaxReviewTabs, 1, 8);
        var items = settings.JobLinkQueues.TryGetValue(profile.Id.ToString(), out var saved)
            ? saved.Select(item => item.Clone()).ToList() : new List<JobLinkQueueItem>();
        JobQueue.Clear();
        _submittedCount = 0;
        foreach (var item in items)
        {
            if (item.Status == JobLinkQueueStatuses.InProgress) item.Status = JobLinkQueueStatuses.Queued;
            // A review waiting at shutdown cannot survive it: the filled form lived in a browser that
            // is gone, and the tab it belonged to went with it. Re-queue rather than showing a review
            // card for a page that no longer exists.
            if (item.Status == JobLinkQueueStatuses.ReadyForReview) item.Status = JobLinkQueueStatuses.Queued;
            if (item.Status == JobLinkQueueStatuses.Completed) item.Status = JobLinkQueueStatuses.Submitted;
            // Clears out links submitted by builds that kept them, so an existing queue prunes itself once.
            if (item.Status == JobLinkQueueStatuses.Submitted) continue;
            JobQueue.Add(item);
        }
        if (JobQueue.Count != items.Count) await SaveQueueAsync();
        CurrentQueueItem = JobQueue.FirstOrDefault(item => item.Status is not (JobLinkQueueStatuses.Queued or JobLinkQueueStatuses.Submitted or JobLinkQueueStatuses.Skipped));
        NotifyQueueState();
    }

    private async Task SaveQueueAsync()
    {
        var profile = _profiles.Current;
        if (profile == null) return;
        var settings = await _settings.GetForEditAsync();
        settings.JobLinkQueues[profile.Id.ToString()] = JobQueue.Select(item => item.Clone()).ToList();
        await _settings.SaveAsync(settings);
    }

    private void SetStatus(JobLinkQueueItem item, string status)
    {
        item.Status = status;
        item.UpdatedAt = DateTime.UtcNow;
        NotifyQueueState();
    }

    private void NotifyQueueState()
    {
        OnPropertyChanged(nameof(QueueSummary));
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(NeedsJobDescription));
        OnPropertyChanged(nameof(IsReadyForReview));
        OnPropertyChanged(nameof(ShowManualTools));
        OnPropertyChanged(nameof(FailedLinks));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(HasFailedLinks));
    }

    /// <summary>Entry count of an answers object or a questions array, for the trace line.</summary>
    private static int CountQuestions(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("answers", out var wrapped)) root = wrapped;
            return root.ValueKind switch
            {
                JsonValueKind.Array => root.GetArrayLength(),
                JsonValueKind.Object => root.EnumerateObject().Count(),
                _ => 0,
            };
        }
        catch (JsonException) { return -1; }
    }

    private static string NormalizeUrl(Uri uri) =>
        uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped).TrimEnd('/');

    public void RecordFill(string host, string adapter, int filled, int skipped, IReadOnlyCollection<string>? touched = null)
    {
        var mapped = touched == null || touched.Count == 0
            ? ""
            : "; mapped: " + string.Join(", ", touched.Take(8));
        var detail = $"{adapter} on {host}: {filled} filled, {skipped} skipped{mapped}";
        if (filled > 0) _activity.Success("Job Browser", "Application fields filled", detail);
        else _activity.Warning("Job Browser", "No application fields filled", detail);
    }

    public void RecordUpload(string host, string fileName) =>
        _activity.Success("Job Browser", "Resume uploaded", $"{fileName} on {host}");
    public void RecordFailure(string title, string detail) => _activity.Error("Job Browser", title, detail);
    public void RecordWarning(string title, string detail) => _activity.Warning("Job Browser", title, detail);

    private static Dictionary<string, string> ParseAnswers(string raw)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
        var root = document.RootElement;
        if (root.TryGetProperty("answers", out var wrapped)) root = wrapped;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a JSON object.");

        // Built by assignment rather than ToDictionary, which throws on a duplicate key. The keys
        // here are question text written back by ChatGPT and compared case-insensitively, so two of
        // them colliding is a thing the model can do to us at any time - and it did so where nobody
        // was watching the exception. The later value wins; there is no better rule, and losing one
        // answer beats losing the whole set.
        var answers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number
                or JsonValueKind.True or JsonValueKind.False)) continue;
            answers[property.Name] = property.Value.ToString();
        }
        return answers;
    }

    /// <summary>
    /// Names the questions ChatGPT returned nothing for.
    ///
    /// <para>
    /// A blank answer to a required question is the quietest way an application dies: the field is
    /// simply never typed, the site rejects the submission, and the reason is buried among a dozen
    /// "still empty" diagnostics. Saying it plainly at the moment the answers arrive is what turns
    /// that into something noticeable — and if the site does reject them, these are exactly the
    /// questions the correction round will carry back.
    /// </para>
    /// </summary>
    private void WarnAboutBlankAnswers(JobLinkQueueItem item, string answersJson)
    {
        try
        {
            var answers = ParseAnswers(answersJson);
            var blank = answers.Where(pair => string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => pair.Key).ToArray();
            if (blank.Length == 0) return;
            _trace.Warn("ChatGPT", $"{blank.Length} question(s) answered blank",
                string.Join(" | ", blank.Select(question =>
                    question.Length <= 90 ? question : question[..90] + "…")));
            _activity.Warning("Job Browser", "ChatGPT left application questions unanswered",
                $"{blank.Length} of {answers.Count} on {item.Url}");
        }
        catch (JsonException) { /* the reply shape is already reported by Resume Studio */ }
    }

    private static string MergeAnswers(string original, string correction)
    {
        var merged = ParseAnswers(original);
        foreach (var pair in ParseAnswers(correction))
            if (!string.IsNullOrWhiteSpace(pair.Value)) merged[pair.Key] = pair.Value.Trim();
        return JsonSerializer.Serialize(new { answers = merged });
    }
}

public sealed record JobResumePreparation(
    Guid WorkItemId,
    string JobUrl,
    string JobDescription,
    string QuestionsJson,
    string KnownAnswersJson,
    string AnswerConversationUrl = "");
