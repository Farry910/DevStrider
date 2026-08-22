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
    public event Action? AutoBidCancellationRequested;
    public event Action? NewChatRequested;
    public event Action<ResumeAutomationResult>? ResumeAutomationCompleted;
    public event Action<Guid, string>? ResumeAutomationFailed;

    private ChatGptResumeRequest? _pendingRequest;
    private ChatGptResumeRequest? _activeRequest;
    private string _activeAnswersJson = "{}";
    private string _activeBidId = "";
    private bool _chatGptBrowserReady;

    public ResumeStudioViewModel(
        SettingsService settings,
        ProfileContext profiles,
        BidBoardService bids,
        WordMacroService word,
        ActivityLogService activity)
    {
        _settings = settings;
        _profiles = profiles;
        _bids = bids;
        _word = word;
        _activity = activity;
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
        string knownAnswersJson) =>
        QueueResumeRequest(workItemId, jobUrl, jobDescription, questionsJson, knownAnswersJson,
            resumeOnly: false, "");

    private void QueueResumeRequest(
        Guid workItemId,
        string jobUrl,
        string jobDescription,
        string questionsJson,
        string knownAnswersJson,
        bool resumeOnly,
        string label)
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
            startFreshChat, resumeOnly, label);

        _activeRequest = request;
        _activeAnswersJson = "{}";
        _activeBidId = "";
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
        if (!_chatGptBrowserReady || _pendingRequest == null) return;
        var request = _pendingRequest;
        _pendingRequest = null;
        AutoResumeRequested?.Invoke(request);
    }

    public async Task CompleteAutomatedResumeAsync(
        ChatGptResumeRequest request,
        string resumeReply,
        string answersJson)
    {
        if (_activeRequest?.WorkItemId != request.WorkItemId) return;
        GeneratedResume = resumeReply;

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
            var macro = await _word.RunAsync(split.ResumePart, profile.WordDocPath, profile.MacroName, profile.Name);
            if (!macro.Success) throw new InvalidOperationException("Word macro failed: " + macro.Message);

            await FinishSuccessfulRequestAsync(request, split.ResumePart, split.FastFeedLine, split.Parsed, bidId);
        }
        catch (Exception ex)
        {
            Fail(request.WorkItemId, SharedDbCredentials.Redact(ex.Message));
        }
    }

    public void ReportAutomatedResumeFailure(Guid workItemId, string message) => Fail(workItemId, message);

    private void Fail(Guid workItemId, string message)
    {
        _pendingRequest = null;
        if (_activeRequest is { StartFreshChat: true }) ResumeChatStarted = false;
        IsAutomationRunning = false;
        ShowManualRecovery = true;
        StatusMessage = message;
        _activity.Error("Resume Studio", "Resume automation failed", message);
        ResumeAutomationFailed?.Invoke(workItemId, message);
    }

    [RelayCommand]
    private void StartFreshChat()
    {
        CancelAutomation();
        ResumeChatStarted = false;
        CompletedInChat = 0;
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
        _activeRequest = null;
        _activeAnswersJson = "{}";
        _activeBidId = "";
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
        IsAutomationRunning = false;
        _activeRequest = null;
        StatusMessage = request.ResumeOnly
            ? (string.IsNullOrWhiteSpace(filePath)
                ? "Resume generated and ready to share. Configure the output root in Settings to show its file path."
                : $"Resume ready to share: {filePath}")
            : "Resume generated. Returning to the application form...";

        _activity.Success("Resume Studio", "Resume generated",
            parsed == null ? request.Label : $"{parsed.Company} - {parsed.Role}");
        ResumeAutomationCompleted?.Invoke(new ResumeAutomationResult(
            request.WorkItemId, request.JobUrl, resumeContent,
            _activeAnswersJson, filePath, bidId, request.ResumeOnly));
        _activeAnswersJson = "{}";
        _activeBidId = "";
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
    }

    private static string NormalizeAnswersJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "{}";
        raw = raw.Trim();
        if (raw.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = raw.IndexOf('\n');
            var lastFence = raw.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine) raw = raw[(firstLine + 1)..lastFence].Trim();
        }
        var objectStart = raw.IndexOf('{');
        var objectEnd = raw.LastIndexOf('}');
        if (objectStart >= 0 && objectEnd > objectStart) raw = raw[objectStart..(objectEnd + 1)];
        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.GetRawText()
                : "{}";
        }
        catch (JsonException) { return "{}"; }
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
    string Label);

public sealed record ResumeAutomationResult(
    Guid WorkItemId,
    string JobUrl,
    string ResumeContent,
    string AnswersJson,
    string ResumeFilePath,
    string BidId,
    bool ResumeOnly);
