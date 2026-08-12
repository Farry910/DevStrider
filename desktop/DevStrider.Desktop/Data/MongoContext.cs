using DevStrider.Desktop.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;

namespace DevStrider.Desktop.Data;

/// <summary>
/// Holds the live <see cref="IMongoDatabase"/> + typed collection accessors. One instance per
/// app process; constructed once at startup and registered in DI. Reconnect-on-settings-change
/// is intentionally not handled — restart the app after editing the connection string.
/// </summary>
public class MongoContext
{
    private static int _registered;

    public MongoContext(string connectionString, string databaseName)
    {
        if (System.Threading.Interlocked.Exchange(ref _registered, 1) == 0)
        {
            RegisterConventions();
        }

        var client = new MongoClient(connectionString);
        Database = client.GetDatabase(databaseName);
    }

    public IMongoDatabase Database { get; }

    public IMongoCollection<UserProfile> Profiles =>
        Database.GetCollection<UserProfile>("profiles");

    /// <summary>
    /// Bidding identities (new multi-profile system, 2026-06+). Distinct from the legacy
    /// <see cref="Profiles"/> collection which is a singleton holding the team-repo nickname.
    /// </summary>
    public IMongoCollection<Profile> BidProfiles =>
        Database.GetCollection<Profile>("bidProfiles");

    public IMongoCollection<GroupLink> Links =>
        Database.GetCollection<GroupLink>("links");

    public IMongoCollection<UserBid> Bids =>
        Database.GetCollection<UserBid>("bids");

    public IMongoCollection<Interview> Interviews =>
        Database.GetCollection<Interview>("interviews");

    public IMongoCollection<Achievement> Achievements =>
        Database.GetCollection<Achievement>("achievements");

    public IMongoCollection<AppSettings> Settings =>
        Database.GetCollection<AppSettings>("settings");

    /// <summary>Local mirror of peer bids pulled from the shared Atlas cluster.</summary>
    public IMongoCollection<PeerBid> PeerBids =>
        Database.GetCollection<PeerBid>("peerBids");

    /// <summary>Local mirror of peer interviews pulled from the shared Atlas cluster.</summary>
    public IMongoCollection<PeerInterview> PeerInterviews =>
        Database.GetCollection<PeerInterview>("peerInterviews");

    /// <summary>Local mirror of published team identities — drives the Peers tab's user picker.</summary>
    public IMongoCollection<PeerUser> PeerUsers =>
        Database.GetCollection<PeerUser>("peerUsers");

    /// <summary>
    /// Camel-case + ignore-unknown-fields so we can round-trip JSON payloads (export/import)
    /// without forcing the deserializer to know every property the producer might add later.
    /// </summary>
    private static void RegisterConventions()
    {
        var pack = new ConventionPack
        {
            new CamelCaseElementNameConvention(),
            new IgnoreExtraElementsConvention(true),
            new EnumRepresentationConvention(BsonType.String),
        };
        ConventionRegistry.Register("DevStriderConventions", pack, _ => true);
    }

