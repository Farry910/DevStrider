using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Lazy connection wrapper for the shared Atlas (or any reachable MongoDB) cluster that
/// hosts <c>peerBids</c> and <c>peerInterviews</c>. Doesn't hold a connection until first
/// use; safe to keep as a singleton even when the URI is empty.
///
/// <para>
/// Distinct from <see cref="MongoContext"/> (which is the local database for everything
/// else). The two are deliberately separate so changing the shared cluster doesn't
/// require restarting local features.
/// </para>
/// </summary>
public sealed class AtlasContext
{
    private readonly SettingsService _settings;
    private MongoClient? _client;
    private string _connectedUri = "";
    private string _connectedDb = "";

    public AtlasContext(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>True when <see cref="AppSettings.SharedMongoUri"/> is set.</summary>
    public async Task<bool> IsConfiguredAsync()
    {
        var s = await _settings.GetAsync();
        return !string.IsNullOrWhiteSpace(s.SharedMongoUri);
    }

    /// <summary>
    /// Returns a live <see cref="IMongoDatabase"/> against the configured shared cluster.
    /// Throws <see cref="InvalidOperationException"/> if the URI is empty. Reconnects
    /// transparently when the URI or DB name changes.
    /// </summary>
    public async Task<IMongoDatabase> GetDatabaseAsync()
    {
        var s = await _settings.GetAsync();
        var uri = (s.SharedMongoUri ?? "").Trim();
        var db = string.IsNullOrWhiteSpace(s.SharedDatabaseName) ? "devstrider-shared" : s.SharedDatabaseName.Trim();
        if (uri.Length == 0)
            throw new InvalidOperationException("Shared MongoDB URI isn't configured — set it in Settings → Peer database.");

        if (_client == null || _connectedUri != uri)
        {
            _client = new MongoClient(uri);
            _connectedUri = uri;
        }
        _connectedDb = db;
        return _client.GetDatabase(db);
    }

    public async Task<IMongoCollection<PeerBid>> PeerBidsAsync() =>
        (await GetDatabaseAsync()).GetCollection<PeerBid>("peerBids");

    public async Task<IMongoCollection<PeerInterview>> PeerInterviewsAsync() =>
        (await GetDatabaseAsync()).GetCollection<PeerInterview>("peerInterviews");

    /// <summary>
    /// Cheap reachability check — pings the cluster with a small query. Uses an aggressive
    /// 8-second timeout for both connect + server-selection so the user gets useful
    /// feedback fast instead of waiting on the driver's 30-second default.
    /// </summary>
    public async Task<(bool ok, string message)> TestConnectionAsync()
    {
        var s = await _settings.GetAsync();
        var uri = (s.SharedMongoUri ?? "").Trim();
        var dbName = string.IsNullOrWhiteSpace(s.SharedDatabaseName) ? "devstrider-shared" : s.SharedDatabaseName.Trim();
        if (uri.Length == 0)
            return (false, "Shared MongoDB URI isn't configured.");

        try
        {
            var settings = MongoClientSettings.FromConnectionString(uri);
            settings.ConnectTimeout = TimeSpan.FromSeconds(8);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(8);
            settings.SocketTimeout = TimeSpan.FromSeconds(8);
            var probeClient = new MongoClient(settings);
            await probeClient.GetDatabase(dbName).RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
            return (true, $"Connected to {dbName}.");
        }
        catch (TimeoutException ex)
        {
            return (false,
                "Timed out — the cluster didn't answer in 8s. Common causes:\n" +
                "  • Your machine's IP isn't on Atlas's IP Access List (Project → Network Access)\n" +
                "  • Firewall / VPN blocking outbound 27017\n" +
                "  • Corporate DNS blocking SRV lookups (try the non-SRV form of the URI)\n" +
                $"Underlying: {ex.Message}");
        }
        catch (MongoAuthenticationException ex)
        {
            return (false, $"Authentication rejected — check the username/password in the URI. ({ex.Message})");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>List every collection name in the configured shared DB.</summary>
    public async Task<List<string>> ListCollectionsAsync()
    {
        var db = await GetDatabaseAsync();
        var cursor = await db.ListCollectionNamesAsync();
        return await cursor.ToListAsync();
    }

    /// <summary>Drop a single collection from the shared DB. Used by the Reset-DB UI.</summary>
    public async Task DropCollectionAsync(string name)
    {
        var db = await GetDatabaseAsync();
        await db.DropCollectionAsync(name);
    }
}
