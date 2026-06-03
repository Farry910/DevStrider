using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Services;
using DevStrider.Desktop.Views;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// Peer sync hub. One Sync button does push + pull against the shared Atlas cluster; the
/// Reset section lists every collection in the shared DB so the user can prune legacy data
/// left over from the old web app.
/// </summary>
public partial class SharingViewModel : ViewModelBase
{
    private readonly AtlasSyncService _sync;
    private readonly AtlasContext _atlas;
    private readonly SettingsService _settings;
    private readonly ActivityLogService _activity;

    public SharingViewModel(
        AtlasSyncService sync,
        AtlasContext atlas,
        SettingsService settings,
        ActivityLogService activity)
    {
        _sync = sync;
        _atlas = atlas;
        _settings = settings;
        _activity = activity;
    }

    private string _lastSyncDisplay = "Never";
    public string LastSyncDisplay { get => _lastSyncDisplay; set => SetProperty(ref _lastSyncDisplay, value); }

    /// <summary>true when <see cref="Models.AppSettings.SharedMongoUri"/> is set.</summary>
    private bool _isConfigured;
    public bool IsConfigured { get => _isConfigured; set => SetProperty(ref _isConfigured, value); }

    /// <summary>Collections discovered in the shared cluster — feeds the Reset section grid.</summary>
    public ObservableCollection<RemoteCollectionRow> RemoteCollections { get; } = new();

    [RelayCommand]
    public async Task LoadAsync()
    {
        var s = await _settings.GetAsync();
        IsConfigured = !string.IsNullOrWhiteSpace(s.SharedMongoUri);
        LastSyncDisplay = s.LastSyncAt > DateTime.MinValue
            ? $"{s.LastSyncAt:yyyy-MM-dd HH:mm:ss} UTC"
            : "Never";
        if (IsConfigured)
        {
            try { await LoadRemoteCollectionsAsync(); }
            catch (Exception ex) { StatusMessage = $"Couldn't reach shared DB: {ex.Message}"; }
        }
        else
        {
            RemoteCollections.Clear();
            StatusMessage = "Shared MongoDB URI isn't configured — set it in Settings.";
        }
    }

    [RelayCommand]
    public async Task SyncAsync()
    {
        IsBusy = true;
        try
        {
            StatusMessage = "Syncing…";
            var result = await _sync.SyncAsync();
            StatusMessage = result;
            await LoadAsync();
        }
        finally { IsBusy = false; }
    }

    /// <summary>List collections on the shared cluster + their document counts.</summary>
    [RelayCommand]
    public async Task LoadRemoteCollectionsAsync()
    {
        if (!IsConfigured) return;
        IsBusy = true;
        try
        {
            RemoteCollections.Clear();
            var names = await _atlas.ListCollectionsAsync();
            var db = await _atlas.GetDatabaseAsync();
            foreach (var name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                long count;
                try
                {
                    count = await db.GetCollection<MongoDB.Bson.BsonDocument>(name)
                        .CountDocumentsAsync(MongoDB.Driver.FilterDefinition<MongoDB.Bson.BsonDocument>.Empty);
                }
                catch { count = -1; }
                RemoteCollections.Add(new RemoteCollectionRow
                {
                    Name = name,
                    DocumentCount = count,
                    IsKept = name is "peerBids" or "peerInterviews",
                });
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't list collections: {ex.Message}";
            _activity.Error("Atlas", "List collections failed", ex.Message);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Drop every collection the user has flagged for removal.</summary>
    [RelayCommand]
    public async Task DropSelectedCollectionsAsync()
    {
        var toDrop = RemoteCollections.Where(r => r.SelectedForDrop && !r.IsKept).ToList();
        if (toDrop.Count == 0)
        {
            StatusMessage = "Nothing selected to drop.";
            return;
        }
        var names = string.Join(", ", toDrop.Select(r => r.Name));
        var ok = ConfirmDialog.Ask(
            System.Windows.Application.Current?.MainWindow,
            "Drop these collections?",
            $"Permanently drop from shared cluster:\n\n{names}\n\nThis can't be undone. " +
            "DevStrider's own collections (peerBids, peerInterviews) are protected.",
            okText: "Drop");
        if (!ok) return;

        IsBusy = true;
        try
        {
            foreach (var row in toDrop)
            {
                await _atlas.DropCollectionAsync(row.Name);
                _activity.Success("Atlas", "Dropped collection", row.Name);
            }
            await LoadRemoteCollectionsAsync();
            StatusMessage = $"Dropped {toDrop.Count} collection(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Drop failed: {ex.Message}";
            _activity.Error("Atlas", "Drop collection failed", ex.Message);
        }
        finally { IsBusy = false; }
    }
}

/// <summary>One row in the Reset-DB grid.</summary>
public class RemoteCollectionRow
{
    public string Name { get; set; } = "";
    /// <summary>-1 when the count query failed.</summary>
    public long DocumentCount { get; set; }
    /// <summary>true for the two collections DevStrider 3.x owns; checkbox is disabled.</summary>
    public bool IsKept { get; set; }
    public bool SelectedForDrop { get; set; }
}
