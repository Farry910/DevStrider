using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// Shell view-model: owns each tab's content view-model and the currently-selected one.
/// View bindings: ContentControl Content="{Binding Current}" + buttons that call ShowX.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    public BidBoardViewModel Bids { get; }
    public InterviewPanelViewModel Interviews { get; }
    public FindBidViewModel FindBid { get; }
    public OverviewViewModel Overview { get; }
    public StatsViewModel Stats { get; }
    public SettingsViewModel Settings { get; }
    public AboutViewModel About { get; }
    public ActivityViewModel Activity { get; }
    public ProfilesViewModel ProfilesPage { get; }
    public PeersViewModel Peers { get; }
    public ResumeStudioViewModel ResumeStudio { get; }
    public AssistedAutomationViewModel AssistedAutomation { get; }
    public JobBrowserViewModel JobBrowser { get; }

    public ProfileContext ProfileContext { get; }

    /// <summary>Bound to the title-bar ComboBox. Changing it switches profile and reloads everything.</summary>
    public Profile? ActiveProfile
    {
        get => ProfileContext.Current;
        set
        {
            if (value == null || value.Id == ProfileContext.Current?.Id) return;
            _ = ProfileContext.SwitchAsync(value.Id);
        }
    }

    public ObservableCollection<Profile> Profiles => ProfileContext.All;

    /// <summary>
    /// Built from <c>&lt;Version&gt;</c> in the csproj at compile time. Rendered as "v1.x.y"
    /// next to the brand mark in the title bar so you can spot at a glance whether the
    /// build actually picked up the latest source (vs. a stale dotnet-run cache).
    /// </summary>
    public string Version =>
        "v" + (typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "?");

    private ViewModelBase _current = default!;
    public ViewModelBase Current
    {
        get => _current;
        set
        {
            if (!SetProperty(ref _current, value)) return;
            OnPropertyChanged(nameof(IsJobBrowserVisible));
            OnPropertyChanged(nameof(IsResumeStudioVisible));
            OnPropertyChanged(nameof(IsRegularViewVisible));
            OnPropertyChanged(nameof(RegularCurrent));
        }
    }

    public bool IsJobBrowserVisible => ReferenceEquals(Current, JobBrowser);
    public bool IsResumeStudioVisible => ReferenceEquals(Current, ResumeStudio);
    public bool IsRegularViewVisible => !IsJobBrowserVisible && !IsResumeStudioVisible;
    public ViewModelBase? RegularCurrent => IsRegularViewVisible ? Current : null;

    /// <summary>
    /// Runs one workspace-to-workspace handoff and makes its failure visible.
    ///
    /// <para>
    /// These were all "_ = SomeAsync(...)": started, never awaited, never looked at again. A task
    /// that faults there disappears completely — no log, no status, no failed work item, nothing to
    /// retry — and the run simply stops where it stood. That is indistinguishable from a hang, and
    /// it is what "after generating the corrected answers, nothing happens" was: the corrections
    /// came back, the handoff threw, and the exception went into a task nobody held.
    /// </para>
    ///
    /// <para>
    /// The work item is left where it is rather than failed automatically. These handoffs are the
    /// seam between two workspaces and a fault here says the seam broke, not that the application
    /// is bad; the operator can see what happened and retry the link.
    /// </para>
    /// </summary>
    private void Handoff(string what, Task work)
    {
        _ = work.ContinueWith(finished =>
        {
            var error = finished.Exception?.GetBaseException();
            if (error == null) return;
            JobBrowser.Trace.Fail("Run", what + " threw", error.ToString());
            JobBrowser.StatusMessage = what + " failed: " + error.Message +
                                       " The link is unchanged — retry it from the queue.";
        }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    public MainWindowViewModel(
        BidBoardViewModel bids,
        InterviewPanelViewModel interviews,
        FindBidViewModel findBid,
        OverviewViewModel overview,
        StatsViewModel stats,
        SettingsViewModel settings,
        AboutViewModel about,
        ActivityViewModel activity,
        ProfilesViewModel profilesPage,
        PeersViewModel peers,
        ResumeStudioViewModel resumeStudio,
        AssistedAutomationViewModel assistedAutomation,
        JobBrowserViewModel jobBrowser,
        ProfileContext profileContext)
    {
        Bids = bids;
        Interviews = interviews;
        FindBid = findBid;
        Overview = overview;
        Stats = stats;
        Settings = settings;
        About = about;
        Activity = activity;
        ProfilesPage = profilesPage;
        Peers = peers;
        ResumeStudio = resumeStudio;
        AssistedAutomation = assistedAutomation;
        JobBrowser = jobBrowser;
        ProfileContext = profileContext;
        Current = bids;
        // Each handoff brings its workspace to the front. Hidden keeps a WebView usable, but the
        // stage that is actually driving a page belongs on screen: it runs unthrottled, and the
        // user watches the resume being written and then sees the filled form they have to review.
        JobBrowser.ResumeGenerationRequested += request =>
        {
            Current = ResumeStudio;
            ResumeStudio.PrepareAutomaticApplication(
                request.WorkItemId,
                request.JobUrl,
                request.JobDescription,
                request.QuestionsJson,
                request.KnownAnswersJson,
                request.AnswerConversationUrl);
        };
        // Stopping the queue has to reach the ChatGPT driver too: the resume wait is the longest
        // thing a run has in flight, and it is owned by the other workspace.
        JobBrowser.RunCancellationRequested += ResumeStudio.CancelActiveRun;
        JobBrowser.ApplicationFillRequested += _ => Current = JobBrowser;
        JobBrowser.ApplicationRefillRequested += _ => Current = JobBrowser;
        JobBrowser.QueueNavigationRequested += () => Current = JobBrowser;
        JobBrowser.AnswerCorrectionRequested += request =>
        {
            Current = ResumeStudio;
            ResumeStudio.PrepareAnswerCorrection(request);
        };
        ResumeStudio.ResumeAutomationCompleted += result =>
        {
            if (result.ResumeOnly)
            {
                Current = ResumeStudio;
                return;
            }
            Handoff("Accepting the generated resume", JobBrowser.AcceptResumeResultAsync(result));
        };
        ResumeStudio.ResumeAutomationFailed += (workItemId, message) =>
            Handoff("Recording a resume failure", JobBrowser.MarkAutomationFailureAsync(workItemId, message));
        ResumeStudio.AnswerConversationResolved += (workItemId, url, id) =>
            Handoff("Remembering the answer conversation",
                JobBrowser.RememberAnswerConversationAsync(workItemId, url, id));
        ResumeStudio.AnswerCorrectionCompleted += result =>
            Handoff("Applying the corrected answers", JobBrowser.AcceptAnswerCorrectionAsync(result));
        ResumeStudio.AnswerCorrectionFailed += (workItemId, message) =>
            Handoff("Recording a correction failure",
                JobBrowser.MarkAnswerCorrectionFailureAsync(workItemId, message));

        // Forward profile-context changes so the title-bar ComboBox + nav bindings refresh.
        ProfileContext.ProfileChanged += () => OnPropertyChanged(nameof(ActiveProfile));
        ProfileContext.ProfileListChanged += () => OnPropertyChanged(nameof(Profiles));
    }

    [RelayCommand] private void ShowBids() => Current = Bids;
    [RelayCommand] private void ShowInterviews() => Current = Interviews;
    [RelayCommand] private void ShowFindBid() => Current = FindBid;
    [RelayCommand] private void ShowOverview() => Current = Overview;
    [RelayCommand] private void ShowStats() => Current = Stats;
    [RelayCommand] private void ShowSettings() => Current = Settings;
    [RelayCommand] private void ShowAbout() => Current = About;
    [RelayCommand] private void ShowActivity() => Current = Activity;
    [RelayCommand] private void ShowProfiles() => Current = ProfilesPage;
    [RelayCommand] private void ShowPeers() => Current = Peers;
    [RelayCommand] private void ShowResumeStudio() => Current = ResumeStudio;
    [RelayCommand] private void ShowAssistedAutomation() => Current = AssistedAutomation;
    [RelayCommand] private void ShowJobBrowser() => Current = JobBrowser;
}
