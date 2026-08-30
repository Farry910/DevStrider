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
    private readonly ChatGptAccountService _accounts;
    private readonly ChatGptConversationRegistry _conversations;

    /// <summary>The run trace, so the ChatGPT view can log the steps it drives.</summary>
    public BidTraceService Trace => _trace;

    /// <summary>
    /// The settings the ChatGPT browser is built from. Read once, at the moment that browser is
    /// created — WebView2 fixes its proxy then and cannot be moved onto one afterwards.
    /// </summary>
    public AppSettings? ProxySettings => _settings.Current;

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

    /// <summary>
    /// The answer conversation the run is currently reusing, and how many applications have been
    /// answered in it.
    ///
    /// <para>
    /// Every application used to open its own answer chat, so a batch of thirty left thirty
    /// conversations in the sidebar. The question prompt is self-contained — it carries the
    /// reference data, the generated resume and the questions every time — so a chat can serve
    /// several applications without the later ones depending on the earlier. It is not unlimited:
    /// each round adds a full prompt and a full reply, and a long chat is what made the resume
    /// conversation drift off its output format in the first place.
    /// </para>
    /// </summary>
    private string _sharedAnswerConversationUrl = "";
    private int _answersInChat;

    /// <summary>How many applications one answer chat serves before a fresh one is opened.</summary>
    private const int AnswersPerChat = 3;

    /// <summary>
    /// Where the questions step should go: the work item's own conversation when it has one (a retry
    /// continues where it left off), the shared one while it has room, and a fresh chat otherwise.
    /// </summary>
    public string AnswerChatTarget(string workItemConversationUrl)
    {
        if (!string.IsNullOrWhiteSpace(workItemConversationUrl)) return workItemConversationUrl;
        if (!string.IsNullOrWhiteSpace(_sharedAnswerConversationUrl) && _answersInChat < AnswersPerChat)
        {
            _trace.Step("ChatGPT", "reusing the answer chat",
                $"{_answersInChat}/{AnswersPerChat} used: {_sharedAnswerConversationUrl}");
            return _sharedAnswerConversationUrl;
        }
        if (!string.IsNullOrWhiteSpace(_sharedAnswerConversationUrl))
            _trace.Step("ChatGPT", "answer chat is full", $"{_answersInChat} answered; opening a fresh one");
        return "https://chatgpt.com/";
    }

    /// <summary>Records which answer chat was actually used, so the next application can reuse it.</summary>
    public void NoteSharedAnswerChat(string conversationUrl)
    {
        var url = (conversationUrl ?? "").Trim();
        if (url.Length == 0) return;
        if (string.Equals(url, _sharedAnswerConversationUrl, StringComparison.OrdinalIgnoreCase))
            _answersInChat++;
        else
        {
            _sharedAnswerConversationUrl = url;
            _answersInChat = 1;
        }
        _trace.Step("ChatGPT", "answer chat in use", $"{_answersInChat}/{AnswersPerChat}: {url}");
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

    /// <summary>
    /// Which kind of work this workspace serves — <see cref="ChatGptLanes.Auto"/> or
    /// <see cref="ChatGptLanes.Manual"/>. It decides which ChatGPT account the browser signs in as,
    /// and which conversation this instance remembers, so the two never continue the same chat.
    /// </summary>
    public string Lane { get; }

    /// <summary>The account id this lane is signed in as, for the conversation claims.</summary>
    private string _accountId = ChatGptAccountService.DefaultAccountId;

    public ResumeStudioViewModel(
        SettingsService settings,
        ProfileContext profiles,
        BidBoardService bids,
        WordMacroService word,
        ActivityLogService activity,
        BidTraceService trace,
        ChatGptAccountService accounts,
        ChatGptConversationRegistry conversations,
        string lane = ChatGptLanes.Auto)
    {
        _settings = settings;
        _profiles = profiles;
        _bids = bids;
        _word = word;
        _activity = activity;
        _trace = trace;
        _accounts = accounts;
        _conversations = conversations;
        Lane = lane;
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

    /// <summary>
    /// A resume for a link the user is filling in by hand, generated without taking the screen.
    ///
    /// <para>
    /// <c>resumeOnly: false</c> so the bid is recorded — a manual bid is still a bid, and skipping
    /// the capture is the one thing that separates this from <see cref="PrepareRecruiterResume"/>.
    /// No questions are sent, because the app is not answering any: the user is looking at the form
    /// and will fill it themselves.
    /// </para>
    /// </summary>
    public void PrepareManualBidResume(Guid workItemId, string jobUrl, string jobDescription) =>
        QueueResumeRequest(workItemId, jobUrl, jobDescription, "[]", "{}",
            resumeOnly: false, "", "", background: true);

    private void QueueResumeRequest(
        Guid workItemId,
        string jobUrl,
        string jobDescription,
        string questionsJson,
        string knownAnswersJson,
        bool resumeOnly,
        string label,
        string answerConversationUrl = "",
        bool background = false)
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

        // Continuing a chat means sending the job description on its own, because the profile prompt
        // is already at the top of that conversation. That only holds if we know which conversation
        // it is. ChatGPT assigns the /c/ id after the first reply and the capture can come back
        // empty; the run then believed it had a chat, sent a bare job description, and - with no URL
        // to navigate to - sent it into whatever the pane was last pointing at, which after the
        // questions step is the previous job s answer chat. A resume tailored by a conversation that
        // never saw the resume prompt looks plausible and is wrong.
        var lostTheChat = ResumeChatStarted && string.IsNullOrWhiteSpace(ResumeConversationUrl);
        if (lostTheChat)
            _trace.Warn("ChatGPT", "resume chat url was never captured", "starting a fresh chat instead");

        // The other lane may hold this conversation — both can be signed in as one account, and
        // ChatGPT's own sidebar will happily reopen a chat that belongs to the other workspace.
        // Losing it is not a failure: it costs one profile prompt and keeps two jobs out of one
        // thread. Read from settings already in memory rather than awaited, because this runs on
        // the UI thread and the decision feeds startFreshChat immediately below.
        var mayContinue = _conversations.MayUse(_accountId, ConversationId(ResumeConversationUrl), Lane);
        if (!mayContinue)
        {
            _trace.Warn("ChatGPT", "remembered chat belongs to the other workspace",
                $"{ChatGptLanes.Label(Lane)} is starting its own instead");
            _activity.Info("ChatGPT", "Started a separate chat",
                $"{ChatGptLanes.Label(Lane)} and the other workspace are on one account, so they were "
                + "kept out of each other's conversation.");
        }

        var startFreshChat = !ResumeChatStarted || lostTheChat || !mayContinue
                             || CompletedInChat >= GenerationLimit;
        if (startFreshChat)
        {
            ResumeChatStarted = true;
            CompletedInChat = 0;
        }

        var jd = jobDescription.Trim();
        // Both are built every time. The driver sends Prompt, but if a continuation turns out to
        // point at a conversation that will not open, it needs the full one in hand right then.
        var freshPrompt = profile.ResumePrompt.Trim() + "\n\nJob description:\n\n" + jd;
        // A continuation sends the job description alone, and by the fifth turn ChatGPT had stopped
        // using the [Section]: labels the Word macro looks up — it returned a perfectly good resume
        // as prose, the shape check rejected it, and the link failed. The format instruction was
        // eight messages up the conversation by then. This restates the contract without restating
        // the whole prompt, and names no labels: each profile's prompt picks its own, so the only
        // honest reference is the shape the conversation has already been using.
        var prompt = startFreshChat
            ? freshPrompt
            : "Job description:\n\n" + jd +
              "\n\nReply in exactly the same [Section]: labelled format you used above — the same " +
              "labels, in the same order, and nothing outside them.";

        var request = new ChatGptResumeRequest(
            workItemId, prompt, freshPrompt, jobUrl, jd, questionsJson, knownAnswersJson,
            startFreshChat, resumeOnly, label, startFreshChat ? "" : ResumeConversationUrl,
            answerConversationUrl, background);

        _trace.Step("ChatGPT", startFreshChat
                ? "new chat: sending the resume prompt with this job description"
                : "same chat: sending this job description only",
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
                    // The job board, not the tool that wrote the resume.
                    var site = JobSiteApplyAdapters.SiteNameFor(request.JobUrl);
                    bid.Origin = site.Length > 0 ? site : "Job site";
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
            var macroStartedAt = DateTime.Now;
            var macro = await _word.RunAsync(split.ResumePart, profile.WordDocPath, profile.MacroName, profile.Name);
            _trace.Step("Word", "macro returned", $"success={macro.Success}: {macro.Message}");
            if (!macro.Success) throw new InvalidOperationException("Word macro failed: " + macro.Message);

            await FinishSuccessfulRequestAsync(request, split.ResumePart, split.FastFeedLine, split.Parsed, bidId,
                macroStartedAt);
        }
        catch (Exception ex)
        {
            Fail(request.WorkItemId, Safe.Redact(ex.Message));
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
        // Any failure abandons the conversation, not only one that had just started it. A
        // continuation whose reply came back in the wrong shape has drifted, and leaving it in place
        // pointed the next link at the same drifted chat: one bad reply turned into every following
        // link failing identically, three in a row, and the run stopping on a machinery streak that
        // was really one conversation going bad. Starting fresh costs one profile prompt.
        if (ResumeChatStarted)
            _trace.Step("ChatGPT", "abandoning the resume conversation",
                "its last reply was unusable; the next job starts a fresh chat");
        ResumeChatStarted = false;
        CompletedInChat = 0;
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
            var macroStartedAt = DateTime.Now;
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
            await FinishSuccessfulRequestAsync(_activeRequest, split.ResumePart, split.FastFeedLine, split.Parsed, _activeBidId,
                macroStartedAt);
        }
        finally { IsAutomationRunning = false; }
    }

    /// <summary>
    /// Records that a remembered resume chat could not be reopened and a fresh one took its place.
    ///
    /// <para>
    /// The counters move with it. The old conversation is unreachable, so its URL is worth nothing,
    /// and the generation count belongs to a chat nothing is being added to any more.
    /// </para>
    /// </summary>
    public async Task NoteResumeChatRestartedAsync(string reason)
    {
        _trace.Warn("ChatGPT", "remembered resume chat could not be opened", reason + "; started a fresh one");
        CompletedInChat = 0;
        ResumeChatStarted = true;
        await RememberConversationAsync("");
        StatusMessage = "The previous resume chat could not be opened, so a fresh one was started " +
                        "with the full profile prompt.";
    }

    /// <summary>
    /// Tears down whatever this workspace has running, for a run the operator stopped elsewhere.
    /// </summary>
    public void CancelActiveRun()
    {
        if (!IsAutomationRunning && _pendingRequest == null && _pendingAnswerCorrection == null) return;
        _trace.Warn("ChatGPT", "cancelled with the run", "the queue was stopped");
        CancelAutomation();
        StatusMessage = "Stopped with the queue.";
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
        string bidId,
        DateTime macroStartedAt)
    {
        CompletedInChat++;
        var filePath = await ResolveGeneratedResumePathAsync(fastFeedLine, macroStartedAt);
        _trace.Step("Word", "resume file resolved",
            $"folder=\"{fastFeedLine}\" -> " + (filePath.Length == 0 ? "(not found)" : filePath));

        // No file means no resume, whatever the reply looked like. This said "Resume generated" and
        // handed an empty path to the job browser anyway, which filled the form, attached nothing,
        // and submitted — an application that reads as complete and carries no resume, logged as a
        // success. The only thing worse than not applying is applying like that. A bid without its
        // resume is a failed bid, and it is reported as one.
        if (!request.ResumeOnly && string.IsNullOrWhiteSpace(filePath))
        {
            Fail(request.WorkItemId,
                "ChatGPT replied and the macro ran, but no resume file was produced. Nothing was " +
                "submitted for this link. Check the Word macro's output folder, then retry it.");
            return;
        }

        IsAutomationRunning = false;
        _activeRequest = null;
        StatusMessage = request.ResumeOnly
            ? (string.IsNullOrWhiteSpace(filePath)
                ? "Resume generated and ready to share. Configure the output root in Settings to show its file path."
                : $"Resume ready to share: {filePath}")
            : request.Background
                ? $"Resume ready for the manual bid: {filePath}"
                : "Resume generated. Returning to the application form...";

        _activity.Success("Resume Studio", "Resume generated",
            parsed == null ? request.Label : $"{parsed.Company} - {parsed.Role}");
        _trace.Ok("ChatGPT", "handing back to job browser",
            $"resumeOnly={request.ResumeOnly}, background={request.Background}");
        ResumeAutomationCompleted?.Invoke(new ResumeAutomationResult(
            request.WorkItemId, request.JobUrl, resumeContent,
            _activeAnswersJson, filePath, bidId, request.ResumeOnly,
            _activeAnswerConversationUrl, ConversationId(_activeAnswerConversationUrl),
            request.Background));
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
    private async Task<string> ResolveGeneratedResumePathAsync(string folderName, DateTime macroStartedAt)
    {
        // The profile owns this, not the machine: the macro that just wrote the file is this
        // profile s macro, and its OUTPUT_ROOT is a property of that document.
        var profile = _profiles.Current;
        var root = (profile?.ResumeOutputRoot ?? "").Trim();
        if (root.Length == 0)
        {
            _trace.Warn("Word", "no output root for this profile",
                "set it on the profile so the generated resume can be found");
            return "";
        }
        if (!Directory.Exists(root))
        {
            _trace.Warn("Word", "the profile s output root does not exist", root);
            return "";
        }

        var folder = "";
        var safeFolder = SafeFolderName(folderName);
        if (safeFolder.Length > 0)
        {
            var direct = Path.Combine(root, safeFolder);
            if (Directory.Exists(direct)) folder = direct;
        }
        // The macro decides the folder name from its own copy of the fast-feed line, so a stray
        // space or comma is enough to miss it, and the newest folder is then the best guess. The
        // window used to be ten minutes wide, which is several jobs in one run: with no fast-feed
        // line at all this walked into the previous application's folder and returned its resume.
        // The file was real, the path existed, and the run reported a successful generation while
        // attaching a resume written for a different job. Only what was written after this macro
        // started counts as this macro's output.
        var cutoff = macroStartedAt.AddSeconds(-5);
        if (folder.Length == 0) folder = NewestFolderSince(root, cutoff);
        if (folder.Length == 0)
        {
            _trace.Warn("Word", "no resume folder was written by this run",
                $"looked in {root} for anything newer than {cutoff:HH:mm:ss}" +
                (safeFolder.Length == 0 ? "; ChatGPT sent no [FolderName]: line" : $"; and for \"{safeFolder}\""));
            return "";
        }

        var preferred = (profile?.ResumeOutputFileBase ?? "").Trim();
        foreach (var pattern in new[] { "*.pdf", "*.docx", "*.doc" })
        {
            // Freshness is the test, not existence. A folder can hold last week's PDF beside the
            // .docx this run just wrote, and returning the stale one attaches the wrong resume.
            var files = Directory.EnumerateFiles(folder, pattern)
                .Where(file => File.GetLastWriteTime(file) >= cutoff).ToList();
            if (files.Count == 0) continue;
            var named = preferred.Length == 0 ? null : files.FirstOrDefault(file =>
                Path.GetFileNameWithoutExtension(file).Equals(preferred, StringComparison.OrdinalIgnoreCase));
            return Path.GetFullPath(named ?? files.OrderByDescending(File.GetLastWriteTimeUtc).First());
        }
        _trace.Warn("Word", "the resume folder holds nothing this run wrote",
            $"{folder} has no .pdf/.docx newer than {cutoff:HH:mm:ss}");
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
        _accountId = (await _accounts.ForLaneAsync(Lane)).Id;
        ResumeConversationUrl = Session(settings)?.ResumeConversationUrl ?? "";
        // The URL is remembered across restarts; the count of resumes already written in it is not.
        // Treating the remembered conversation as "started" therefore meant every launch resumed an
        // old chat believing it was 0 of 10 used, when it might hold forty. Long chats drift: the
        // [Section]: format instruction ends up far above the fold and ChatGPT stops using it, so
        // the reply is a fine resume that the Word macro cannot read, and no resume gets made at all.
        // A session begins with a fresh chat. That costs one profile prompt and is the only honest
        // reading of a counter that starts at zero.
        ResumeChatStarted = false;
        CompletedInChat = 0;
        if (!string.IsNullOrWhiteSpace(ResumeConversationUrl))
            _trace.Step("ChatGPT", "not resuming the remembered resume chat",
                "its generation count did not survive the restart; starting fresh");
    }

    /// <summary>
    /// This lane's ChatGPT session settings for the active profile, or null when there is no
    /// profile. Keyed on profile <em>and</em> lane, which is what stops two workspaces on one
    /// account reading the same remembered conversation and both continuing it.
    /// </summary>
    private ChatGptResumeSessionSettings? Session(AppSettings settings)
    {
        var profile = _profiles.Current;
        if (profile == null) return null;
        var key = ChatGptConversationRegistry.SessionKey(profile.Id.ToString(), Lane);
        return settings.ChatGptResumeSessions.TryGetValue(key, out var session) ? session : null;
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
        var key = ChatGptConversationRegistry.SessionKey(profile.Id.ToString(), Lane);
        if (!settings.ChatGptResumeSessions.TryGetValue(key, out var session))
            settings.ChatGptResumeSessions[key] = session = new ChatGptResumeSessionSettings();
        session.ResumeConversationUrl = url;
        await _settings.SaveAsync(settings);

        // Stake this lane's claim on the conversation. Two lanes on one account share a chat list,
        // and the id is the only thing that tells one of their conversations from the other's.
        var id = ConversationId(url);
        if (id.Length > 0) await _conversations.TryClaimAsync(_accountId, id, Lane);
    }

    /// <summary>
    /// Gives up this lane's conversation claims. Called when the workspace rotates to a fresh chat,
    /// so an abandoned id does not sit claimed and keep the other lane off a chat nobody is using.
    /// </summary>
    public Task ReleaseConversationClaimAsync() =>
        _conversations.ReleaseLaneAsync(_accountId, Lane);

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

/// <param name="Prompt">What to send: prompt plus JD for a fresh chat, JD alone for a continuation.</param>
/// <param name="FreshChatPrompt">
/// The full prompt plus JD, carried on every request. A continuation only works if the conversation
/// it names still opens; when it does not, the driver starts a fresh chat and needs the full prompt
/// at that moment rather than having to ask for the request to be rebuilt.
/// </param>
public sealed record ChatGptResumeRequest(
    Guid WorkItemId,
    string Prompt,
    string FreshChatPrompt,
    string JobUrl,
    string JobDescription,
    string QuestionsJson,
    string KnownAnswersJson,
    bool StartFreshChat,
    bool ResumeOnly,
    string Label,
    string ConversationUrl = "",
    string AnswerConversationUrl = "",
    bool Background = false);

/// <param name="Background">
/// This run must not take the screen. A manual bid generates its resume while the user is typing
/// into the application form in the other workspace, so the window switching that serves an
/// automatic run — jumping to Resume Studio to show the reply, then back to fill the form —
/// would move the page out from under them mid-field. Nothing about the generation differs; only
/// what the shell is allowed to do when it finishes.
/// </param>
public sealed record ResumeAutomationResult(
    Guid WorkItemId,
    string JobUrl,
    string ResumeContent,
    string AnswersJson,
    string ResumeFilePath,
    string BidId,
    bool ResumeOnly,
    string AnswerConversationUrl = "",
    string AnswerConversationId = "",
    bool Background = false);

/// <summary>
/// A request for a resume to be written in the background, for a link being applied to by hand.
/// No questions and no answers: the person looking at the form is filling it in themselves.
/// </summary>
public sealed record ManualBidResumeRequest(Guid WorkItemId, string JobUrl, string JobDescription);

/// <summary>
/// The two Resume Studio workspaces, so the container can hand out both without either resolving
/// by type — they are the same class and differ only by their lane.
/// </summary>
public sealed record ResumeStudioWorkspaces(ResumeStudioViewModel Auto, ResumeStudioViewModel Manual);

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
