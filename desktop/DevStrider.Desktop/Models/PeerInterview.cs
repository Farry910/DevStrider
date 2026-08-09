using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DevStrider.Desktop.Models;

/// <summary>
/// A peer's interview as seen in the shared Atlas cluster (and mirrored locally).
/// Carries the pipeline shape plus the JD snapshot. The meeting link, the attached resume
/// text, and private comments stay on the originator's machine.
/// </summary>
public class PeerInterview
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    /// <summary>
    /// Foreign key to <c>peer_users.id</c>, which is a (person, profile) pair — so this one
    /// column names both. Neither the username nor the profile name is copied here: resolve them
    /// through the mirrored <see cref="PeerUser"/>, so renaming either can't leave stale copies
    /// scattered across every row.
    /// </summary>
    public long OwnerUserId { get; set; }

    /// <summary>
    /// The bid this interview came from, or <see cref="ObjectId.Empty"/> when it didn't come
    /// from one at all (a LinkedIn-chat interview has no bid behind it). Maps to a nullable
    /// <c>bid_id</c> in the shared database, so "no bid" and "bid unknown" stay distinguishable.
    /// </summary>
    public ObjectId BidId { get; set; }

    /// <summary>
    /// Groups every round of one hiring process — HR, Tech 1, Tech 2, Offer — under a single id
    /// so a pipeline reads as one thing. Shared verbatim from the owner's local interview.
    /// </summary>
    public string ProcessId { get; set; } = "";

    public string Company { get; set; } = "";
    public string Role { get; set; } = "";
    public string InterviewType { get; set; } = "";
    public string Status { get; set; } = "";
    public string Recruiter { get; set; } = "";
    public string ResumeId { get; set; } = "";

    /// <summary>
    /// JD snapshot taken when the interview was scheduled (the local
    /// <see cref="Interview.AttachedJobDescription"/>). Shared for the same reason as on a bid:
    /// it's what someone else needs to prepare. The meeting link is not shared.
    /// </summary>
    public string JobDescription { get; set; } = "";

    public DateTime? ScheduledDate { get; set; }
    public string ScheduledTime { get; set; } = "";
    public int? DurationMinutes { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
