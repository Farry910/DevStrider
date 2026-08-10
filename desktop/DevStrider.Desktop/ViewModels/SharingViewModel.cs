using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Services;
using DevStrider.Desktop.Views;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// Peer sync hub. <b>Sync now</b> does one push + pull against the shared PostgreSQL database;
/// the background scheduler does the same thing hourly. The status grid reports DevStrider's own
/// tables and their row counts — the app owns no DDL, so there is nothing here that can alter or
/// remove them.
/// </summary>
public partial class SharingViewModel : ViewModelBase
{
    private readonly PeerSyncService _sync;
    private readonly SharedDbContext _shared;
    private readonly SettingsService _settings;
    private readonly ActivityLogService _activity;
    private readonly SyncScheduler _scheduler;

    public SharingViewModel(
        PeerSyncService sync,
        SharedDbContext shared,
        SettingsService settings,
        ActivityLogService activity,
        SyncScheduler scheduler)
    {
        _sync = sync;
        _shared = shared;
        _settings = settings;
        _activity = activity;
        _scheduler = scheduler;
    }

    private string _lastSyncDisplay = "Never";
    public string LastSyncDisplay { get => _lastSyncDisplay; set => SetProperty(ref _lastSyncDisplay, value); }

    /// <summary>
    /// State of the automatic half of syncing. There are two ways data moves — this scheduler
    /// and the Sync now button — and a background job you can't see is one you can't trust, so
    /// its cadence and next run are reported next to the manual control.
    /// </summary>
    private string _autoSyncDisplay = "";
    public string AutoSyncDisplay { get => _autoSyncDisplay; set => SetProperty(ref _autoSyncDisplay, value); }

    /// <summary>True once the shared database has enough configuration to connect.</summary>
    private bool _isConfigured;
    public bool IsConfigured { get => _isConfigured; set => SetProperty(ref _isConfigured, value); }

    /// <summary>Tables discovered in the shared database — feeds the Reset section grid.</summary>
    public ObservableCollection<RemoteTableRow> RemoteTables { get; } = new();

    [RelayCommand]
    public async Task LoadAsync()
    {
        var s = await _settings.GetAsync();
        IsConfigured = await _shared.IsConfiguredAsync();
        LastSyncDisplay = s.LastSyncAt > DateTime.MinValue
            ? $"{s.LastSyncAt:yyyy-MM-dd HH:mm:ss} UTC"
            : "Never";

        if (s.SyncIntervalMinutes <= 0)
        {
            AutoSyncDisplay = "Off — only the Sync now button syncs. Set an interval in Settings to enable it.";
        }
        else if (_scheduler.NextRunAt is { } next)
        {
            var mins = Math.Max(0, (int)Math.Round((next - DateTime.Now).TotalMinutes));
            AutoSyncDisplay = $"Every {s.SyncIntervalMinutes} min · next run in about {mins} min";
        }
        else
        {
            AutoSyncDisplay = $"Every {s.SyncIntervalMinutes} min · first run a couple of minutes after launch";
        }

        if (IsConfigured)
        {
            try { await LoadRemoteTablesAsync(); }
            catch { /* details go to Activity; keep the tab clean */ }
        }
        else
        {
            RemoteTables.Clear();
            StatusMessage = "Shared database isn't configured — set it in Settings → Peer database.";
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

    /// <summary>
    /// Clear the delta marker and sync, so every local bid and interview is sent again.
    ///
    /// <para>
    /// Needed because "already synced" is tracked by one timestamp on this machine, not by a flag
    /// on each row. If the shared tables are rebuilt — recreated to add a column, restored from a
    /// backup, pointed at a different server — the remote rows are gone but this machine still
    /// believes it sent them, and normal syncs push nothing for ever. Every push is an upsert on
    /// the row's own id, so re-sending is harmless.
    /// </para>
    /// </summary>
    [RelayCommand]
    public async Task ResyncAllAsync()
    {
        if (!IsConfigured) return;
        IsBusy = true;
        try
        {
            StatusMessage = "Re-sending everything…";
            var s = await _settings.GetForEditAsync();
            s.LastSyncAt = DateTime.MinValue;
            await _settings.SaveAsync(s);
            _activity.Info("Peers", "Full resync", "Delta marker cleared — every bid and interview will be pushed.");

            StatusMessage = await _sync.SyncAsync();
            await LoadAsync();
        }
        finally { IsBusy = false; }
    }

    /// <summary>List tables in the shared database with their row counts.</summary>
    [RelayCommand]
    public async Task LoadRemoteTablesAsync()
    {
        if (!IsConfigured) return;
        IsBusy = true;
        try
        {
            RemoteTables.Clear();
            // Only DevStrider's own tables. This database can be shared with other applications,
            // and listing theirs served no purpose once the drop tool was removed.
            var present = await _shared.ListTablesAsync();
            foreach (var name in SharedDbContext.OwnedTables)
            {
                RemoteTables.Add(new RemoteTableRow
                {
                    Name = name,
                    RowCount = present.Contains(name) ? await _shared.CountRowsAsync(name) : -1,
                });
            }
            var missing = SharedDbContext.OwnedTables.Where(t => !present.Contains(t)).ToList();
            StatusMessage = missing.Count > 0
                ? $"Missing: {string.Join(", ", missing)} — run shared-db-schema.sql against this database."
                : "";
        }
        catch (Exception ex)
        {
            // Full detail goes to Activity, where rows are copyable. This tab stays clean.
            StatusMessage = "";
            _activity.Error("Peers", "List tables failed", SharedDbCredentials.Redact(ex.Message));
        }
        finally { IsBusy = false; }
    }

}

/// <summary>One row in the shared-database status grid. Read-only — the app issues no DDL.</summary>
public class RemoteTableRow
{
    public string Name { get; set; } = "";
    /// <summary>-1 when the count query failed.</summary>
    public long RowCount { get; set; }
}
