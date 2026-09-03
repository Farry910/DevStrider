using System.IO;
using System.Text.Json;
using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Bson;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Holds newly recorded bids briefly and writes them to the shared database in batches.
///
/// <para>
/// <b>The buffer is on disk, not only in memory.</b> That is the whole design. An in-memory buffer
/// is exactly as durable as the process, and this process ends by force-killing itself — see
/// <c>App.OnExit</c>, which calls <c>Process.Kill()</c> on the normal path and again from a
/// watchdog. Add a crash, a reboot, or Task Manager, and an hour of bids that only ever lived in a
/// field would be gone with no trace that they had existed. The schema file says these tables are
/// the only copy of someone's work; a buffer that can lose them quietly is worse than no buffer.
/// So every enqueue is appended to <c>pending-bids.json</c> before the caller is told it worked,
/// and the file is only cleared once the rows are actually in Postgres.
/// </para>
///
/// <para>
/// Flush triggers, in order of which fires first: <see cref="FlushThreshold"/> bids queued, or
/// <see cref="FlushInterval"/> elapsed, or the app exiting, or the user pressing Submit now.
/// </para>
///
/// <para>
/// Pending rows are not invisible while they wait — <see cref="BidBoardService"/> merges them over
/// what the database returns, so the board, the dedup lookup and the edit path all behave as if
/// the write had already happened. A queue the user can't see is a queue they don't trust.
/// </para>
/// </summary>
public sealed class PendingBidQueue : IDisposable
{
    /// <summary>Flush once this many bids are waiting.</summary>
    public const int FlushThreshold = 5;

    /// <summary>…or once this long has passed, whichever comes first.</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromHours(1);

    private readonly IBidRepository _bids;
    private readonly SessionContext _session;
    private readonly ActivityLogService _activity;

    /// <summary>
    /// Insertion-ordered so a flush writes bids in the order they were made. Guarded by
    /// <see cref="_gate"/>: enqueues arrive on listener thread-pool threads while the timer flushes.
    /// </summary>
    private readonly Dictionary<ObjectId, UserBid> _pending = new();
    private readonly List<ObjectId> _order = new();
    private readonly object _gate = new();

    /// <summary>One flush at a time — two concurrent ones would write the same rows twice.</summary>
    private readonly SemaphoreSlim _flushLock = new(1, 1);

