using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;
using MongoDB.Driver;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// Drives the bids-per-10-min line chart on the Stats page. Surfaces the owner-filter chips
/// (self + each imported peer) so the view can wire them to per-line visibility.
/// </summary>
public partial class StatsViewModel : ViewModelBase
{
    private readonly StatsService _stats;
    private readonly ProfileService _profiles;
    private readonly MongoContext _db;

    public ObservableCollection<HourlySlot> Slots { get; } = new();
    public ObservableCollection<OwnerFilterItem> OwnerFilter { get; } = new();

    private DateTime _selectedDay = DateTime.Today;
    public DateTime SelectedDay
    {
        get => _selectedDay;
        set { if (SetProperty(ref _selectedDay, value)) _ = ReloadAsync(); }
    }

    public StatsViewModel(StatsService stats, ProfileService profiles, MongoContext db)
    {
        _stats = stats;
        _profiles = profiles;
        _db = db;
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            var self = (await _profiles.GetAsync()).Username;
            await BuildOwnerFilter(self);
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

    private async Task BuildOwnerFilter(string self)
    {
        // Keep prior selections sticky; only add new owners as they appear.
        if (OwnerFilter.All(o => o.Owner != self))
            OwnerFilter.Insert(0, new OwnerFilterItem(self, isSelf: true));

        var imported = await _db.ImportedSnapshots
            .Find(MongoDB.Driver.FilterDefinition<ImportedSnapshot>.Empty)
            .ToListAsync();
        foreach (var name in imported.Select(s => s.Owner).Distinct())
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