    public async Task EnsureIndexesAsync()
    {
        await Links.Indexes.CreateOneAsync(new CreateIndexModel<GroupLink>(
            Builders<GroupLink>.IndexKeys.Ascending(x => x.UrlNorm),
            new CreateIndexOptions { Unique = false }));

        await Bids.Indexes.CreateOneAsync(new CreateIndexModel<UserBid>(
            Builders<UserBid>.IndexKeys.Ascending(x => x.GroupLinkId)));
        await Bids.Indexes.CreateOneAsync(new CreateIndexModel<UserBid>(
            Builders<UserBid>.IndexKeys.Descending(x => x.UpdatedAt)));
        await Bids.Indexes.CreateOneAsync(new CreateIndexModel<UserBid>(
            Builders<UserBid>.IndexKeys.Ascending(x => x.AppliedAt)));

        await Interviews.Indexes.CreateOneAsync(new CreateIndexModel<Interview>(
            Builders<Interview>.IndexKeys.Ascending(x => x.BidId)));
        await Interviews.Indexes.CreateOneAsync(new CreateIndexModel<Interview>(
            Builders<Interview>.IndexKeys.Descending(x => x.ScheduledDate)));
        // "Every round of this hiring process" is the query the Interviews tab groups by.
        await Interviews.Indexes.CreateOneAsync(new CreateIndexModel<Interview>(
            Builders<Interview>.IndexKeys.Ascending(x => x.ProcessId).Ascending(x => x.ScheduledDate)));

        await Achievements.Indexes.CreateOneAsync(new CreateIndexModel<Achievement>(
            Builders<Achievement>.IndexKeys
                .Ascending(x => x.Kind)
                .Ascending(x => x.PeriodKey),
            new CreateIndexOptions { Unique = true }));

        // Peer mirror — index by Owner + Updated for the Peers tab's date+owner filters.
        await PeerBids.Indexes.CreateOneAsync(new CreateIndexModel<PeerBid>(
            Builders<PeerBid>.IndexKeys.Ascending(x => x.OwnerUserId).Descending(x => x.UpdatedAt)));
        await PeerInterviews.Indexes.CreateOneAsync(new CreateIndexModel<PeerInterview>(
            Builders<PeerInterview>.IndexKeys.Ascending(x => x.OwnerUserId).Ascending(x => x.ScheduledDate)));
        await PeerInterviews.Indexes.CreateOneAsync(new CreateIndexModel<PeerInterview>(
            Builders<PeerInterview>.IndexKeys.Ascending(x => x.ProcessId)));

        // The mirror holds one row per (person, profile), so a username legitimately repeats —
        // anyone running two profiles has two rows. An older unique-on-username index survives
        // in databases created before that change and rejects the second one with
        // "E11000 duplicate key ... index: username_1", which aborts sync before it can push
        // anything. Creating the new indexes does not remove it, so drop it explicitly.
        try
        {
            await PeerUsers.Indexes.DropOneAsync("username_1");
        }
        catch (MongoCommandException)
        {
            // Already gone, which is the normal case on a database created after the change.
        }

        // RemoteId is the shared database's identity for a (person, profile) pair — the real key
        // of this mirror, and what the pull deletes and rebuilds against.
        await PeerUsers.Indexes.CreateOneAsync(new CreateIndexModel<PeerUser>(
            Builders<PeerUser>.IndexKeys.Ascending(x => x.RemoteId),
            new CreateIndexOptions { Unique = true }));

        // Drives the Peers tab's person → profile cascade.
        await PeerUsers.Indexes.CreateOneAsync(new CreateIndexModel<PeerUser>(
            Builders<PeerUser>.IndexKeys.Ascending(x => x.Username).Ascending(x => x.ProfileSlug)));

        // Per-profile filtering hits these indexes for every Bid board / Interview load.
        await Links.Indexes.CreateOneAsync(new CreateIndexModel<GroupLink>(
            Builders<GroupLink>.IndexKeys.Ascending(x => x.ProfileId)));
        await Bids.Indexes.CreateOneAsync(new CreateIndexModel<UserBid>(
            Builders<UserBid>.IndexKeys.Ascending(x => x.ProfileId)));
        await Interviews.Indexes.CreateOneAsync(new CreateIndexModel<Interview>(
            Builders<Interview>.IndexKeys.Ascending(x => x.ProfileId)));

        // The Bid board and Interview list both filter by profile *and* bound by date. Mongo
        // uses one index per query stage, so the single-field indexes above can serve the
        // profile predicate or the date sort but never both — these compounds serve the whole
        // query, which is what the day/range pickers actually issue.
        await Bids.Indexes.CreateOneAsync(new CreateIndexModel<UserBid>(
            Builders<UserBid>.IndexKeys.Ascending(x => x.ProfileId).Descending(x => x.UpdatedAt)));
        await Links.Indexes.CreateOneAsync(new CreateIndexModel<GroupLink>(
            Builders<GroupLink>.IndexKeys.Ascending(x => x.ProfileId).Descending(x => x.CreatedAt)));
        await Interviews.Indexes.CreateOneAsync(new CreateIndexModel<Interview>(
            Builders<Interview>.IndexKeys.Ascending(x => x.ProfileId).Ascending(x => x.ScheduledDate)));
    }
}
