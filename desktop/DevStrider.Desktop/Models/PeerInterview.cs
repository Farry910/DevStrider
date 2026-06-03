using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DevStrider.Desktop.Models;

/// <summary>
/// A peer's interview as seen in the shared Atlas cluster (and mirrored locally).
/// Strips private fields (meeting link, attached JD/resume, comments) so peers only see
/// the high-level shape of each other's interview pipeline.
/// </summary>
public class PeerInterview
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    public string OwnerUsername { get; set; } = "";
    public string OwnerProfileSlug { get; set; } = "";
    public string OwnerProfileName { get; set; } = "";

    public string Company { get; set; } = "";
    public string Role { get; set; } = "";
    public string InterviewType { get; set; } = "";
    public string Status { get; set; } = "";
    public string Recruiter { get; set; } = "";
    public string ResumeId { get; set; } = "";

    public DateTime? ScheduledDate { get; set; }
    public string ScheduledTime { get; set; } = "";
    public int? DurationMinutes { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
