using MongoDB.Bson;

namespace DevStrider.Desktop.Models;

/// <summary>
/// Machine-level settings. Persisted as JSON on this machine by
/// <see cref="Services.SettingsStore"/>. Unknown fields are tolerated so removed fields from
/// older installs deserialize quietly.
///
/// <para>
/// DevStrider holds no database credential any more — every account and every ds_* row is reached
/// through hr-system's HTTP API (<see cref="Services.HrApi.HrApiClient"/>), and the sign-in flow is
/// hr-system's, not this app's. What is stored is what is true of this machine: which hr-system
/// server to talk to, the week-long bearer token that login hands out (so the app does not ask for
/// a password every launch — see <see cref="Services.HrApi.HrApiClient"/>), which port the local
/// listener binds, which profile was last open.
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

    // ── hr-system — the account, the ds_* data, and the JWT session ─────────
    // DevStrider used to hold the shared Postgres credential and query ds_* directly. That is
    // gone: hr-system's /api/devstrider/* routes are the only way in now, and this app carries
    // nothing more sensitive than the bearer token they hand out.

    /// <summary>
    /// Base URL of the hr-system deployment, no trailing slash — e.g.
    /// <c>https://triospace.org/hr</c>. <c>/api/devstrider/...</c> is appended to it.
    /// </summary>
    public string HrApiBaseUrl { get; set; } = "https://triospace.org/hr";

    /// <summary>
    /// The week-long bearer token <c>/api/devstrider/auth/login</c> hands out. Cleartext, like
    /// every other credential this file has ever held — holding it is what lets the app skip the
    /// login window on every launch. Cleared by signing in again with different credentials, and
    /// meaningless once <see cref="HrTokenExpiresAt"/> has passed.
    /// </summary>
    public string HrToken { get; set; } = "";

    /// <summary>UTC. The app refreshes the token once this is inside its last day.</summary>
    public DateTime? HrTokenExpiresAt { get; set; }

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
    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
