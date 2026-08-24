using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// One persistent ChatGPT resume engine. A resume chat is one ChatGPT conversation and is
/// automatically rotated after the configured number of successful resume generations.
/// </summary>
public sealed partial class ResumeStudioViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly ProfileContext _profiles;
    private readonly BidBoardService _bids;
    private readonly WordMacroService _word;
    private readonly ActivityLogService _activity;
    private readonly BidTraceService _trace;

    /// <summary>The run trace, so the ChatGPT view can log the steps it drives.</summary>
    public BidTraceService Trace => _trace;

    private string _recruiterJobDescription = "";
    public string RecruiterJobDescription
    {
        get => _recruiterJobDescription;
        set => SetProperty(ref _recruiterJobDescription, value);
    }

    private string _recruiterLabel = "";
    public string RecruiterLabel { get => _recruiterLabel; set => SetProperty(ref _recruiterLabel, value); }

    private string _generatedResume = "";
    public string GeneratedResume { get => _generatedResume; set => SetProperty(ref _generatedResume, value); }

    private string _preparedPrompt = "";
    public string PreparedPrompt { get => _preparedPrompt; private set => SetProperty(ref _preparedPrompt, value); }

    private int _generationLimit = 10;
    public int GenerationLimit
    {
        get => _generationLimit;
        private set
        {
            if (!SetProperty(ref _generationLimit, value)) return;
            OnPropertyChanged(nameof(ResumeChatProgress));
        }
    }

    private int _completedInChat;
    public int CompletedInChat
    {
        get => _completedInChat;
        private set
        {
            if (!SetProperty(ref _completedInChat, value)) return;
            OnPropertyChanged(nameof(ResumeChatProgress));
        }
    }

    private bool _resumeChatStarted;
    public bool ResumeChatStarted
    {
        get => _resumeChatStarted;
        private set
        {
            if (!SetProperty(ref _resumeChatStarted, value)) return;
            OnPropertyChanged(nameof(ResumeChatProgress));
        }
    }

    /// <summary>The /c/… conversation the resume chat lives in. Empty means no chat yet.</summary>
    public string ResumeConversationUrl { get; private set; } = "";

    private bool _isAutomationRunning;
    public bool IsAutomationRunning
    {
        get => _isAutomationRunning;
        private set
        {
            if (!SetProperty(ref _isAutomationRunning, value)) return;
            OnPropertyChanged(nameof(InputsReadOnly));
        }
    }

    private bool _showManualRecovery;
    public bool ShowManualRecovery
    {
        get => _showManualRecovery;
        set => SetProperty(ref _showManualRecovery, value);
    }

    public bool InputsReadOnly => IsAutomationRunning;
    public string ActiveProfileName => _profiles.Current?.Name ?? "No active profile";
    public string ResumeChatProgress => ResumeChatStarted
        ? $"Resume chat: {CompletedInChat} / {GenerationLimit}"
        : $"A fresh resume chat will start automatically (limit {GenerationLimit}).";

    public event Action<ChatGptResumeRequest>? AutoResumeRequested;
    public event Action<ChatGptAnswerCorrectionRequest>? AutoAnswerCorrectionRequested;
    public event Action? AutoBidCancellationRequested;
    public event Action? NewChatRequested;
    public event Action<ResumeAutomationResult>? ResumeAutomationCompleted;
    public event Action<Guid, string>? ResumeAutomationFailed;
    public event Action<ChatGptAnswerCorrectionResult>? AnswerCorrectionCompleted;
    public event Action<Guid, string>? AnswerCorrectionFailed;
    public event Action<Guid, string, string>? AnswerConversationResolved;

    private ChatGptResumeRequest? _pendingRequest;
    private ChatGptAnswerCorrectionRequest? _pendingAnswerCorrection;
    private ChatGptResumeRequest? _activeRequest;
    private string _activeAnswersJson = "{}";
    private string _activeBidId = "";
    private string _activeAnswerConversationUrl = "";
    private bool _chatGptBrowserReady;

    public ResumeStudioViewModel(
        SettingsService settings,
        ProfileContext profiles,
        BidBoardService bids,
        WordMacroService word,
        ActivityLogService activity,
        BidTraceService trace)
    {
        _settings = settings;
        _profiles = profiles;
        _bids = bids;
        _word = word;
        _activity = activity;
        _trace = trace;
        _profiles.ProfileChanged += () =>
        {
            CancelAutomation();
            ResumeChatStarted = false;
            CompletedInChat = 0;
            _ = LoadSettingsAsync();
            OnPropertyChanged(nameof(ActiveProfileName));
        };
        _ = LoadSettingsAsync();
    }

    [RelayCommand]
    private void GenerateRecruiterResume()
    {
        if (string.IsNullOrWhiteSpace(RecruiterJobDescription))
        {
            StatusMessage = "Paste the recruiter job description first.";
            return;
        }

        QueueResumeRequest(Guid.NewGuid(), "", RecruiterJobDescription, "[]", "{}",
            resumeOnly: true, RecruiterLabel);
    }

    public void PrepareAutomaticApplication(
        Guid workItemId,
        string jobUrl,
        string jobDescription,
        string questionsJson,
        string knownAnswersJson,
        string answerConversationUrl = "") =>
        QueueResumeRequest(workItemId, jobUrl, jobDescription, questionsJson, knownAnswersJson,
            resumeOnly: false, "", answerConversationUrl);

    private void QueueResumeRequest(
        Guid workItemId,
        string jobUrl,
        string jobDescription,
        string questionsJson,
        string knownAnswersJson,
        bool resumeOnly,
        string label,
        string answerConversationUrl = "")
    {
        var profile = _profiles.Current;
        if (profile == null || string.IsNullOrWhiteSpace(profile.ResumePrompt))
        {
            Fail(workItemId, "The active profile needs a resume prompt first.");
            return;
        }
        if (string.IsNullOrWhiteSpace(jobDescription))
        {
            Fail(workItemId, "A job description is required.");
            return;
        }

        var startFreshChat = !ResumeChatStarted || CompletedInChat >= GenerationLimit;
        if (startFreshChat)
        {
            ResumeChatStarted = true;
            CompletedInChat = 0;
        }

        var jd = jobDescription.Trim();
        var prompt = startFreshChat
            ? profile.ResumePrompt.Trim() + "\n\nJob description:\n\n" + jd
            : "Job description:\n\n" + jd;

        var request = new ChatGptResumeRequest(
            workItemId, prompt, jobUrl, jd, questionsJson, knownAnswersJson,
            startFreshChat, resumeOnly, label, startFreshChat ? "" : ResumeConversationUrl,
            answerConversationUrl);

        _trace.Step("ChatGPT", "request queued",
            $"freshChat={startFreshChat}, chat={CompletedInChat}/{GenerationLimit}, " +
            $"conversation={(string.IsNullOrWhiteSpace(ResumeConversationUrl) ? "(none)" : ResumeConversationUrl)}");
        _trace.Payload("ChatGPT", "resume prompt", prompt);
        _activeRequest = request;
        _activeAnswersJson = "{}";
        _activeBidId = "";
        _activeAnswerConversationUrl = "";
        _pendingRequest = request;
        PreparedPrompt = prompt;
        GeneratedResume = "";
        ShowManualRecovery = false;
        IsAutomationRunning = true;
        StatusMessage = startFreshChat
            ? "Starting a fresh ChatGPT resume chat..."
            : "Sending the next JD to the current resume chat...";

        _ = _word.PrewarmAsync(profile.WordDocPath);
        DispatchPendingRequest();
        _activity.Info("Resume Studio", resumeOnly ? "Recruiter resume started" : "Application resume started",
            string.IsNullOrWhiteSpace(jobUrl) ? label : jobUrl);
    }

    public void MarkChatGptBrowserReady()
    {
        _chatGptBrowserReady = true;
        DispatchPendingRequest();
    }

    public void MarkChatGptBrowserUnavailable() => _chatGptBrowserReady = false;

    private void DispatchPendingRequest()
    {
        if (!_chatGptBrowserReady) return;
        if (_pendingRequest != null)
        {
            var request = _pendingRequest;
            _pendingRequest = null;
            AutoResumeRequested?.Invoke(request);
            return;
        }
        if (_pendingAnswerCorrection == null) return;
        var correction = _pendingAnswerCorrection;
        _pendingAnswerCorrection = null;
        AutoAnswerCorrectionRequested?.Invoke(correction);
    }

    public void PrepareAnswerCorrection(ChatGptAnswerCorrectionRequest request)
    {
        _pendingAnswerCorrection = request;
        IsAutomationRunning = true;
        ShowManualRecovery = false;
        StatusMessage = "Reopening this application's answer chat to correct failed fields...";
        DispatchPendingRequest();
    }

    public void NoteAnswerConversation(Guid workItemId, string conversationUrl)
    {
        var id = ConversationId(conversationUrl);
        AnswerConversationResolved?.Invoke(workItemId, conversationUrl, id);
    }

    public void CompleteAnswerCorrection(ChatGptAnswerCorrectionRequest request, string answersJson,
        string conversationUrl)
    {
        var normalized = NormalizeAnswersJson(answersJson);
        if (normalized == "{}")
        {
            FailAnswerCorrection(request.WorkItemId,
                "ChatGPT did not return usable JSON for the application-field correction.");
            return;
        }
        IsAutomationRunning = false;
        StatusMessage = "Corrected answers received. Returning to the application form...";
        var id = ConversationId(conversationUrl);
        AnswerConversationResolved?.Invoke(request.WorkItemId, conversationUrl, id);
        AnswerCorrectionCompleted?.Invoke(new ChatGptAnswerCorrectionResult(
            request.WorkItemId, normalized, conversationUrl, id));
    }

    public void FailAnswerCorrection(Guid workItemId, string message)
    {
        _pendingAnswerCorrection = null;
        IsAutomationRunning = false;
        ShowManualRecovery = true;
        StatusMessage = message;
        _trace.Fail("ChatGPT", "answer correction failed", message);
        _activity.Error("Resume Studio", "Answer correction failed", message);
        AnswerCorrectionFailed?.Invoke(workItemId, message);
    }

    public async Task CompleteAutomatedResumeAsync(
        ChatGptResumeRequest request,
        string resumeReply,
        string answersJson,
        string answerConversationUrl = "")
    {
        // The reply belongs to a run that is no longer the active one — a profile switch, a fresh
        // chat, or a second request replaced it. This used to return quietly, which stranded the
        // application in Resume Studio: no Word, no handoff back to the form, no error, nothing to
        // retry. Say so and fail the work item so the queue can recover it.
        if (_activeRequest?.WorkItemId != request.WorkItemId)
        {
            const string reason = "The resume came back after its application had already been " +
                "cancelled or replaced, so it was not handed back to the job browser. " +
                "Retry that link from the queue.";
            StatusMessage = reason;
            _activity.Warning("Resume Studio", "Completed resume had no active request", reason);
            ResumeAutomationFailed?.Invoke(request.WorkItemId, reason);
            return;
        }
        GeneratedResume = resumeReply;
        _activeAnswerConversationUrl = answerConversationUrl;

        try
        {
            var split = FastFeed.SplitTrailing(resumeReply);
            if (string.IsNullOrWhiteSpace(split.ResumePart))
                throw new InvalidOperationException("ChatGPT returned no resume content.");
            // Guards the clipboard-recovery path as well as the automatic one. Without this the
            // macro spends 90s hunting labels that were never in the reply and reports a Word fault.
            if (!FastFeed.HasSectionLabels(split.ResumePart))
                throw new InvalidOperationException(
                    "ChatGPT's reply carries none of the [Section]: labels the Word macro fills in, " +
                    "so it was not sent to Word. Check the reply in Resume Studio and retry.");

            var bidId = "";
            _activeAnswersJson = NormalizeAnswersJson(answersJson);
            _trace.Step("ChatGPT", "answers normalised",
                $"in={answersJson?.Length ?? 0} chars, out={_activeAnswersJson.Length} chars");
            if (!request.ResumeOnly)
            {
                var captured = await _bids.CaptureAsync(request.JobUrl, request.JobDescription, bid =>
                {
                    bid.JobDescription = request.JobDescription;
                    bid.GptResumeContent = split.ResumePart;
                    bid.Origin = "ChatGPT UI";
                    bid.Status = BidStatuses.Draft;
                    if (split.Parsed == null) return;
                    bid.ResumeId = split.Parsed.ResumeId;
                    bid.Company = split.Parsed.Company;
                    bid.Role = split.Parsed.Role;
                    bid.PrimaryStacks = split.Parsed.PrimaryStacks.ToList();
                });
                bidId = captured.bid.Id.ToString();
                _activeBidId = bidId;
            }

            StatusMessage = "ChatGPT reply received. Generating the Word resume...";
            var profile = _profiles.Current ?? throw new InvalidOperationException("No active profile.");
            _trace.Step("Word", "running macro",
                $"{profile.MacroName} on {profile.WordDocPath} ({split.ResumePart.Length} chars)");
            var macro = await _word.RunAsync(split.ResumePart, profile.WordDocPath, profile.MacroName, profile.Name);
            _trace.Step("Word", "macro returned", $"success={macro.Success}: {macro.Message}");
            if (!macro.Success) throw new InvalidOperationException("Word macro failed: " + macro.Message);

            await FinishSuccessfulRequestAsync(request, split.ResumePart, split.FastFeedLine, split.Parsed, bidId);
        }
        catch (Exception ex)
        {
            Fail(request.WorkItemId, SharedDbCredentials.Redact(ex.Message));
        }
    }

    /// <summary>Called by the browser once it knows which conversation the resume landed in.</summary>
    public Task NoteConversationAsync(string conversationUrl) => RememberConversationAsync(conversationUrl);

    /// <summary>
    /// ChatGPT answered the form questions with something that will not parse. The resume itself is
    /// fine, so the run continues on reference data alone — but loudly, because the alternative was
    /// an application quietly missing every written answer with nothing in the log to explain it.
    /// </summary>
    public void ReportUnusableAnswers(string reply)
    {
        var snippet = string.IsNullOrWhiteSpace(reply)
            ? "(no reply captured)"
            : reply.Length <= 200 ? reply.Trim() : reply.Trim()[..200] + "...";
        var detail = "The form answers were not usable JSON, so only your saved personal data was " +
                     "filled. ChatGPT said: " + snippet.Replace('\r', ' ').Replace('\n', ' ');
        StatusMessage = detail;
        _activity.Warning("Resume Studio", "Form answers discarded", detail);
    }

    public void ReportAutomatedResumeFailure(Guid workItemId, string message) => Fail(workItemId, message);

    private void Fail(Guid workItemId, string message)
    {
        _pendingRequest = null;
        if (_activeRequest is { StartFreshChat: true }) ResumeChatStarted = false;
        IsAutomationRunning = false;
        ShowManualRecovery = true;
        StatusMessage = message;
        _trace.Fail("ChatGPT", "resume automation failed", message);
        _activity.Error("Resume Studio", "Resume automation failed", message);
        ResumeAutomationFailed?.Invoke(workItemId, message);
    }

    [RelayCommand]
    private void StartFreshChat()
    {
        CancelAutomation();
        ResumeChatStarted = false;
        CompletedInChat = 0;
        _ = RememberConversationAsync("");
        NewChatRequested?.Invoke();
        StatusMessage = "Fresh ChatGPT resume chat opened. The full profile prompt will be sent with the next JD.";
    }

    [RelayCommand]
    private void CopyPreparedPrompt()
    {
        if (string.IsNullOrWhiteSpace(PreparedPrompt))
        {
            StatusMessage = "Start a resume request first.";
            return;
        }
        Clipboard.SetText(PreparedPrompt);
        ShowManualRecovery = true;
        StatusMessage = "Prepared prompt copied for manual recovery.";
    }

    [RelayCommand]
    private async Task FinishFromClipboardAsync()
    {
        if (_activeRequest == null)
        {
            StatusMessage = "There is no resume request waiting for a manual reply.";
            return;
        }
        var reply = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        if (string.IsNullOrWhiteSpace(reply))
        {
            StatusMessage = "Copy ChatGPT's completed resume reply first.";
            return;
        }
        await CompleteAutomatedResumeAsync(_activeRequest, reply, "{}");
    }

    [RelayCommand]
    private async Task RetryWordMacroAsync()
    {
        if (string.IsNullOrWhiteSpace(GeneratedResume))
        {
            StatusMessage = "No generated resume is available.";
            return;
        }
        var profile = _profiles.Current;
        if (profile == null) return;
        IsAutomationRunning = true;
        try
        {
            var body = FastFeed.SplitTrailing(GeneratedResume).ResumePart;
            var result = await _word.RunAsync(body, profile.WordDocPath, profile.MacroName, profile.Name);
            if (!result.Success)
            {
                StatusMessage = "Word macro failed: " + result.Message;
                return;
            }
            if (_activeRequest == null)
            {
                StatusMessage = "Word resume generated.";
                return;
            }
            var split = FastFeed.SplitTrailing(GeneratedResume);
            await FinishSuccessfulRequestAsync(_activeRequest, split.ResumePart, split.FastFeedLine, split.Parsed, _activeBidId);
        }
        finally { IsAutomationRunning = false; }
    }

    private void CancelAutomation()
    {
        _pendingRequest = null;
        _pendingAnswerCorrection = null;
        _activeRequest = null;
        _activeAnswersJson = "{}";
        _activeBidId = "";
        _activeAnswerConversationUrl = "";
        IsAutomationRunning = false;
        AutoBidCancellationRequested?.Invoke();
    }

    private async Task FinishSuccessfulRequestAsync(
        ChatGptResumeRequest request,
        string resumeContent,
        string fastFeedLine,
        FastFeed.Parsed? parsed,
        string bidId)
    {
        CompletedInChat++;
        var filePath = await ResolveGeneratedResumePathAsync(fastFeedLine);
        _trace.Step("Word", "resume file resolved",
            $"folder=\"{fastFeedLine}\" -> " + (filePath.Length == 0 ? "(not found)" : filePath));
        IsAutomationRunning = false;
        _activeRequest = null;
        StatusMessage = request.ResumeOnly
            ? (string.IsNullOrWhiteSpace(filePath)
                ? "Resume generated and ready to share. Configure the output root in Settings to show its file path."
                : $"Resume ready to share: {filePath}")
            : "Resume generated. Returning to the application form...";

        _activity.Success("Resume Studio", "Resume generated",
            parsed == null ? request.Label : $"{parsed.Company} - {parsed.Role}");
        _trace.Ok("ChatGPT", "handing back to job browser", $"resumeOnly={request.ResumeOnly}");
        ResumeAutomationCompleted?.Invoke(new ResumeAutomationResult(
            request.WorkItemId, request.JobUrl, resumeContent,
            _activeAnswersJson, filePath, bidId, request.ResumeOnly,
            _activeAnswerConversationUrl, ConversationId(_activeAnswerConversationUrl)));
        _activeAnswersJson = "{}";
        _activeBidId = "";
        _activeAnswerConversationUrl = "";
    }

    /// <summary>
    /// Finds the file the macro just wrote, given only the parent folder from Settings.
    ///
    /// <para>
    /// The macro owns the file name — the shipped template saves <c>Fernando.pdf</c>, named for the
    /// person the profile is for, because that is what a recruiter sees on the attachment. This used
    /// to demand <c>{ResumeOutputFileBase}.pdf</c>, defaulting to <c>Resume.pdf</c>, so it never
    /// found anything and every application reported no generated resume. The folder holds exactly
    /// the .docx and the .pdf, so searching by extension is both simpler and correct, and the file
    /// name setting is now only a tie-break.
    /// </para>
    /// </summary>
    private async Task<string> ResolveGeneratedResumePathAsync(string folderName)
    {
        var settings = await _settings.GetAsync();
        var root = (settings.ResumeOutputRoot ?? "").Trim();
        if (root.Length == 0 || !Directory.Exists(root)) return "";

        var folder = "";
        var safeFolder = SafeFolderName(folderName);
        if (safeFolder.Length > 0)
        {
            var direct = Path.Combine(root, safeFolder);
            if (Directory.Exists(direct)) folder = direct;
        }
        // The macro decides the folder name from its own copy of the fast-feed line, so a stray
        // space or comma is enough to miss it. Whatever it just wrote is the newest folder here.
        if (folder.Length == 0) folder = NewestFolderSince(root, DateTime.Now.AddMinutes(-10));
        if (folder.Length == 0) return "";

        var preferred = (settings.ResumeOutputFileBase ?? "").Trim();
        foreach (var pattern in new[] { "*.pdf", "*.docx", "*.doc" })
        {
            var files = Directory.EnumerateFiles(folder, pattern).ToList();
            if (files.Count == 0) continue;
            var named = preferred.Length == 0 ? null : files.FirstOrDefault(file =>
                Path.GetFileNameWithoutExtension(file).Equals(preferred, StringComparison.OrdinalIgnoreCase));
            return Path.GetFullPath(named ?? files.OrderByDescending(File.GetLastWriteTimeUtc).First());
        }
        return "";
    }

    private static string SafeFolderName(string folderName)
    {
        var joined = string.Join("-", (folderName ?? "").Split(Path.GetInvalidFileNameChars(),
            StringSplitOptions.RemoveEmptyEntries));
        return new string(joined.Where(c => c is >= ' ' and <= '~').ToArray()).Trim();
    }

    private static string NewestFolderSince(string root, DateTime cutoff)
    {
        try
        {
            return new DirectoryInfo(root).EnumerateDirectories()
                .Where(directory => directory.LastWriteTime >= cutoff || directory.CreationTime >= cutoff)
                .OrderByDescending(directory => directory.LastWriteTime)
                .Select(directory => directory.FullName)
                .FirstOrDefault() ?? "";
        }
        catch { return ""; }
    }

    private async Task LoadSettingsAsync()
    {
        var settings = await _settings.GetAsync();
        GenerationLimit = Math.Clamp(settings.ResumeGenerationsPerChat, 1, 50);
        ResumeConversationUrl = Session(settings)?.ResumeConversationUrl ?? "";
        // A remembered conversation IS a started chat. Without this the first resume after a
        // restart opened a new one and threw away a chat that still had generations left in it.
        if (!string.IsNullOrWhiteSpace(ResumeConversationUrl)) ResumeChatStarted = true;
    }

    /// <summary>The active profile's ChatGPT session settings, or null when there is no profile.</summary>
    private ChatGptResumeSessionSettings? Session(AppSettings settings)
    {
        var profile = _profiles.Current;
        if (profile == null) return null;
        return settings.ChatGptResumeSessions.TryGetValue(profile.Id.ToString(), out var session)
            ? session : null;
    }

    /// <summary>
    /// Remembers which conversation the resume chat is in, per profile. Every resume after the
    /// first depends on the profile prompt sent at the top of that chat, so the URL is the only
    /// thing that makes "continue the same chat" mean anything.
    /// </summary>
    private async Task RememberConversationAsync(string conversationUrl)
    {
        var profile = _profiles.Current;
        if (profile == null) return;
        var url = (conversationUrl ?? "").Trim();
        if (string.Equals(url, ResumeConversationUrl, StringComparison.OrdinalIgnoreCase)) return;

        ResumeConversationUrl = url;
        var settings = await _settings.GetForEditAsync();
        var key = profile.Id.ToString();
        if (!settings.ChatGptResumeSessions.TryGetValue(key, out var session))
            settings.ChatGptResumeSessions[key] = session = new ChatGptResumeSessionSettings();
        session.ResumeConversationUrl = url;
        await _settings.SaveAsync(settings);
    }

    /// <summary>
    /// The answers object from a rendered ChatGPT reply, or <c>{}</c>. Delegates to
    /// <see cref="AnswerJson"/> so the wait predicate and this agree on what counts as usable —
    /// they disagreed before, and the wait then spun until its timeout on a reply it would have
    /// accepted here.
    /// </summary>
    private static string NormalizeAnswersJson(string raw) => AnswerJson.Extract(raw);

    private static string ConversationId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.FindIndex(parts, part => part.Equals("c", StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < parts.Length ? parts[index + 1] : "";
    }
}

public sealed record ChatGptResumeRequest(
    Guid WorkItemId,
    string Prompt,
    string JobUrl,
    string JobDescription,
    string QuestionsJson,
    string KnownAnswersJson,
    bool StartFreshChat,
    bool ResumeOnly,
    string Label,
    string ConversationUrl = "",
    string AnswerConversationUrl = "");

public sealed record ResumeAutomationResult(
    Guid WorkItemId,
    string JobUrl,
    string ResumeContent,
    string AnswersJson,
    string ResumeFilePath,
    string BidId,
    bool ResumeOnly,
    string AnswerConversationUrl = "",
    string AnswerConversationId = "");

public sealed record ChatGptAnswerCorrectionRequest(
    Guid WorkItemId,
    string ConversationUrl,
    string ConversationId,
    string QuestionsJson,
    string KnownAnswersJson,
    string CurrentAnswersJson,
    string JobDescription);

public sealed record ChatGptAnswerCorrectionResult(
    Guid WorkItemId,
    string AnswersJson,
    string ConversationUrl,
    string ConversationId);
