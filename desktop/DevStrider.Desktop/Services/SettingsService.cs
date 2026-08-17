using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Driver;

namespace DevStrider.Desktop.Services;

/// <summary>
/// The single <see cref="AppSettings"/> record, loaded once at startup and served from memory
/// thereafter.
///
/// <para>
/// Every credential the app holds — the shared-cluster password, the R2 token — lives on it, so
/// this is the single place they are read from. Before caching, each of the ~16 call sites
/// re-queried the database: <c>/refresh-word</c> hit it on every purple click just to read a
/// hotkey.
/// </para>
///
/// <para>
/// Storage is <see cref="SettingsStore"/>'s JSON file, not either database — see that class for
/// why. Installs created before that change keep their settings: the first load with no file
/// present imports the old MongoDB row, writes it out, and never looks at Mongo again.
/// </para>
///
/// <para>
/// <see cref="GetAsync"/> hands back the <em>cached instance</em>, not a copy — callers must treat
/// it as read-only. Anything that edits settings (the Settings form) takes
/// <see cref="GetForEditAsync"/> and passes the result to <see cref="SaveAsync"/>, which persists
/// it and installs it as the new cache.
/// </para>
/// </summary>
public class SettingsService
{
    private readonly SettingsStore _store;
    private readonly MongoContext _mongo;

    /// <summary>Guards the first load so concurrent callers don't each hit the disk.</summary>
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    /// <summary>
    /// Volatile because the listener's thread-pool threads read this while the UI thread can
    /// replace it on save; the reference swap has to be visible without a lock.
    /// </summary>
    private volatile AppSettings? _cached;

    public SettingsService(SettingsStore store, MongoContext mongo)
    {
        _store = store;
        _mongo = mongo;
    }

    /// <summary>
    /// The loaded settings, or <c>null</c> before <see cref="LoadAsync"/> completes. For code
    /// that can't await — prefer <see cref="GetAsync"/>.
    /// </summary>
    public AppSettings? Current => _cached;

    /// <summary>
    /// Populate the cache. Called once during startup, before anything else reads settings;
    /// safe to call again to force a re-read from disk.
    /// </summary>
    public async Task<AppSettings> LoadAsync()
    {
        await _loadLock.WaitAsync();
        try
        {
            _cached = await FetchOrSeedAsync();
            return _cached;
        }
        finally { _loadLock.Release(); }
    }

    /// <summary>
    /// The cached settings, loading them on first use if startup hasn't run yet. The returned
    /// instance is shared — do not mutate it; use <see cref="GetForEditAsync"/> for that.
    /// </summary>
    public async Task<AppSettings> GetAsync()
    {
        var cached = _cached;
        if (cached != null) return cached;

        await _loadLock.WaitAsync();
        try
        {
            // Re-check inside the lock: another caller may have loaded while we waited.
            return _cached ??= await FetchOrSeedAsync();
        }
        finally { _loadLock.Release(); }
    }

    /// <summary>
    /// A private copy for editing. Mutating it has no effect on anyone else until it reaches
    /// <see cref="SaveAsync"/>.
    /// </summary>
    public async Task<AppSettings> GetForEditAsync() => (await GetAsync()).Clone();

    /// <summary>Persist and install as the new cache, so every reader sees it immediately.</summary>
    public async Task SaveAsync(AppSettings s)
    {
        s.UpdatedAt = DateTime.UtcNow;
        s.DataSource = DataSources.Normalize(s.DataSource);
        await _store.SaveAsync(s);
        _cached = s;
    }

    /// <summary>
    /// Read the settings file, seeding it on first run. Order matters: the old MongoDB row is
    /// preferred over bare defaults so an existing install doesn't wake up with its shared-database
    /// password gone.
    /// </summary>
    private async Task<AppSettings> FetchOrSeedAsync()
    {
        var fromFile = _store.Load();
        if (fromFile != null)
        {
            fromFile.DataSource = DataSources.Normalize(fromFile.DataSource);
            return fromFile;
        }

        var seed = await ImportFromMongoAsync() ?? new AppSettings();
        seed.DataSource = DataSources.Normalize(seed.DataSource);
        _store.Save(seed);
        return seed;
    }

    /// <summary>
    /// One-time lift of the legacy <c>settings</c> collection into the file. Returns null when
    /// there's nothing to import — including when MongoDB isn't running, which is the expected
    /// case on an install that has already finished the migration and uninstalled it.
    /// </summary>
    private async Task<AppSettings?> ImportFromMongoAsync()
    {
        try
        {
            // Short leash. MongoContext already caps server selection, but this runs on the
            // startup path with a window waiting to appear, so it gets its own ceiling too.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await _mongo.Settings
                .Find(FilterDefinition<AppSettings>.Empty)
                .FirstOrDefaultAsync(cts.Token);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"No legacy settings to import: {ex.Message}");
            return null;
        }
    }
}
