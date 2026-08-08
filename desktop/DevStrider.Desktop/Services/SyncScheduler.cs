using DevStrider.Desktop.Models;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Runs <see cref="AtlasSyncService.SyncAsync"/> on a timer so peers see your activity without
/// anyone remembering to press <b>Sync now</b>.
///
/// <para>
/// A plain async loop rather than a <c>DispatcherTimer</c>: syncing is background I/O with
/// nothing to show, so there's no reason to involve the UI thread. The interval is re-read from
/// settings on every pass, so changing it in Settings takes effect on the next tick instead of
/// needing a restart.
/// </para>
///
/// <para>
/// Overlap is handled inside <see cref="AtlasSyncService"/>, which turns away a second concurrent
/// caller — so a slow scheduled run and an impatient manual click can't both advance the sync
/// marker.
/// </para>
/// </summary>
public sealed class SyncScheduler : IDisposable
{
    /// <summary>
    /// Grace period before the first sync. Startup is already doing index creation, migrations
    /// and profile init; piling a cluster round-trip on top just makes the window slower to
    /// appear.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    /// <summary>Floor on the configured interval, to stay friendly to the free Atlas tier.</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

    /// <summary>Retry cadence while the cluster is unconfigured or unreachable.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);

    private readonly AtlasSyncService _sync;
    private readonly AtlasContext _atlas;
    private readonly SettingsService _settings;
    private readonly ActivityLogService _activity;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SyncScheduler(
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

    public bool IsRunning => _cts != null;

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        if (_cts == null) return;
        try { _cts.Cancel(); } catch { /* already disposed */ }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        if (!await DelayAsync(StartupDelay, ct)) return;

        while (!ct.IsCancellationRequested)
        {
            var wait = await TickAsync();
            if (!await DelayAsync(wait, ct)) return;
        }
    }

    /// <summary>Run one pass; returns how long to wait before the next one.</summary>
    private async Task<TimeSpan> TickAsync()
    {
        try
        {
            var s = await _settings.GetAsync();
            if (s.SyncIntervalMinutes <= 0) return RetryInterval;  // disabled — re-check later

            if (!await _atlas.IsConfiguredAsync()) return RetryInterval;

            // SyncAsync never throws and logs its own outcome to Activity.
            await _sync.SyncAsync();

            var configured = TimeSpan.FromMinutes(s.SyncIntervalMinutes);
            return configured < MinInterval ? MinInterval : configured;
        }
        catch (Exception ex)
        {
            // Defensive: a scheduler that dies silently is worse than one that logs and retries.
            _activity.Warning("Atlas", "Scheduled sync failed", SharedMongoCredentials.Redact(ex.Message), silent: true);
            return RetryInterval;
        }
    }

    /// <summary>Cancellable delay. False means we were asked to stop.</summary>
    private static async Task<bool> DelayAsync(TimeSpan span, CancellationToken ct)
    {
        try
        {
            await Task.Delay(span, ct);
            return true;
        }
        catch (OperationCanceledException) { return false; }
    }

    public void Dispose() => Stop();
}