    private Timer? _timer;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new ObjectIdJsonConverter() },
    };

    public PendingBidQueue(IBidRepository bids, SessionContext session, ActivityLogService activity)
    {
        _bids = bids;
        _session = session;
        _activity = activity;
    }

    /// <summary>
    /// Per-user, per-machine. The account is in the filename because a shared machine must never
    /// flush one person's queued bids under the next person's login — the repositories stamp the
    /// signed-in account onto every row, so that would silently reassign their work.
    /// </summary>
    private string JournalPath => Path.Combine(
        SettingsStore.DirectoryPath, $"pending-bids-{_session.UserId}.json");

    /// <summary>How many bids are waiting. Bound by the UI so the count is never a mystery.</summary>
    public int Count { get { lock (_gate) return _order.Count; } }

    /// <summary>Raised after the queue changes, so the board can show the count.</summary>
    public event Action? Changed;

    /// <summary>
    /// Start the interval flush. Called after login, once there is an account to file rows under.
    /// </summary>
    public void Start()
    {
        _timer ??= new Timer(_ => _ = FlushAsync("interval"), null, FlushInterval, FlushInterval);
    }

    // ── the queue as the rest of the app sees it ────────────────────────────

    /// <summary>Queue a new or edited bid. Durable before this returns.</summary>
    public async Task EnqueueAsync(UserBid bid)
    {
        lock (_gate)
        {
            if (!_pending.ContainsKey(bid.Id)) _order.Add(bid.Id);
            _pending[bid.Id] = bid;
        }
        await SaveJournalAsync();
        Changed?.Invoke();

        if (Count >= FlushThreshold) await FlushAsync("threshold");
    }

    public UserBid? Get(ObjectId id)
    {
        lock (_gate) return _pending.GetValueOrDefault(id);
    }

    /// <summary>Queued rows under one profile, oldest first — merged into the board.</summary>
    public List<UserBid> ListByProfile(ObjectId profileId)
    {
        lock (_gate)
            return _order.Select(id => _pending[id])
                         .Where(b => b.ProfileId == profileId)
                         .ToList();
    }

    /// <summary>
    /// Dedup has to see the queue too. Without this, capturing the same URL twice inside one batch
    /// window would create two rows — the second lookup would miss the first, which is still only
    /// on disk here.
    /// </summary>
    public UserBid? FindByUrlNorm(ObjectId profileId, string urlNorm)
    {
        if (string.IsNullOrEmpty(urlNorm)) return null;
        lock (_gate)
            return _order.Select(id => _pending[id])
                         .FirstOrDefault(b => b.ProfileId == profileId && b.UrlNorm == urlNorm);
    }

    /// <summary>Drop a queued bid — a delete of something that never reached the database.</summary>
    public async Task<bool> RemoveAsync(ObjectId id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _pending.Remove(id);
            if (removed) _order.Remove(id);
        }
        if (!removed) return false;
        await SaveJournalAsync();
        Changed?.Invoke();
        return true;
    }

    // ── flushing ────────────────────────────────────────────────────────────

    /// <summary>
    /// Write everything queued, then clear the journal. Failures leave the queue intact and are
    /// reported to Activity: the next trigger retries, and until then the bids are still on disk.
    /// </summary>
    public async Task<int> FlushAsync(string reason)
    {
        await _flushLock.WaitAsync();
        try
        {
            List<UserBid> batch;
            lock (_gate) batch = _order.Select(id => _pending[id]).ToList();
            if (batch.Count == 0) return 0;

            var written = new List<ObjectId>();
            try
            {
                // One statement each rather than one big statement: every write is already an
                // upsert on the row's own id, so a batch that fails half way has still done real
                // work, and the ids that landed are exactly the ones to stop tracking.
                foreach (var bid in batch)
                {
                    await _bids.UpsertAsync(bid);
                    written.Add(bid.Id);
                }
            }
            catch (Exception ex)
            {
                _activity.Warning("Bids", $"{written.Count} of {batch.Count} bids submitted",
                    $"The rest stay queued and will go with the next batch. " + ex.Message);
            }

            if (written.Count > 0)
            {
                lock (_gate)
                {
                    foreach (var id in written)
                    {
                        _pending.Remove(id);
                        _order.Remove(id);
                    }
                }
                await SaveJournalAsync();
                Changed?.Invoke();
                _activity.Success("Bids", $"Submitted {written.Count} bid{(written.Count == 1 ? "" : "s")}",
                    $"Batch trigger: {reason}.", silent: true);
            }
            return written.Count;
        }
        finally { _flushLock.Release(); }
    }

    /// <summary>
    /// Last flush before the process dies. Bounded, because <c>App.OnExit</c> force-kills shortly
    /// after — and safe to lose, because anything still queued is on disk and gets picked up by
    /// <see cref="RestoreAsync"/> on the next launch.
    /// </summary>
    public void FlushOnExit(TimeSpan timeout)
    {
        try
        {
            if (Count == 0) return;
            Task.Run(() => FlushAsync("exit")).Wait(timeout);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[pending] exit flush failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reload anything a previous run left behind and try to send it. Called after login — this is
    /// what makes a crash or a force-quit cost nothing.
    /// </summary>
    public async Task RestoreAsync()
    {
        try
        {
            if (!File.Exists(JournalPath)) return;
            var text = await File.ReadAllTextAsync(JournalPath);
            if (string.IsNullOrWhiteSpace(text)) return;

            var restored = JsonSerializer.Deserialize<List<UserBid>>(text, Json) ?? new();
            if (restored.Count == 0) return;

            lock (_gate)
            {
                foreach (var bid in restored)
                {
                    if (!_pending.ContainsKey(bid.Id)) _order.Add(bid.Id);
                    _pending[bid.Id] = bid;
                }
            }
            Changed?.Invoke();
            _activity.Info("Bids", $"Recovered {restored.Count} unsent bid{(restored.Count == 1 ? "" : "s")}",
                "Left over from the last session — submitting now.");
            await FlushAsync("recovered");
        }
        catch (Exception ex)
        {
            // A corrupt journal must not take the app down, and must not be deleted either: it is
            // the only copy of whatever is in it, and a human can still read the JSON.
            _activity.Error("Bids", "Couldn't read queued bids",
                $"{JournalPath} — {ex.Message}");
        }
    }

    /// <summary>
    /// Rewrite the journal to match the queue. Temp file then move, so a crash mid-write leaves the
    /// previous journal rather than a truncated one that fails to parse.
    /// </summary>
    private async Task SaveJournalAsync()
    {
        List<UserBid> snapshot;
        lock (_gate) snapshot = _order.Select(id => _pending[id]).ToList();

        try
        {
            Directory.CreateDirectory(SettingsStore.DirectoryPath);
            if (snapshot.Count == 0)
            {
                if (File.Exists(JournalPath)) File.Delete(JournalPath);
                return;
            }
            var temp = JournalPath + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(snapshot, Json));
            File.Move(temp, JournalPath, overwrite: true);
        }
        catch (Exception ex)
        {
            // The bid is still in memory and will still be flushed; what is lost is only the
            // crash-safety. Worth saying out loud rather than swallowing.
            _activity.Warning("Bids", "Couldn't write the queued-bid file",
                $"Queued bids are held in memory only until the next batch. {ex.Message}");
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        _flushLock.Dispose();
    }
}
