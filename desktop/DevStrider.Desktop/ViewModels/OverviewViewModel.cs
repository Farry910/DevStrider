using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

public partial class OverviewViewModel : ViewModelBase
{
    private readonly StatsService _stats;
    private readonly ProfileService _profiles;

    public ObservableCollection<OverviewRow> Rows { get; } = new();

    private DateTime _from = DateTime.Today.AddDays(-7);
    public DateTime From { get => _from; set { if (SetProperty(ref _from, value)) _ = ReloadAsync(); } }

    private DateTime _to = DateTime.Today;
    public DateTime To { get => _to; set { if (SetProperty(ref _to, value)) _ = ReloadAsync(); } }

    public OverviewViewModel(StatsService stats, ProfileService profiles, ProfileContext profileContext)
    {
        _stats = stats;
        _profiles = profiles;
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
            var self = (await _profiles.GetAsync()).Username;
            var fromUtc = From.ToUniversalTime();
            var toUtc = To.ToUniversalTime();
            var rows = await _stats.OverviewAsync(fromUtc, toUtc, self);
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
            StatusMessage = $"{rows.Count} owner row(s).";
        }
        finally { IsBusy = false; }
    }
}
