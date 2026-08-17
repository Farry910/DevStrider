using MongoDB.Bson;

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
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    /// <summary>Owning account — <c>app_user.id</c>. Stamped by the repository on write.</summary>
    public long UserId { get; set; }

    /// <summary>Owning profile. <see cref="ObjectId.Empty"/> until a profile is stamped on.</summary>
    public ObjectId ProfileId { get; set; }

    public ObjectId BidId { get; set; }
    public ObjectId? ParentInterviewId { get; set; }

    /// <summary>
    /// Groups every round of one hiring process. The first interview of a process gets a fresh
    /// id; each next round inherits its parent's.
    ///
    /// <para>
    /// Neither existing field could do this alone: <see cref="BidId"/> is <c>Empty</c> for
    /// interviews that came from a LinkedIn chat rather than a bid, and
    /// <see cref="ParentInterviewId"/> is a linked list — answering "show me every round of this
    /// process" meant walking it. One indexed id answers it directly.
    /// </para>
    /// </summary>
    public ObjectId ProcessId { get; set; }

    public string MeetingLink { get; set; } = "";
    public string Origin { get; set; } = "";
    public string InterviewType { get; set; } = InterviewTypes.Interview;
    public string Company { get; set; } = "";
    public string Role { get; set; } = "";
    public string Recruiter { get; set; } = "";

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

    // ── Resume file in Cloudflare R2 ────────────────────────────────────────
    // AttachedResumeContent above is the resume *text* captured at apply time. These point at
    // the actual document the candidate walks in with — the .docx/.pdf the Word macro produced,
    // which until now existed only on the machine that generated it.

    /// <summary>
    /// R2 object key, or empty when nothing is attached. Shared with peers so a teammate can
    /// download the same file; see <c>peer_interviews.resume_object_key</c>.
    /// </summary>
    public string ResumeObjectKey { get; set; } = "";

    /// <summary>Original filename, kept so downloads land with a meaningful name.</summary>
    public string ResumeFileName { get; set; } = "";

    public long ResumeSizeBytes { get; set; }

    /// <summary>Null when nothing has been uploaded.</summary>
    public DateTime? ResumeUploadedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
