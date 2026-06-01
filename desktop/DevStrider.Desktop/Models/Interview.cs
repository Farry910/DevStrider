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

/// <summary>
/// Interview-funnel stages, listed in the rough order they happen. Stored verbatim — these
/// strings ARE the persisted value. Legacy values (<c>phone_screening</c>, <c>interview</c>,
/// lower-case <c>assessment</c>/<c>offer</c>) are preserved as constants so old records
/// keep rendering, but new scheduling uses the friendly-cased forms below.
/// </summary>
public static class InterviewTypes
{
    public const string HR              = "HR";
    public const string Assessment      = "Assessment";
    public const string PhoneCall       = "Phone Call";
    public const string Tech1           = "Tech 1";
    public const string Tech2           = "Tech 2";
    public const string Tech3           = "Tech 3";
    public const string ClientInterview = "Client Interview";
    public const string FinalInterview  = "Final Interview";
    public const string Offer           = "Offer";

    // Legacy values still present in older docs — keep so they don't render blank.
    public const string PhoneScreening  = "phone_screening";
    public const string Interview       = "interview";

    /// <summary>Order shown in dropdowns. Legacy values intentionally omitted from the UI.</summary>
    public static readonly string[] All =
    {
        HR, Assessment, PhoneCall, Tech1, Tech2, Tech3, ClientInterview, FinalInterview, Offer
    };
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

    /// <summary>
    /// Some legacy docs persisted this field as a single comma-separated string instead of
    /// a BSON array. <see cref="FlexibleStringListSerializer"/> accepts either form on read
    /// and always writes back the array form.
    /// </summary>
    [BsonSerializer(typeof(FlexibleStringListSerializer))]
    public List<string> AdditionalAttendees { get; set; } = new();

    /// <summary>
    /// Resume UID captured from the source bid at scheduling time (e.g. "7mK92"). Lets the
    /// interview row label which resume was submitted without re-traversing the bid.
    /// </summary>
    public string ResumeId { get; set; } = "";

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
