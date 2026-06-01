using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DevStrider.Desktop.Models;

/// <summary>
/// One row per GitHub push the user has made (success or failure). Drives the Sharing tab's
/// upload tracker so the user can confirm at a glance which days they've shared with the
/// group and which day's push failed.
/// </summary>
public class UploadLog
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    /// <summary>Local calendar day this push covers, e.g. "2026-06-01".</summary>
    public string DayKey { get; set; } = "";
    public DateTime PushedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Repo-relative path that was written, e.g. "2026-06-01/joshua.json".</summary>
    public string RepoPath { get; set; } = "";
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    /// <summary>True when the push went through AES-GCM (sharing key was set).</summary>
    public bool Encrypted { get; set; }
}
