using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DevStrider.Desktop.Models;

/// <summary>
/// A peer's exported data, pulled from the shared GitHub repo and stored locally read-only.
/// Importing multiple peers' files = stats/overview gain one extra series/column each.
/// </summary>
public class ImportedSnapshot
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    /// <summary>Local profile this peer file was imported into. Empty on legacy rows.</summary>
    public ObjectId ProfileId { get; set; }

    /// <summary>The peer's username, taken from the GitHub file prefix.</summary>
    public string Owner { get; set; } = "";
    /// <summary>UTC date the snapshot was exported by the peer.</summary>
    public string DayKey { get; set; } = "";
    public DateTime ExportedAt { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Whole serialized payload (links + bids + interviews + profile) as JSON.</summary>
    public string PayloadJson { get; set; } = "";

    /// <summary>SHA of the file commit on GitHub (so we don't reimport identical contents).</summary>
    public string SourceSha { get; set; } = "";
}

/// <summary>Wire shape inside <see cref="ImportedSnapshot.PayloadJson"/>.</summary>
public class SnapshotPayload
{
    public string SchemaVersion { get; set; } = "1";
    public string Owner { get; set; } = "";
    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;

    public UserProfile? Profile { get; set; }
    public List<GroupLink> Links { get; set; } = new();
    public List<UserBid> Bids { get; set; } = new();
    public List<Interview> Interviews { get; set; } = new();
    public List<Achievement> Achievements { get; set; } = new();
}
