using MongoDB.Bson;

namespace DevStrider.Desktop.Models;

/// <summary>
/// A bidding identity. One account can hold several — each represents a different real person
/// whose bids and interviews are tracked in isolation.
///
/// <para>
/// The CV lives here, not on <see cref="UserProfile"/>. A profile <i>is</i> the person being bid
/// for, so their education, certifications and experience belong to it; the account above owns
/// only what is genuinely per-person-behind-the-keyboard: the username and the goals.
/// </para>
///
/// <para>
/// <see cref="WordDocPath"/> and <see cref="MacroName"/> name a file on one Windows machine. They
/// mean nothing on another, but they travel with the profile so a reinstall restores them.
/// </para>
/// </summary>
public class Profile
{
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    /// <summary>Owning account — <c>app_user.id</c>. Stamped by the repository on write.</summary>
    public long UserId { get; set; }

    /// <summary>Real human name shown in the title-bar switcher (e.g. "Fernando Garcia").</summary>
    public string Name { get; set; } = "";

    /// <summary>Per-profile Word .docm with that profile's resume macro.</summary>
    public string WordDocPath { get; set; } = "";

    /// <summary>
    /// Name of the VBA macro inside <see cref="WordDocPath"/> to invoke via COM. Empty resolves to
    /// <c>WordMacroService.DefaultMacroName</c> at call time.
    /// </summary>
    public string MacroName { get; set; } = "";

    /// <summary>
    /// Per-profile resume-generation prompt sent to ChatGPT ahead of the job description. The
    /// prompt instructs ChatGPT to emit the resume body followed by a trailing fast-feed line:
    /// <c>UID, Company, Role, Stack1, Stack2, …</c>.
    /// </summary>
    public string ResumePrompt { get; set; } = "";

    // ── the CV this profile bids with ───────────────────────────────────────

    public string Headline { get; set; } = "";
    public string Location { get; set; } = "";
    public string Phone { get; set; } = "";

    /// <summary>The address that goes on the resume — not the login, which is on the account.</summary>
    public string PersonalEmail { get; set; } = "";

    public string LinkedinUrl { get; set; } = "";

    /// <summary>
    /// Stored one row per entry rather than as a blob, so the company portal — which shares this
    /// database — can query across them. List order is the CV's order and is persisted explicitly;
    /// rows have none of their own.
    /// </summary>
    public List<Education> Education { get; set; } = new();
    public List<Certification> Certifications { get; set; } = new();
    public List<Experience> Experiences { get; set; } = new();

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

/// <summary>
/// Years are nullable integers because that is what a CV actually states — "2019", no month —
/// and because NULL is the ongoing role or the unfinished degree.
/// </summary>
public class Education
{
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();
    public string Degree { get; set; } = "";
    public string School { get; set; } = "";
    public string Location { get; set; } = "";
    public int? StartYear { get; set; }
    public int? EndYear { get; set; }
}

public class Certification
{
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();
    public string Name { get; set; } = "";
    public string Issuer { get; set; } = "";
    public int? Year { get; set; }
}

public class Experience
{
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();
    public string Company { get; set; } = "";
    public string Location { get; set; } = "";
    public int? StartYear { get; set; }
    public int? EndYear { get; set; }
}
