using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DevStrider.Desktop.Models;

/// <summary>
/// A peer's bid as seen in the shared PostgreSQL database (and mirrored locally for offline
/// reads). Carries the summary fields plus the job description; the job URL, the generated
/// resume text, and the private comment stay on the originator's machine.
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
    /// <summary>
    /// Foreign key to <c>peer_users.id</c>, which is a (person, profile) pair — so this one
    /// column names both. Neither the username nor the profile name is copied here: resolve them
    /// through the mirrored <see cref="PeerUser"/>, so renaming either can't leave stale copies
    /// scattered across every row.
    /// </summary>
    public long OwnerUserId { get; set; }
    /// <summary>FS-safe slug of the originator's profile name.</summary>
    /// <summary>The originator's profile display name.</summary>

    public string Company { get; set; } = "";
    public string Role { get; set; } = "";
    public string Status { get; set; } = "";
    public string Origin { get; set; } = "";
    public string ResumeId { get; set; } = "";
    public List<string> PrimaryStacks { get; set; } = new();

    /// <summary>
    /// The job description this bid was made against. Shared so a teammate picking up the role
    /// — or preparing to interview for it — can read what was actually applied to, rather than
    /// guessing from company and title. The job URL is deliberately not shared alongside it.
    /// </summary>
    public string JobDescription { get; set; } = "";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime FirstCreatedAt { get; set; }
    public DateTime? AppliedAt { get; set; }
}
