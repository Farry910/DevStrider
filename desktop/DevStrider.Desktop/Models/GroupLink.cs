using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DevStrider.Desktop.Models;

/// <summary>
/// A job-posting URL. Local-mode keeps the legacy "GroupLink" name + indexes so the web app's
/// existing data can be imported wholesale.
/// </summary>
public class GroupLink
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    public string Url { get; set; } = "";
    /// <summary>Canonical form for dedup: lowercased href with trailing slash trimmed; query + hash preserved.</summary>
    public string UrlNorm { get; set; } = "";
    public string SharedJobDescription { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Filled when the creator marks this posting as useless (creator can purge if no peer bid).</summary>
    public DateTime? MarkedUselessAt { get; set; }

    public DateTime? AppliedAt { get; set; }
    public string AppliedCompany { get; set; } = "";
    public string AppliedRole { get; set; } = "";
    public List<string> AppliedStacks { get; set; } = new();
}
