using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// Owns the durable application queue. Browser code extracts and fills the page; this class
/// advances each work item and stops at the two human checkpoints: missing JD and final submit.
/// </summary>
public sealed partial class JobBrowserViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly ProfileContext _profiles;
    private readonly ActivityLogService _activity;
    private readonly BidBoardService _bids;
    private readonly FormAnswerService _answers;

    /// <summary>
    /// The answer bank, keyed on the normalised question. Cached per profile because the fill
    /// script needs it synchronously, and refreshed whenever the profile or the bank changes.
    /// </summary>
    private Dictionary<string, string> _answerBank = new(StringComparer.OrdinalIgnoreCase);

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

    public JobBrowserViewModel(SettingsService settings, ProfileContext profiles, ActivityLogService activity,
        BidBoardService bids, FormAnswerService answers)
    {
        _settings = settings;
        _profiles = profiles;
        _activity = activity;
        _bids = bids;
        _answers = answers;
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
        SetStatus(item, JobLinkQueueStatuses.Loading);
        Address = item.Url;
        JobDescription = item.JobDescription;
        FormQuestionsJson = item.FormQuestionsJson;
        CurrentAnswersJson = item.AnswersJson;
        SelectedResumePath = item.ResumeFilePath;
        FallbackJobDescription = "";
        await SaveQueueAsync();
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
        SetStatus(item, JobLinkQueueStatuses.Submitted);
        if (ObjectId.TryParse(item.BidId, out var bidId))
            await _bids.UpdateAsync(bidId, bid => bid.Status = BidStatuses.Applied);
        _activity.Success("Job Browser", "Application submitted", item.Url);
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

        if (string.IsNullOrWhiteSpace(JobDescription))
        {
            SetStatus(item, JobLinkQueueStatuses.NeedsJobDescription);
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
        SetStatus(item, JobLinkQueueStatuses.GeneratingResume);
        await SaveQueueAsync();
        StatusMessage = "Generating the tailored resume in ChatGPT...";
        ResumeGenerationRequested?.Invoke(new JobResumePreparation(
            item.Id, item.Url, item.JobDescription, item.FormQuestionsJson,
            JsonSerializer.Serialize(BuildKnownValues())));
    }

    public async Task AcceptResumeResultAsync(ResumeAutomationResult result)
    {
        var item = JobQueue.FirstOrDefault(x => x.Id == result.WorkItemId);
        if (item == null || result.ResumeOnly) return;
        CurrentQueueItem = item;
        item.AnswersJson = result.AnswersJson;
        item.ResumeFilePath = result.ResumeFilePath;
        item.BidId = result.BidId;
        CurrentAnswersJson = result.AnswersJson;
        SelectedResumePath = result.ResumeFilePath;
        await BankGeneratedAnswersAsync(item, result.AnswersJson);
        SetStatus(item, JobLinkQueueStatuses.FillingApplication);
        await SaveQueueAsync();
        StatusMessage = "Resume ready. Filling the application and uploading the resume...";
        ApplicationFillRequested?.Invoke(result);
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
        // Whatever the page still wants becomes a question in Job Operations, so the next
        // application of the same kind fills it instead of asking again.
        await BankOutstandingAsync(item, unfilled);
        SetStatus(item, JobLinkQueueStatuses.ReadyForReview);
        _consecutiveFailures = 0;
        IsAutomaticQueueRunning = false;
        await SaveQueueAsync();
        var upload = resumeUploaded ? "Resume uploaded." : "Resume upload needs manual attention.";
        StatusMessage = $"{adapter}: filled {filled}, skipped {skipped}. {upload} Review every field, submit on the site, then choose Mark submitted & next. {note}".Trim();
        RecordFill(new Uri(item.Url).Host, adapter, filled, skipped, touched);
        _activity.Info("Job Browser", "Human review required", item.Url);
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
        Clipboard.SetText("Answer only from known facts. Return ONLY a JSON object mapping the exact question to its answer. " +
                          "Do not answer legal attestations, demographic, disability, veteran, salary, work-authorization, sponsorship, or signature questions unless an exact saved answer is supplied.\n\n" + FormQuestionsJson);
        StatusMessage = "Question prompt copied for manual recovery.";
    }

    [RelayCommand]
    private async Task SaveAnswersAsync()
    {
        try
        {
            var profile = _profiles.Current;
            if (profile == null) { StatusMessage = "No active profile."; return; }
            var answers = ParseAnswers(SavedAnswersJson);
            foreach (var pair in answers)
                await _answers.SaveUserAnswerAsync(profile.Id, pair.Key, pair.Value);
            await LoadSavedAnswersAsync();
            StatusMessage = $"Saved {answers.Count} reusable answer(s) to the shared database.";
        }
        catch (JsonException ex) { StatusMessage = "Saved answers must be valid JSON: " + ex.Message; }
    }

    public Dictionary<string, string> BuildFillValues()
    {
        var values = BuildKnownValues();
        foreach (var pair in ParseAnswers(CurrentAnswersJson)) values[pair.Key] = pair.Value;
        return values.Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Saves what ChatGPT answered so the same question fills itself next time. They land
    /// unapproved: the Answers tab is where they are reviewed and promoted.
    /// </summary>
    private async Task BankGeneratedAnswersAsync(JobLinkQueueItem item, string answersJson)
    {
        var profile = _profiles.Current;
        if (profile == null) return;
        var site = Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) ? uri.Host : "";
        try
        {
            foreach (var pair in ParseAnswers(answersJson))
            {
                if (string.IsNullOrWhiteSpace(pair.Value)) continue;
                await _answers.RecordGeneratedAsync(profile.Id, pair.Key, pair.Value, site);
            }
        }
        catch (JsonException) { /* a malformed reply is already reported by Resume Studio */ }
        await LoadSavedAnswersAsync();
    }

    private async Task BankOutstandingAsync(JobLinkQueueItem item, IReadOnlyCollection<string>? unfilled)
    {
        var profile = _profiles.Current;
        if (profile == null || unfilled == null || unfilled.Count == 0) return;
        var site = Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) ? uri.Host : "";
        foreach (var question in unfilled)
        {
            // The fill script tags dropdowns for the review line; the question itself is the key.
            var text = question.EndsWith(" (dropdown)", StringComparison.OrdinalIgnoreCase)
                ? question[..^11] : question;
            await _answers.RecordOutstandingAsync(profile.Id, text, site);
        }
        await LoadSavedAnswersAsync();
    }

    private Dictionary<string, string> BuildKnownValues()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var profile = _profiles.Current;
        if (profile != null)
        {
            var names = profile.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            values["full name"] = profile.Name;
            if (names.Length > 0) values["first name"] = names[0];
            if (names.Length > 1) values["last name"] = names[^1];
            values["email"] = profile.PersonalEmail;
            values["phone"] = profile.Phone;
            values["location"] = profile.Location;
            values["linkedin"] = profile.LinkedinUrl;
            values["headline"] = profile.Headline;
        }
        // The bank last: a question answered explicitly beats a profile field that merely
        // resembles it, and it is the only source for anything the profile has no column for.
        foreach (var pair in _answerBank) values[pair.Key] = pair.Value;
        return values;
    }

    /// <summary>
    /// Loads the answer bank for the active profile. Unapproved ChatGPT answers are included:
    /// approving the automatic flow is the decision to trust them, and the Answers tab is where
    /// they are reviewed afterwards.
    /// </summary>
    public async Task LoadSavedAnswersAsync()
    {
        var profile = _profiles.Current;
        if (profile == null) return;
        _answerBank = await _answers.BuildLookupAsync(profile.Id, approvedOnly: false);
        SavedAnswersJson = JsonSerializer.Serialize(_answerBank, new JsonSerializerOptions { WriteIndented = true });
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
}

public sealed record JobResumePreparation(
    Guid WorkItemId,
    string JobUrl,
    string JobDescription,
    string QuestionsJson,
    string KnownAnswersJson);
