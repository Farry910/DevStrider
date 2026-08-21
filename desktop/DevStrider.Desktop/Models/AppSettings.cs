using MongoDB.Bson;

namespace DevStrider.Desktop.Models;

/// <summary>
/// Machine-level settings. Persisted as JSON on this machine by
/// <see cref="Services.SettingsStore"/> — <b>not</b> in the database, because it carries the
/// credentials needed to reach the database and so has to be readable before any connection
/// exists. Unknown fields are tolerated so removed fields from older installs deserialize quietly.
///
/// <para>
/// Nothing here identifies the logged-in user: there is no persisted session, and the password is
/// asked for on every start of the app. What is stored is only what is true of this machine — how
/// to reach the database, which port the listener binds, which profile was last open.
/// </para>
/// </summary>
public class AppSettings
{
    /// <summary>
    /// Connection to a legacy local MongoDB, read only by the one-time import that lifts a
    /// machine's old data into the shared database. Nothing else touches Mongo.
    /// </summary>
    public string MongoUri { get; set; } = "mongodb://127.0.0.1:27017";
    public string DatabaseName { get; set; } = "devstrider";

    // ── Shared PostgreSQL — the store ───────────────────────────────────────
    // Two ways to say the same thing. Providers hand out a service URI; a self-hosted box is
    // easier to describe in parts. SharedDbMode decides which set is authoritative — the other
    // is kept, not cleared, so switching back and forth doesn't lose what you typed.

    /// <summary><c>uri</c> or <c>parts</c>. See <see cref="Services.SharedDbCredentials"/>.</summary>
    public string SharedDbMode { get; set; } = Services.SharedDbCredentials.ModeUri;

    /// <summary>Service URI, e.g. <c>postgresql://user:pass@host:5432/devstrider?sslmode=require</c>.</summary>
    public string SharedDbUri { get; set; } = "";

    public string SharedDbHost { get; set; } = "";
    public int SharedDbPort { get; set; } = 5432;
    public string SharedDbName { get; set; } = "devstrider";
    public string SharedDbUser { get; set; } = "";

    /// <summary>Cleartext, like every other credential here — the shared cluster is one login the whole team shares.</summary>
    public string SharedDbPassword { get; set; } = "";

    /// <summary>
    /// Require TLS. On for hosted Postgres (Supabase, Neon, Railway, Aiven all mandate it); turn
    /// off only for a local box that isn't listening on TLS at all.
    /// </summary>
    public bool SharedDbRequireSsl { get; set; } = true;

    /// <summary>
    /// Port the local Bid-Assistant listener binds to (loopback only). Default 8765 — keep in
    /// sync with the Chrome extension's configured base URL. Localhost binding means no
    /// authentication is required.
    /// </summary>
    public int ListenerPort { get; set; } = 8765;

    /// <summary>
    /// Active <see cref="Profile"/> id. Set by the title-bar switcher; persisted so the next
    /// launch opens the same identity. <see cref="ObjectId.Empty"/> on a fresh install — the
    /// migration creates a "Default" profile and stamps it here.
    /// </summary>
    public ObjectId ActiveProfileId { get; set; }

    /// <summary>
    /// Legacy single-profile Word path. Kept so the first-launch migration can copy it into
    /// the seed <see cref="Profile.WordDocPath"/>; no longer read at runtime once a profile
    /// exists. Safe to remove a release or two after every install has run the migration.
    /// </summary>
    public string WordDocPath { get; set; } = "";

    /// <summary>Hotkey assigned to the Word macro. Default F9 triggers field updates.</summary>
    public string WordHotkey { get; set; } = "F9";

    /// <summary>
    /// Per-profile preferences for the user-driven ChatGPT Resume Studio. They contain no
    /// credentials and are machine preferences, so they belong beside the active-profile choice.
    /// </summary>
    public Dictionary<string, ChatGptResumeSessionSettings> ChatGptResumeSessions { get; set; } = new();

    /// <summary>Per-profile reusable job-form answers. These are user-provided answers, never secrets.</summary>
    public Dictionary<string, Dictionary<string, string>> JobFormAnswers { get; set; } = new();

    // ── Cloudflare R2 (resume file storage) ─────────────────────────────────
    // Same rule as the shared-database credential above: stored in this file and loaded once at
    // startup by SettingsService, never re-read per use.

    /// <summary>
    /// R2 account id — the hex prefix of the S3 endpoint
    /// (<c>https://&lt;account&gt;.r2.cloudflarestorage.com</c>). Not a secret.
    /// </summary>
    public string R2AccountId { get; set; } = "";

    /// <summary>Bucket holding uploaded resume files. Not a secret.</summary>
    public string R2Bucket { get; set; } = "";

    /// <summary>R2 API token access key id. Half of the credential pair.</summary>
    public string R2AccessKeyId { get; set; } = "";

    /// <summary>
    /// R2 API token secret. Stored in cleartext like every other credential here — and note that
    /// a token with object-write permission can also *delete* objects, so every install holding
    /// this can wipe the bucket.
    /// </summary>
    public string R2SecretAccessKey { get; set; } = "";

    /// <summary>S3-compatible endpoint derived from <see cref="R2AccountId"/>. Empty until set.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string R2Endpoint =>
        string.IsNullOrWhiteSpace(R2AccountId) ? "" : $"https://{R2AccountId.Trim()}.r2.cloudflarestorage.com";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Shallow copy for editing. <see cref="SettingsService"/> hands the cached instance to
    /// read-only consumers, so the Settings form must work on a copy — otherwise every keystroke
    /// in the form would be visible to the listener and every other service before Save.
    /// Every property here is a value type or string, so a member-wise copy is a full copy.
    /// </summary>
    public AppSettings Clone()
    {
        var clone = (AppSettings)MemberwiseClone();
        clone.ChatGptResumeSessions = (ChatGptResumeSessions ?? new()).ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone());
        clone.JobFormAnswers = (JobFormAnswers ?? new()).ToDictionary(
            pair => pair.Key,
            pair => new Dictionary<string, string>(pair.Value, StringComparer.OrdinalIgnoreCase));
        return clone;
    }
}

/// <summary>Non-secret local preferences for one profile's ChatGPT UI generation session.</summary>
public sealed class ChatGptResumeSessionSettings
{
    public int GenerationLimit { get; set; } = 5;
    public bool AutomaticallyRunWordMacro { get; set; }

    public ChatGptResumeSessionSettings Clone() => new()
    {
        GenerationLimit = GenerationLimit,
        AutomaticallyRunWordMacro = AutomaticallyRunWordMacro,
    };
}
