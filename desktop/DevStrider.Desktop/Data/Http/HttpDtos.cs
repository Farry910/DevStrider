using MongoDB.Bson;

namespace DevStrider.Desktop.Data.Http;

// Wire shapes for hr-system's /api/devstrider/* responses (see hr-system/repo.js, the ds* family
// of functions). Field names are PascalCase here and camelCase on the wire; HrApiClient's JSON
// options are case-insensitive, so no per-property [JsonPropertyName] is needed.

internal sealed class AccountDto
{
    public long UserId { get; set; }
    public string Username { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

internal sealed class UsernameTakenDto
{
    public bool Taken { get; set; }
}

internal sealed class ProfileDto
{
    public ObjectId Id { get; set; }
    public long UserId { get; set; }
    public string Name { get; set; } = "";
    public string WordDocPath { get; set; } = "";
    public string MacroName { get; set; } = "";
    public string ResumePrompt { get; set; } = "";
    public string Headline { get; set; } = "";
    public string Location { get; set; } = "";
    public string Phone { get; set; } = "";
    public string PersonalEmail { get; set; } = "";
    public string LinkedinUrl { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

internal sealed class BidDto
{
    public ObjectId Id { get; set; }
    public long UserId { get; set; }
    public ObjectId ProfileId { get; set; }
    public string Url { get; set; } = "";
    public string UrlNorm { get; set; } = "";
    public DateTime? MarkedUselessAt { get; set; }
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
    public DateTime? AppliedAt { get; set; }
}

internal sealed class BidCountDto
{
    public long Count { get; set; }
}

internal sealed class InterviewDto
{
    public ObjectId Id { get; set; }
    public long UserId { get; set; }
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

internal sealed class InterviewCountDto
{
    public long Count { get; set; }
}

internal sealed class ChangedCountDto
{
    public long Changed { get; set; }
}

internal sealed class DeletedCountDto
{
    public long Deleted { get; set; }
}

internal sealed class PeerIdentityDto
{
    public long UserId { get; set; }
    public string Username { get; set; } = "";
    public ObjectId ProfileId { get; set; }
    public string ProfileName { get; set; } = "";
    public string ProfileSlug { get; set; } = "";
    public string Email { get; set; } = "";
}
