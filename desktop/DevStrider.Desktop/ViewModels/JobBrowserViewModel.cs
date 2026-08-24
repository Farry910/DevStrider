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

    private const int ConsecutiveFailureLimit = 3;
    private int _consecutiveFailures;
    private int _submittedCount;

    public IEnumerable<JobLinkQueueItem> FailedLinks =>
        JobQueue.Where(item => item.Status == JobLinkQueueStatuses.Failed);
    public int FailedCount => FailedLinks.Count();
    public bool HasFailedLinks => FailedCount > 0;

    public bool QueueInputReadOnly => IsAutomaticQueueRunning;
    public bool IsAutomaticQueueIdle => !IsAutomaticQueueRunning;
    public bool NeedsJobDescription => CurrentQueueItem?.Status == JobLinkQueueStatuses.NeedsJobDescription;
    public bool IsReadyForReview => CurrentQueueItem?.Status == JobLinkQueueStatuses.ReadyForReview;
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
        _consecutiveFailures = 0;
        IsAutomaticQueueRunning = true;
        StatusMessage = "Automatic application flow approved. DevStrider will stop before final submission.";
        await OpenNextWorkItemAsync();
    }

    [RelayCommand]
    private void StopAutomaticQueue()
    {
        IsAutomaticQueueRunning = false;
        StatusMessage = "Automatic queue stopped. The current page and recovery controls remain available.";
        NotifyQueueState();
    }

    [RelayCommand]
    private async Task OpenNextQueuedLinkAsync()
    {
        IsAutomaticQueueRunning = false;
        await OpenNextWorkItemAsync();
    }

    private async Task OpenNextWorkItemAsync()
    {
        var item = CurrentQueueItem;
        if (item == null || item.Status is JobLinkQueueStatuses.Submitted or JobLinkQueueStatuses.Skipped or JobLinkQueueStatuses.Failed)
            item = JobQueue.FirstOrDefault(candidate => candidate.Status == JobLinkQueueStatuses.Queued);
        if (item == null)
        {
            IsAutomaticQueueRunning = false;
            CurrentQueueItem = null;
            StatusMessage = "No queued job links remain.";
            return;
        }

        CurrentQueueItem = item;
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
        var item = CurrentQueueItem;
        if (item == null || item.Status != JobLinkQueueStatuses.ReadyForReview)
        {
            StatusMessage = "There is no reviewed application waiting to be marked submitted.";
            return;
        }
        await CompleteSubmittedItemAsync(item, "Marked submitted after human review.", automatic: false);
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
        CurrentQueueItem = null;
        IsAutomaticQueueRunning = true;
        await SaveQueueAsync();
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
            SetStatus(item, JobLinkQueueStatuses.NeedsJobDescription);
            _trace.Warn("Extract", "no usable JD", "waiting for a pasted job description");
            _trace.End("needs job description");
            IsAutomaticQueueRunning = false;
            StatusMessage = "This site did not expose a usable JD. Paste it below to resume the same flow.";
            await SaveQueueAsync();
            return;
        }
        await StartResumeGenerationAsync(item, JobDescription, FormQuestionsJson);
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
        // Again before filling: an answer saved in Quick answers mid-run is a new personal fact,
        // and the fill script reads this cache synchronously.
        await LoadSavedAnswersAsync();
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
        IsAutomaticQueueRunning = false;
        await SaveQueueAsync();
        var upload = resumeUploaded ? "Resume uploaded." : "Resume upload needs manual attention.";
        StatusMessage = $"{adapter}: filled {filled}, skipped {skipped}. {upload} Automatic submission could not be confirmed. Review the visible result; submit manually if needed, then choose Mark submitted & next. {note}".Trim();
        RecordFill(new Uri(item.Url).Host, adapter, filled, skipped, touched);
        _activity.Info("Job Browser", "Human review required", item.Url);
        _trace.End("ready for review", $"filled={filled}, skipped={skipped}");
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
        var salary = _settings.Current?.SalaryExpectation?.Trim() ?? "";
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
        var items = settings.JobLinkQueues.TryGetValue(profile.Id.ToString(), out var saved)
            ? saved.Select(item => item.Clone()).ToList() : new List<JobLinkQueueItem>();
        JobQueue.Clear();
        _submittedCount = 0;
        foreach (var item in items)
        {
            if (item.Status == JobLinkQueueStatuses.InProgress) item.Status = JobLinkQueueStatuses.Queued;
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
        return root.EnumerateObject()
            .Where(p => p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            .ToDictionary(p => p.Name, p => p.Value.ToString(), StringComparer.OrdinalIgnoreCase);
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
