using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DevStrider.Desktop.Models;

/// <summary>
/// A bidding identity on this Windows account. One end-user can host several profiles —
/// each represents a different real person whose bids/interviews are tracked in isolation.
///
/// <para>
/// NOT to be confused with <see cref="UserProfile"/> (which is a singleton holding the
/// team-repo nickname / Username). <c>Profile.Name</c> = real human name shown in the
/// title-bar switcher. The "nickname" field that prefixes daily snapshot filenames lives
/// on <see cref="AppSettings.Username"/>… err, well, on <see cref="UserProfile.Username"/>
/// and is shared across all profiles on this machine.
/// </para>
/// </summary>
public class Profile
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    /// <summary>Real human name shown in the UI (e.g. "Fernando Garcia").</summary>
    public string Name { get; set; } = "";

    /// <summary>Per-profile Word .docm with that profile's resume macro.</summary>
    public string WordDocPath { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>FS-safe slug derived from <see cref="Name"/>. Used in snapshot filenames.</summary>
    public string Slug() => Slugify(Name);

    public static string Slugify(string raw)
    {
        var trimmed = (raw ?? "").Trim();
        if (trimmed.Length == 0) return "profile";
        var cleaned = new string(trimmed.Select(c =>
            char.IsLetterOrDigit(c) ? c :
            (c == ' ' || c == '-' || c == '_' ? '-' : '-')).ToArray());
        // collapse repeated dashes
        while (cleaned.Contains("--")) cleaned = cleaned.Replace("--", "-");
        cleaned = cleaned.Trim('-');
        return cleaned.Length == 0 ? "profile" : cleaned;
    }
}
