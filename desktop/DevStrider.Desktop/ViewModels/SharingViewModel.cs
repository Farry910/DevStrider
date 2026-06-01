using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// Upload tracker — surfaces today's sync state + the recent push history persisted in
/// <see cref="UploadLog"/>. Drives the Sharing tab in the sidebar.
/// </summary>
public partial class SharingViewModel : ViewModelBase
{
    private readonly GitHubSyncService _sync;
    private readonly ProfileService _profiles;

    public ObservableCollection<UploadLog> Items { get; } = new();

    private string _todayStatus = "Not pushed yet today.";
    public string TodayStatus { get => _todayStatus; set => SetProperty(ref _todayStatus, value); }

    public SharingViewModel(GitHubSyncService sync, ProfileService profiles)
    {
        _sync = sync;
        _profiles = profiles;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var logs = await _sync.ListUploadLogsAsync();
            Items.Clear();
            foreach (var l in logs) Items.Add(l);
            // Today's status — most recent push for the local date.
            var todayKey = DateTime.Now.ToString("yyyy-MM-dd");
            var todays = logs.FirstOrDefault(l => l.DayKey == todayKey);
            TodayStatus = todays == null
                ? "Today's snapshot hasn't been pushed yet."
                : (todays.Success
                    ? $"Today pushed at {todays.PushedAt.ToLocalTime():HH:mm} — {(todays.Encrypted ? "encrypted" : "plaintext")}."
                    : $"Today's last push failed at {todays.PushedAt.ToLocalTime():HH:mm} — {todays.Message}");
            StatusMessage = $"{logs.Count} upload entr{(logs.Count == 1 ? "y" : "ies")}.";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task PushTodayAsync()
    {
        IsBusy = true;
        try
        {
            var p = await _profiles.GetAsync();
            await _sync.PushTodayAsync(p.Username);
            StatusMessage = "Snapshot pushed.";
        }
        catch (Exception ex) { StatusMessage = $"Push failed: {ex.Message}"; }
        finally { IsBusy = false; await LoadAsync(); }
    }
}
