using System.Windows;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// User-driven ChatGPT workflow. The app keeps one embedded ChatGPT conversation available so
/// the user can paste the profile prompt once, then paste several JDs into that same conversation.
/// It intentionally does not scrape or drive ChatGPT's private DOM.
/// </summary>
public sealed partial class ResumeStudioViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly ProfileContext _profiles;
    private readonly BidBoardService _bids;
    private readonly WordMacroService _word;
    private readonly ActivityLogService _activity;

    private string _jobUrl = "";
    public string JobUrl { get => _jobUrl; set => SetProperty(ref _jobUrl, value); }

    private string _jobDescription = "";
    public string JobDescription { get => _jobDescription; set => SetProperty(ref _jobDescription, value); }

    private string _generatedResume = "";
    public string GeneratedResume { get => _generatedResume; set => SetProperty(ref _generatedResume, value); }

    private int _generationLimit = 5;
    public int GenerationLimit
    {
        get => _generationLimit;
        set
        {
            var normalized = Math.Clamp(value, 1, 10);
            if (!SetProperty(ref _generationLimit, normalized)) return;
            _ = SavePreferencesAsync();
            OnPropertyChanged(nameof(SessionProgress));
        }
    }

    private int _completedInSession;
    public int CompletedInSession { get => _completedInSession; private set { if (SetProperty(ref _completedInSession, value)) OnPropertyChanged(nameof(SessionProgress)); } }

    private bool _sessionStarted;
    public bool SessionStarted { get => _sessionStarted; private set => SetProperty(ref _sessionStarted, value); }

    private bool _automaticallyRunWordMacro;
    public bool AutomaticallyRunWordMacro
    {
        get => _automaticallyRunWordMacro;
        set
        {
            if (!SetProperty(ref _automaticallyRunWordMacro, value)) return;
            _ = SavePreferencesAsync();
        }
    }

    private bool _automaticallySubmitChatGptPrompt;
    public bool AutomaticallySubmitChatGptPrompt
    {
        get => _automaticallySubmitChatGptPrompt;
        set
        {
            if (!SetProperty(ref _automaticallySubmitChatGptPrompt, value)) return;
            if (value && !AutomaticallyRunWordMacro)
            {
                _automaticallyRunWordMacro = true;
                OnPropertyChanged(nameof(AutomaticallyRunWordMacro));
            }
            _ = SavePreferencesAsync();
        }
    }

    public string SessionProgress => SessionStarted
        ? $"{CompletedInSession} of {GenerationLimit} resumes saved in this session"
        : $"Start a session to generate up to {GenerationLimit} resumes with this profile prompt.";

    public string ActiveProfileName => _profiles.Current?.Name ?? "No active profile";

    /// <summary>Lets the view focus the already initialized ChatGPT WebView after bid handoff.</summary>
    public event Action? ChatGptFocusRequested;
    public event Action<ChatGptBidRequest>? AutoBidRequested;
    private ChatGptBidRequest? _pendingAutoBid;
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
            _ = LoadPreferencesAsync();
            OnPropertyChanged(nameof(ActiveProfileName));
        };
        _ = LoadPreferencesAsync();
    }

    [RelayCommand]
    private void StartSession()
    {
        var profile = _profiles.Current;
        if (profile == null || string.IsNullOrWhiteSpace(profile.ResumePrompt))
        {
            StatusMessage = "The active profile needs a resume prompt first.";
            return;
        }

        Clipboard.SetText(profile.ResumePrompt);
        SessionStarted = true;
        CompletedInSession = 0;
        StatusMessage = "Profile prompt copied. Paste it into the ChatGPT conversation once, then use Copy JD for each job.";
        _activity.Info("Resume Studio", "ChatGPT session started", profile.Name);
    }

    /// <summary>
    /// Starts the shortest supported handoff from a job-site page: the first job in a session
    /// copies profile prompt + JD together; later jobs copy only the JD into the same chat.
    /// </summary>
    public void PrepareBidFromJob(string jobUrl, string jobDescription)
    {
        var profile = _profiles.Current;
        if (profile == null || string.IsNullOrWhiteSpace(profile.ResumePrompt))
        {
            StatusMessage = "The active profile needs a resume prompt first.";
            return;
        }
        if (string.IsNullOrWhiteSpace(jobDescription))
        {
            StatusMessage = "No visible job description was found.";
            return;
        }
        if (SessionStarted && CompletedInSession >= GenerationLimit)
        {
            StatusMessage = $"This session reached its {GenerationLimit}-resume limit. Start a new session first.";
            return;
        }

        JobUrl = jobUrl;
        JobDescription = jobDescription.Trim();
        GeneratedResume = "";

        var isNewSession = !SessionStarted;
        var prompt = isNewSession
            ? profile.ResumePrompt.Trim() + "\n\nJob description:\n\n" + JobDescription
            : "Job description:\n\n" + JobDescription;

        if (isNewSession)
        {
            SessionStarted = true;
            CompletedInSession = 0;
            _activity.Info("Resume Studio", "Bid generation started", jobUrl);
        }
        else
        {
            _activity.Info("Resume Studio", "Next bid generation started", jobUrl);
        }

        if (AutomaticallySubmitChatGptPrompt)
        {
            _pendingAutoBid = new ChatGptBidRequest(prompt, jobUrl);
            StatusMessage = "Sending the job prompt to ChatGPT and waiting for its resume reply…";
            DispatchPendingAutoBid();
        }
        else
        {
            Clipboard.SetText(prompt);
            StatusMessage = isNewSession
                ? "Profile prompt and job description are ready. Paste once into ChatGPT, then copy its completed reply."
                : "Job description is ready. Paste it into the active ChatGPT conversation, then copy its completed reply.";
        }
        ChatGptFocusRequested?.Invoke();
    }

    public void MarkChatGptBrowserReady()
    {
        _chatGptBrowserReady = true;
        DispatchPendingAutoBid();
    }

    public async Task CompleteAutomatedBidAsync(string reply)
    {
        GeneratedResume = reply;
        await SaveDraftAsync();
    }

    public void ReportAutomatedBidFailure(string message)
    {
        StatusMessage = message;
        _activity.Error("Resume Studio", "ChatGPT automation failed", message);
    }

    private void DispatchPendingAutoBid()
    {
        if (!_chatGptBrowserReady || _pendingAutoBid == null) return;
        var request = _pendingAutoBid;
        _pendingAutoBid = null;
        AutoBidRequested?.Invoke(request);
    }

    [RelayCommand]
    private void EndSession()
    {
        SessionStarted = false;
        CompletedInSession = 0;
        StatusMessage = "ChatGPT generation session ended.";
    }

    [RelayCommand]
    private void CopyJobDescription()
    {
        if (!SessionStarted)
        {
            StatusMessage = "Start a session first so ChatGPT receives the profile prompt.";
            return;
        }
        if (CompletedInSession >= GenerationLimit)
        {
            StatusMessage = $"This session reached its {GenerationLimit}-resume limit. Start a new session.";
            return;
        }
        if (string.IsNullOrWhiteSpace(JobDescription))
        {
            StatusMessage = "Paste a job description first.";
            return;
        }

        Clipboard.SetText("Job description:\n\n" + JobDescription.Trim());
        StatusMessage = "Job description copied. Paste it into the same ChatGPT conversation, then paste its reply below.";
    }

    [RelayCommand]
    private async Task SaveDraftAsync()
    {
        if (!SessionStarted)
        {
            StatusMessage = "Start a ChatGPT session first so this draft belongs to a bounded generation session.";
            return;
        }
        if (CompletedInSession >= GenerationLimit)
        {
            StatusMessage = $"This session reached its {GenerationLimit}-resume limit. Start a new session before saving another draft.";
            return;
        }
        if (string.IsNullOrWhiteSpace(GeneratedResume))
        {
            StatusMessage = "Paste ChatGPT's complete resume reply first.";
            return;
        }
        if (string.IsNullOrWhiteSpace(JobDescription))
        {
            StatusMessage = "A job description is required to save a resume draft.";
            return;
        }

        IsBusy = true;
        try
        {
            var split = FastFeed.SplitTrailing(GeneratedResume);
            var body = split.ResumePart;
            if (string.IsNullOrWhiteSpace(body))
            {
                StatusMessage = "The pasted reply has no resume content.";
                return;
            }

            var (bid, joined) = await _bids.CaptureAsync(JobUrl, JobDescription, b =>
            {
                b.JobDescription = JobDescription.Trim();
                b.GptResumeContent = body;
                b.Origin = "ChatGPT UI";
                // A generated document is a draft, not proof that the application was submitted.
                b.Status = BidStatuses.Draft;
                if (split.Parsed == null) return;
                b.ResumeId = split.Parsed.ResumeId;
                b.Company = split.Parsed.Company;
                b.Role = split.Parsed.Role;
                b.PrimaryStacks = split.Parsed.PrimaryStacks.ToList();
            });

            CompletedInSession++;
            var macroMessage = "Draft saved.";
            if (AutomaticallyRunWordMacro)
                macroMessage = await RunMacroCoreAsync(body);

            StatusMessage = joined ? $"Existing draft updated. {macroMessage}" : macroMessage;
            _activity.Success("Resume Studio", "Resume draft saved", bid.Company.Length > 0 ? bid.Company : "ChatGPT UI");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't save draft: {SharedDbCredentials.Redact(ex.Message)}";
            _activity.Error("Resume Studio", "Draft save failed", SharedDbCredentials.Redact(ex.Message));
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task FinishFromClipboardAsync()
    {
        string reply;
        try { reply = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't read the clipboard: " + ex.Message;
            return;
        }
        if (string.IsNullOrWhiteSpace(reply))
        {
            StatusMessage = "Copy ChatGPT's completed resume reply first.";
            return;
        }
        GeneratedResume = reply;
        await SaveDraftAsync();
    }

    [RelayCommand]
    private async Task RunWordMacroAsync()
    {
        var split = FastFeed.SplitTrailing(GeneratedResume);
        IsBusy = true;
        try { StatusMessage = await RunMacroCoreAsync(split.ResumePart); }
        finally { IsBusy = false; }
    }

    private async Task<string> RunMacroCoreAsync(string resumeBody)
    {
        var profile = _profiles.Current;
        if (profile == null) return "No active profile.";
        var result = await _word.RunAsync(resumeBody, profile.WordDocPath, profile.MacroName, profile.Name);
        if (result.Success)
        {
            _activity.Success("Resume Studio", "Word resume generated", profile.Name);
            return "Word resume generated.";
        }
        _activity.Error("Resume Studio", "Word macro failed", result.Message);
        return "Word macro failed: " + result.Message;
    }

    private async Task LoadPreferencesAsync()
    {
        var profile = _profiles.Current;
        if (profile == null) return;
        var settings = await _settings.GetAsync();
        if (settings.ChatGptResumeSessions.TryGetValue(profile.Id.ToString(), out var saved))
        {
            _generationLimit = Math.Clamp(saved.GenerationLimit, 1, 10);
            _automaticallyRunWordMacro = saved.AutomaticallyRunWordMacro;
            _automaticallySubmitChatGptPrompt = saved.AutomaticallySubmitChatGptPrompt;
            if (_automaticallySubmitChatGptPrompt) _automaticallyRunWordMacro = true;
        }
        else
        {
            _generationLimit = 5;
            _automaticallyRunWordMacro = false;
            _automaticallySubmitChatGptPrompt = false;
        }
        CompletedInSession = 0;
        SessionStarted = false;
        OnPropertyChanged(nameof(GenerationLimit));
        OnPropertyChanged(nameof(AutomaticallyRunWordMacro));
        OnPropertyChanged(nameof(AutomaticallySubmitChatGptPrompt));
        OnPropertyChanged(nameof(SessionProgress));
    }

    private async Task SavePreferencesAsync()
    {
        var profile = _profiles.Current;
        if (profile == null) return;
        var settings = await _settings.GetForEditAsync();
        settings.ChatGptResumeSessions[profile.Id.ToString()] = new ChatGptResumeSessionSettings
        {
            GenerationLimit = Math.Clamp(GenerationLimit, 1, 10),
            AutomaticallyRunWordMacro = AutomaticallyRunWordMacro,
            AutomaticallySubmitChatGptPrompt = AutomaticallySubmitChatGptPrompt,
        };
        await _settings.SaveAsync(settings);
    }
}

public sealed record ChatGptBidRequest(string Prompt, string JobUrl);
