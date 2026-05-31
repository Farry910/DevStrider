using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DevStrider.Desktop.Models;

public static class InterviewStatuses
{
    public const string Scheduled = "scheduled";
    public const string Completed = "completed";
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public static class InterviewTypes
{
    public const string PhoneScreening = "phone_screening";
    public const string Interview = "interview";
    public const string Assessment = "assessment";
    public const string Offer = "offer";
}

public class Interview
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    public ObjectId BidId { get; set; }
    public ObjectId? ParentInterviewId { get; set; }

    public string MeetingLink { get; set; } = "";
    public string Origin { get; set; } = "";
    public string InterviewType { get; set; } = InterviewTypes.Interview;
    public string Company { get; set; } = "";
    public string Role { get; set; } = "";
    public string Recruiter { get; set; } = "";
    public List<string> AdditionalAttendees { get; set; } = new();

    public DateTime? ScheduledDate { get; set; }
    public string ScheduledTime { get; set; } = "";
    public int? DurationMinutes { get; set; }

    public string Status { get; set; } = InterviewStatuses.Scheduled;
    public string UserComment { get; set; } = "";

    /// <summary>JD snapshot at apply time — for the JD viewer on the interview row.</summary>
    public string AttachedJobDescription { get; set; } = "";
    /// <summary>Resume snapshot at apply time.</summary>
    public string AttachedResumeContent { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
