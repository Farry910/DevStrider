using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Data;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// Drives the bids-per-10-min line chart on the Stats page. Surfaces the owner-filter chips
/// (you + each teammate) so the view can wire them to per-line visibility.
/// </summary>
public partial class StatsViewModel : ViewModelBase
{
    private readonly StatsService _stats;
    private readonly ProfileService _account;
    private readonly IPeerDirectory _peers;

    public ObservableCollection<HourlySlot> Slots { get; } = new();
    public ObservableCollection<OwnerFilterItem> OwnerFilter { get; } = new();

    private DateTime _selectedDay = DateTime.Today;
    public DateTime SelectedDay
    {
        get => _selectedDay;
        set { if (SetProperty(ref _selectedDay, value)) _ = ReloadAsync(); }
    }

    public StatsViewModel(
        StatsService stats,
        ProfileService account,
        IPeerDirectory peers,
        ProfileContext profileContext)
    {
        _stats = stats;
        _account = account;
        _peers = peers;
        profileContext.ProfileChanged += () =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                new Action(async () => { try { await ReloadAsync(); } catch { /* ignore */ } }));
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            var self = (await _account.GetAsync()).Username;
            await BuildOwnerFilterAsync(self);
            var selected = new HashSet<string>(
                OwnerFilter.Where(o => o.IsSelected).Select(o => o.Owner));
            var slots = await _stats.BidsPer10MinAsync(
                DateOnly.FromDateTime(SelectedDay),
                selected,
                self);
            Slots.Clear();
            foreach (var s in slots) Slots.Add(s);
            StatusMessage = $"{slots.Sum(s => s.CountsByOwner.Values.Sum())} bids in {Slots.Count} slots.";
        }
        finally { IsBusy = false; }
    }

    private async Task BuildOwnerFilterAsync(string self)
    {
        // Keep prior selections sticky; only add new owners as they appear.
        if (OwnerFilter.All(o => o.Owner != self))
            OwnerFilter.Insert(0, new OwnerFilterItem(self, isSelf: true));

        // One chip per person, not per profile: identities are (person, profile) pairs and a
        // teammate running three of them is still one line on the chart.
        var identities = await _peers.ListIdentitiesAsync();
        foreach (var name in identities.Select(i => i.Username)
                                       .Where(n => !string.IsNullOrEmpty(n) && n != self)
                                       .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (OwnerFilter.All(o => o.Owner != name))
                OwnerFilter.Add(new OwnerFilterItem(name, isSelf: false));
        }
    }

    /// <summary>Parameter is <c>object?</c> to tolerate WPF passing <c>UnsetValue</c>; see BidBoardViewModel.</summary>
    [RelayCommand]
    public async Task ToggleOwnerAsync(object? param)
    {
        if (param is not OwnerFilterItem item) return;
        item.IsSelected = !item.IsSelected;
        await ReloadAsync();
    }
}

public class OwnerFilterItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string Owner { get; }
    public bool IsSelf { get; }

    private bool _isSelected = true;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    public OwnerFilterItem(string owner, bool isSelf)
    {
        Owner = owner;
        IsSelf = isSelf;
    }
}
