using DevStrider.Desktop.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;

namespace DevStrider.Desktop.Data.Import;

/// <summary>
/// Read-only access to a machine's old local MongoDB.
///
/// <para>
/// This is the only Mongo left in the app and it exists for one job: lifting an install's history
/// into the shared database, once. Nothing here is written to, and once a machine has been
/// imported the service can be stopped and uninstalled.
/// </para>
///
/// <para>
/// The documents are described by their own types rather than the live models, because the live
/// models have moved on — a bid and its link are one row now, and the CV has left
/// <c>UserProfile</c>. Mapping the old shape explicitly is what lets both be true at once.
/// </para>
/// </summary>
public sealed class LegacyStore
{
    private static int _conventionsRegistered;

    private readonly IMongoDatabase? _database;

    public LegacyStore(string connectionString, string databaseName)
    {
        RegisterConventions();
        try
        {
            // Short server-selection leash: on a machine that has already finished the import and
            // removed MongoDB, every call here should fail fast rather than stall startup.
            var settings = MongoClientSettings.FromConnectionString(connectionString);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
            settings.ConnectTimeout = TimeSpan.FromSeconds(3);
            _database = new MongoClient(settings).GetDatabase(databaseName);
        }
        catch (Exception ex)
        {
            // A malformed connection string is a settings problem, not a crash — the app runs
            // fine without any legacy database at all.
            System.Diagnostics.Debug.WriteLine($"No legacy MongoDB: {ex.Message}");
            _database = null;
        }
    }

    public bool Available => _database != null;

    private IMongoDatabase Db =>
        _database ?? throw new InvalidOperationException("No legacy MongoDB is configured.");

    public IMongoCollection<LegacyAppSettings> Settings => Db.GetCollection<LegacyAppSettings>("settings");
    public IMongoCollection<LegacyProfile> BidProfiles => Db.GetCollection<LegacyProfile>("bidProfiles");
    public IMongoCollection<LegacyUserProfile> UserProfiles => Db.GetCollection<LegacyUserProfile>("profiles");
    public IMongoCollection<LegacyLink> Links => Db.GetCollection<LegacyLink>("links");
    public IMongoCollection<LegacyBid> Bids => Db.GetCollection<LegacyBid>("bids");
    public IMongoCollection<LegacyInterview> Interviews => Db.GetCollection<LegacyInterview>("interviews");

