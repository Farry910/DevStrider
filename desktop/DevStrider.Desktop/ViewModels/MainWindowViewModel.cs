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
    public ProfileViewModel Profile { get; }
    public SettingsViewModel Settings { get; }
    public ImportViewModel Import { get; }

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
        ProfileViewModel profile,
        SettingsViewModel settings,
        ImportViewModel import)
    {
        Bids = bids;
        Interviews = interviews;
        Overview = overview;
        Stats = stats;
        Profile = profile;
        Settings = settings;
        Import = import;
        Current = bids;
    }

    [RelayCommand] private void ShowBids() => Current = Bids;
    [RelayCommand] private void ShowInterviews() => Current = Interviews;
    [RelayCommand] private void ShowOverview() => Current = Overview;
    [RelayCommand] private void ShowStats() => Current = Stats;
    [RelayCommand] private void ShowProfile() => Current = Profile;
    [RelayCommand] private void ShowSettings() => Current = Settings;
    [RelayCommand] private void ShowImport() => Current = Import;
}
