using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DevStrider.Desktop.Models;

/// <summary>
/// A peer's bid as seen in the shared Atlas cluster (and mirrored locally for offline reads).
/// Deliberately strips private fields (URL, JD, GPT content, comment) so peers can only see
/// the high-level shape of each other's bids.
///
/// <para>
/// The <see cref="Id"/> is the same ObjectId as the originator's local <see cref="UserBid.Id"/>,
/// so we can upsert by id without inventing a synthetic key.
/// </para>
/// </summary>
public class PeerBid
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    /// <summary>The originator's team-repo nickname (<see cref="UserProfile.Username"/>).</summary>
    public string OwnerUsername { get; set; } = "";
    /// <summary>FS-safe slug of the originator's profile name.</summary>
    public string OwnerProfileSlug { get; set; } = "";
    /// <summary>The originator's profile display name.</summary>
    public string OwnerProfileName { get; set; } = "";

    public string Company { get; set; } = "";
    public string Role { get; set; } = "";
    public string Status { get; set; } = "";
    public string Origin { get; set; } = "";
    public string ResumeId { get; set; } = "";
    public List<string> PrimaryStacks { get; set; } = new();

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime FirstCreatedAt { get; set; }
    public DateTime? AppliedAt { get; set; }
}
