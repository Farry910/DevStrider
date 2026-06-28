using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DevStrider.Desktop.Models;

/// <summary>Lifecycle stages of a resume-generation job. Stored verbatim as the value.</summary>
public static class ResumeJobStatuses
{
    public const string Queued         = "Queued";          // waiting in the batch
    public const string Fetching       = "Fetching";        // extension scraping the JD in a background tab
    public const string Generating     = "Generating";      // injected into ChatGPT, awaiting response
    public const string ResumeReceived = "Resume Received"; // ChatGPT response harvested, macro pending
    public const string Done           = "Done";            // macro produced the file + bid recorded
    public const string Failed         = "Failed";          // any stage failed; eligible for a retry round

    public static readonly string[] All =
    {
        Queued, Fetching, Generating, ResumeReceived, Done, Failed
    };
}

/// <summary>
/// One URL in the resume-auto-generation queue. Mirrors ResumeAuto's SQLite <c>jobs</c> row
/// but profile-scoped by <see cref="ObjectId"/> (not name) and stored in Mongo so it lives
/// alongside the rest of DevStrider's data.
///
/// <para>
/// The unique key (ProfileId, JobDate, UrlNorm) reproduces ResumeAuto's
/// <c>UNIQUE(profile_name, date, url)</c> constraint so the same URL can't be queued twice
/// for one profile on one day.
/// </para>
/// </summary>
public class ResumeJob
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    /// <summary>Owning profile (drives which prompt / .docm / macro is used).</summary>
    public ObjectId ProfileId { get; set; }

    public string Url { get; set; } = "";
    /// <summary>Normalized URL for the dedup key. Filled via <see cref="DevStrider.Desktop.Services.UrlNorm"/>.</summary>
    public string UrlNorm { get; set; } = "";

    /// <summary>Local calendar day the job was queued, "yyyy-MM-dd" — part of the dedup key.</summary>
    public string JobDate { get; set; } = "";

    public string Status { get; set; } = ResumeJobStatuses.Queued;

    /// <summary>Output filenames produced by the Word macro (UID-derived). filename2 optional.</summary>
    public string Filename1 { get; set; } = "";
    public string Filename2 { get; set; } = "";

    /// <summary>Scraped job description (filled at the Fetching stage).</summary>
    public string JobDescription { get; set; } = "";

    /// <summary>Raw resume body harvested from ChatGPT (sans the trailing fast-feed line).</summary>
    public string GptResumeContent { get; set; } = "";

    /// <summary>The trailing "UID, Company, Role, Stack1, …" line ChatGPT emitted; drives the auto-bid.</summary>
    public string FastFeedLine { get; set; } = "";

    /// <summary>Last error message when <see cref="Status"/> is Failed (shown in the Resume tab).</summary>
    public string Error { get; set; } = "";

    /// <summary>How many retry rounds this job has been through (caps retries).</summary>
    public int RetryCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
