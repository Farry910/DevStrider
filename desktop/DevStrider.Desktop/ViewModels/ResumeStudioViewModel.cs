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

    public string SessionProgress => SessionStarted
        ? $"{CompletedInSession} of {GenerationLimit} resumes saved in this session"
        : $"Start a session to generate up to {GenerationLimit} resumes with this profile prompt.";

    public string ActiveProfileName => _profiles.Current?.Name ?? "No active profile";

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
        }
        else
        {
            _generationLimit = 5;
            _automaticallyRunWordMacro = false;
        }
        CompletedInSession = 0;
        SessionStarted = false;
        OnPropertyChanged(nameof(GenerationLimit));
        OnPropertyChanged(nameof(AutomaticallyRunWordMacro));
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
        };
        await _settings.SaveAsync(settings);
    }
}
