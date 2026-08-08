using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Driver;

namespace DevStrider.Desktop.Services;

/// <summary>
/// The singleton <see cref="AppSettings"/> row, loaded once at startup and served from memory
/// thereafter.
///
/// <para>
/// Every credential the app holds — the shared-cluster password, the R2 token — lives on that row
/// in the local database, so this is the single place they are read from. Before caching, each of
/// the ~16 call sites re-queried MongoDB: <c>/refresh-word</c> hit the database on every purple
/// click just to read a hotkey, and opening an Atlas connection cost two round-trips before it
/// sent a byte.
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
    private readonly MongoContext _db;

    /// <summary>Guards the first load so concurrent callers don't each hit MongoDB.</summary>
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    /// <summary>
    /// Volatile because the listener's thread-pool threads read this while the UI thread can
    /// replace it on save; the reference swap has to be visible without a lock.
    /// </summary>
    private volatile AppSettings? _cached;

    public SettingsService(MongoContext db) => _db = db;

    /// <summary>
    /// The loaded settings, or <c>null</c> before <see cref="LoadAsync"/> completes. For code
    /// that can't await — prefer <see cref="GetAsync"/>.
    /// </summary>
    public AppSettings? Current => _cached;

    /// <summary>
    /// Populate the cache. Called once during startup, before anything else reads settings;
    /// safe to call again to force a re-read from the database.
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
        await _db.Settings.ReplaceOneAsync(
            Builders<AppSettings>.Filter.Eq(x => x.Id, s.Id),
            s,
            new ReplaceOptions { IsUpsert = true });
        _cached = s;
    }

    /// <summary>Read the singleton row, seeding defaults on first run.</summary>
    private async Task<AppSettings> FetchOrSeedAsync()
    {
        var doc = await _db.Settings.Find(FilterDefinition<AppSettings>.Empty).FirstOrDefaultAsync();
        if (doc != null) return doc;
        var seed = new AppSettings();
        await _db.Settings.InsertOneAsync(seed);
        return seed;
    }
}
