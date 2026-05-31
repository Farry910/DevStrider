using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

public partial class ImportViewModel : ViewModelBase
{
    private readonly GitHubSyncService _sync;

    public ObservableCollection<DateOnly> AvailableDays { get; } = new();
    public ObservableCollection<GitHubSyncService.RepoFileMeta> AvailableFiles { get; } = new();
    public ObservableCollection<ImportedSnapshot> LocalSnapshots { get; } = new();

    private DateOnly? _selectedDay;
    public DateOnly? SelectedDay
    {
        get => _selectedDay;
        set { if (SetProperty(ref _selectedDay, value)) _ = LoadDayFilesAsync(); }
    }

    public ImportViewModel(GitHubSyncService sync)
    {
        _sync = sync;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            AvailableDays.Clear();
            foreach (var d in await _sync.ListDaysAsync()) AvailableDays.Add(d);
            LocalSnapshots.Clear();
            foreach (var s in await _sync.ListLocalSnapshotsAsync()) LocalSnapshots.Add(s);
            StatusMessage = $"{AvailableDays.Count} day folders in repo, {LocalSnapshots.Count} imported locally.";
        }
        catch (Exception ex) { StatusMessage = $"Load failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task LoadDayFilesAsync()
    {
        if (SelectedDay == null) return;
        IsBusy = true;
        try
        {
            AvailableFiles.Clear();
            foreach (var f in await _sync.ListDayAsync(SelectedDay.Value)) AvailableFiles.Add(f);
            StatusMessage = $"{AvailableFiles.Count} peer file(s) on {SelectedDay:yyyy-MM-dd}.";
        }
        catch (Exception ex) { StatusMessage = $"Failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    /// <summary>Parameter is <c>object?</c> to tolerate WPF passing <c>UnsetValue</c>; see BidBoardViewModel.</summary>
    [RelayCommand]
    public async Task ImportFileAsync(object? param)
    {
        if (param is not GitHubSyncService.RepoFileMeta meta) return;
        IsBusy = true;
        try
        {
            var snap = await _sync.ImportFileAsync(meta);
            if (snap != null) LocalSnapshots.Insert(0, snap);
            StatusMessage = $"Imported {meta.Owner} ({meta.Day:yyyy-MM-dd}).";
        }
        catch (Exception ex) { StatusMessage = $"Import failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task DeleteSnapshotAsync(object? param)
    {
        if (param is not ImportedSnapshot snap) return;
        await _sync.DeleteSnapshotAsync(snap.Id);
        LocalSnapshots.Remove(snap);
        StatusMessage = $"Removed local copy of {snap.Owner}.";
    }
}
