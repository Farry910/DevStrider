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
    public SharingViewModel Sharing { get; }
    public SettingsViewModel Settings { get; }
    public ImportViewModel Import { get; }
    public AboutViewModel About { get; }
    public ActivityViewModel Activity { get; }
    public ProfilesViewModel ProfilesPage { get; }

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
        set => SetProperty(ref _current, value);
    }

    public MainWindowViewModel(
        BidBoardViewModel bids,
        InterviewPanelViewModel interviews,
        FindBidViewModel findBid,
        OverviewViewModel overview,
        StatsViewModel stats,
        SharingViewModel sharing,
        SettingsViewModel settings,
        ImportViewModel import,
        AboutViewModel about,
        ActivityViewModel activity,
        ProfilesViewModel profilesPage,
        ProfileContext profileContext)
    {
        Bids = bids;
        Interviews = interviews;
        FindBid = findBid;
        Overview = overview;
        Stats = stats;
        Sharing = sharing;
        Settings = settings;
        Import = import;
        About = about;
        Activity = activity;
        ProfilesPage = profilesPage;
        ProfileContext = profileContext;
        Current = bids;

        // Forward profile-context changes so the title-bar ComboBox + nav bindings refresh.
        ProfileContext.ProfileChanged += () => OnPropertyChanged(nameof(ActiveProfile));
        ProfileContext.ProfileListChanged += () => OnPropertyChanged(nameof(Profiles));
    }

    [RelayCommand] private void ShowBids() => Current = Bids;
    [RelayCommand] private void ShowInterviews() => Current = Interviews;
    [RelayCommand] private void ShowFindBid() => Current = FindBid;
    [RelayCommand] private void ShowOverview() => Current = Overview;
    [RelayCommand] private void ShowStats() => Current = Stats;
    [RelayCommand] private void ShowSharing() => Current = Sharing;
    [RelayCommand] private void ShowSettings() => Current = Settings;
    [RelayCommand] private void ShowImport() => Current = Import;
    [RelayCommand] private void ShowAbout() => Current = About;
    [RelayCommand] private void ShowActivity() => Current = Activity;
    [RelayCommand] private void ShowProfiles() => Current = ProfilesPage;
}