    /// <summary>Whether there is a reachable database with anything worth importing.</summary>
    public async Task<bool> HasDataAsync(CancellationToken ct = default)
    {
        if (_database == null) return false;
        try
        {
            var links = await Links.CountDocumentsAsync(FilterDefinition<LegacyLink>.Empty, cancellationToken: ct);
            if (links > 0) return true;
            var bids = await Bids.CountDocumentsAsync(FilterDefinition<LegacyBid>.Empty, cancellationToken: ct);
            return bids > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Camel-case element names and tolerance for unknown fields — the same conventions the app
    /// wrote these documents under. Registering twice throws, hence the guard.
    /// </summary>
    private static void RegisterConventions()
    {
        if (Interlocked.Exchange(ref _conventionsRegistered, 1) != 0) return;
        ConventionRegistry.Register("DevStriderLegacy", new ConventionPack
        {
            new CamelCaseElementNameConvention(),
            new IgnoreExtraElementsConvention(true),
            new EnumRepresentationConvention(BsonType.String),
        }, _ => true);
    }
}

/// <summary>
/// The old settings document. Read once, to carry an existing install's credentials into
/// settings.json rather than making everyone retype them.
/// </summary>
public class LegacyAppSettings
{
    [BsonId] public ObjectId Id { get; set; }
    public string MongoUri { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public string SharedDbMode { get; set; } = "";
    public string SharedDbUri { get; set; } = "";
    public string SharedDbHost { get; set; } = "";
    public int SharedDbPort { get; set; }
    public string SharedDbName { get; set; } = "";
    public string SharedDbUser { get; set; } = "";
    public string SharedDbPassword { get; set; } = "";
    public bool SharedDbRequireSsl { get; set; }
    public int ListenerPort { get; set; }
    public ObjectId ActiveProfileId { get; set; }
    public string WordDocPath { get; set; } = "";
    public string WordHotkey { get; set; } = "";
    public string R2AccountId { get; set; } = "";
    public string R2Bucket { get; set; } = "";
    public string R2AccessKeyId { get; set; } = "";
    public string R2SecretAccessKey { get; set; } = "";
}

public class LegacyProfile
{
    [BsonId] public ObjectId Id { get; set; }
    public string Name { get; set; } = "";
    public string WordDocPath { get; set; } = "";
    public string ResumePrompt { get; set; } = "";
    public string MacroName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// The old singleton profile row. Only the fields DevStrider still has somewhere to put are
/// mapped: the CV and the goal targets it also carried have no home any more.
/// </summary>
public class LegacyUserProfile
{
    [BsonId] public ObjectId Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Headline { get; set; } = "";
    public string Location { get; set; } = "";
    public string Phone { get; set; } = "";
    public string PersonalEmail { get; set; } = "";
    public string LinkedinUrl { get; set; } = "";
}

/// <summary>The link half of what is now one <see cref="UserBid"/> row.</summary>
public class LegacyLink
{
    [BsonId] public ObjectId Id { get; set; }
    public ObjectId ProfileId { get; set; }
    public string Url { get; set; } = "";
    public string UrlNorm { get; set; } = "";
    public string SharedJobDescription { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? MarkedUselessAt { get; set; }
}

/// <summary>The bid half. <see cref="GroupLinkId"/> is what joins it to its link.</summary>
public class LegacyBid
{
    [BsonId] public ObjectId Id { get; set; }
    public ObjectId ProfileId { get; set; }
    public ObjectId GroupLinkId { get; set; }
    public string ResumeId { get; set; } = "";
    public string Company { get; set; } = "";
    public string Role { get; set; } = "";
    public List<string> PrimaryStacks { get; set; } = new();
    public string Status { get; set; } = "";
    public string Origin { get; set; } = "";
    public string JobDescription { get; set; } = "";
    public string GptResumeContent { get; set; } = "";
    public string Comment { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime FirstCreatedAt { get; set; }
    public DateTime? AppliedAt { get; set; }
}

public class LegacyInterview
{
    [BsonId] public ObjectId Id { get; set; }
    public ObjectId ProfileId { get; set; }
    public ObjectId BidId { get; set; }
    public ObjectId? ParentInterviewId { get; set; }
    public ObjectId ProcessId { get; set; }
    public string MeetingLink { get; set; } = "";
    public string Origin { get; set; } = "";
    public string InterviewType { get; set; } = "";
    public string Company { get; set; } = "";
    public string Role { get; set; } = "";
    public string Recruiter { get; set; } = "";

    /// <summary>
    /// Some documents persisted this as a single comma-separated string instead of an array.
    /// <see cref="FlexibleStringListSerializer"/> accepts either form.
    /// </summary>
    [BsonSerializer(typeof(FlexibleStringListSerializer))]
    public List<string> AdditionalAttendees { get; set; } = new();

    public string ResumeId { get; set; } = "";
    public DateTime? ScheduledDate { get; set; }
    public string ScheduledTime { get; set; } = "";
    public int? DurationMinutes { get; set; }
    public string Status { get; set; } = "";
    public string UserComment { get; set; } = "";
    public string AttachedJobDescription { get; set; } = "";
    public string AttachedResumeContent { get; set; } = "";
    public string ResumeObjectKey { get; set; } = "";
    public string ResumeFileName { get; set; } = "";
    public long ResumeSizeBytes { get; set; }
    public DateTime? ResumeUploadedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
