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

    public IMongoCollection<Resume> Resumes =>
        Database.GetCollection<Resume>("resumes");

    /// <summary>Local mirror of peer bids pulled from the shared Atlas cluster.</summary>
    public IMongoCollection<PeerBid> PeerBids =>
        Database.GetCollection<PeerBid>("peerBids");

    /// <summary>Local mirror of peer interviews pulled from the shared Atlas cluster.</summary>
    public IMongoCollection<PeerInterview> PeerInterviews =>
        Database.GetCollection<PeerInterview>("peerInterviews");

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

        await Achievements.Indexes.CreateOneAsync(new CreateIndexModel<Achievement>(
            Builders<Achievement>.IndexKeys
                .Ascending(x => x.Kind)
                .Ascending(x => x.PeriodKey),
            new CreateIndexOptions { Unique = true }));

        await Resumes.Indexes.CreateOneAsync(new CreateIndexModel<Resume>(
            Builders<Resume>.IndexKeys.Ascending(x => x.Uid)));
        await Resumes.Indexes.CreateOneAsync(new CreateIndexModel<Resume>(
            Builders<Resume>.IndexKeys.Descending(x => x.UploadedAt)));

        // Peer mirror — index by Owner + Updated for the Peers tab's date+owner filters.
        await PeerBids.Indexes.CreateOneAsync(new CreateIndexModel<PeerBid>(
            Builders<PeerBid>.IndexKeys.Ascending(x => x.OwnerUsername).Descending(x => x.UpdatedAt)));
        await PeerInterviews.Indexes.CreateOneAsync(new CreateIndexModel<PeerInterview>(
            Builders<PeerInterview>.IndexKeys.Ascending(x => x.OwnerUsername).Ascending(x => x.ScheduledDate)));

        // Per-profile filtering hits these indexes for every Bid board / Interview load.
        await Links.Indexes.CreateOneAsync(new CreateIndexModel<GroupLink>(
            Builders<GroupLink>.IndexKeys.Ascending(x => x.ProfileId)));
        await Bids.Indexes.CreateOneAsync(new CreateIndexModel<UserBid>(
            Builders<UserBid>.IndexKeys.Ascending(x => x.ProfileId)));
        await Interviews.Indexes.CreateOneAsync(new CreateIndexModel<Interview>(
            Builders<Interview>.IndexKeys.Ascending(x => x.ProfileId)));
    }
}
