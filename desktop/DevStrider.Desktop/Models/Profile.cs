
namespace DevStrider.Desktop.Models;

/// <summary>
/// A bidding identity. One account can hold several — each represents a different real person
/// whose bids and interviews are tracked in isolation.
///
/// <para>
/// The CV is not here, and not anywhere in this app. Education, certifications and work history
/// used to hang off this object as three lists backed by three tables; they are gone. That
/// material lives in <see cref="WordDocPath"/>, which is where it was being written and maintained
/// anyway — a second copy in the database only ever meant two versions of one CV, and the
/// database's was the one nobody updated. DevStrider does not read a CV and does not render one.
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

    /// <summary>Per-profile Word .docm with that profile's resume macro — and its CV.</summary>
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

    // ── who this identity is, in the few fields the app actually uses ───────

    public string Headline { get; set; } = "";
    public string Location { get; set; } = "";
    public string Phone { get; set; } = "";

    /// <summary>The address that goes on the resume — not the login, which is on the account.</summary>
    public string PersonalEmail { get; set; } = "";

    public string LinkedinUrl { get; set; } = "";

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
