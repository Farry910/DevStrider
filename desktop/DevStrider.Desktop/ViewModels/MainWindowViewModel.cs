using CommunityToolkit.Mvvm.Input;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// Shell view-model: owns the four content view-models and the currently-selected one.
/// View bindings: ContentControl Content="{Binding Current}" + buttons that call ShowX.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    public BidBoardViewModel Bids { get; }
    public InterviewPanelViewModel Interviews { get; }
    public OverviewViewModel Overview { get; }
    public StatsViewModel Stats { get; }
    public ResumesViewModel Resumes { get; }
    public SettingsViewModel Settings { get; }
    public ImportViewModel Import { get; }

    /// <summary>
    /// Built from <c>&lt;Version&gt;</c> in the csproj at compile time. Rendered as "v1.2.0"
    /// next to the brand mark in the title bar so you can spot at a glance whether the
    /// build actually picked up the latest source (vs. a stale dotnet-run cache).
    /// </summary>
    public string Version =>
        "v" + (typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "?");

    private ViewModelBase _current = default!;
    public ViewModelBase Current
    {
        get => _current;
        set => SetProperty(ref _current, value);
    }

    public MainWindowViewModel(
        BidBoardViewModel bids,
        InterviewPanelViewModel interviews,
        OverviewViewModel overview,
        StatsViewModel stats,
        ResumesViewModel resumes,
        SettingsViewModel settings,
        ImportViewModel import)
    {
        Bids = bids;
        Interviews = interviews;
        Overview = overview;
        Stats = stats;
        Resumes = resumes;
        Settings = settings;
        Import = import;
        Current = bids;
    }

    [RelayCommand] private void ShowBids() => Current = Bids;
    [RelayCommand] private void ShowInterviews() => Current = Interviews;
    [RelayCommand] private void ShowOverview() => Current = Overview;
    [RelayCommand] private void ShowStats() => Current = Stats;
    [RelayCommand] private void ShowResumes() => Current = Resumes;
    [RelayCommand] private void ShowSettings() => Current = Settings;
    [RelayCommand] private void ShowImport() => Current = Import;
}
