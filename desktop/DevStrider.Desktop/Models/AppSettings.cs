using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DevStrider.Desktop.Models;

/// <summary>
/// One-row singleton holding install-level settings. <see cref="MongoContext"/> reads it on
/// startup; the Settings UI is the editor. Unknown fields are tolerated (BSON convention)
/// so removed fields from older installs deserialize quietly.
/// </summary>
public class AppSettings
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    /// <summary>Local Mongo connection — defaults to the standard local install.</summary>
    public string MongoUri { get; set; } = "mongodb://127.0.0.1:27017";
    public string DatabaseName { get; set; } = "devstrider";

    // ── Shared PostgreSQL (peer database) ───────────────────────────────────
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

    /// <summary>UTC timestamp of the last successful peer sync. Drives delta queries.</summary>
    public DateTime LastSyncAt { get; set; } = DateTime.MinValue;

    /// <summary>
    /// How often the background scheduler pushes/pulls peer data, in minutes. 0 or less disables
    /// automatic sync and leaves only the manual <b>Sync now</b> button. Clamped to a 5-minute
    /// floor at read time — hosted Postgres tiers have connection budgets worth respecting.
    /// </summary>
    public int SyncIntervalMinutes { get; set; } = 60;

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
    /// Folder the Word macro saves generated resume files into. **No longer used** for
    /// auto-ingest — resumes aren't stored or shared. Kept on the schema only so old docs
    /// deserialize cleanly.
    /// </summary>
    public string ResumeOutputFolder { get; set; } = "";

    // ── Cloudflare R2 (resume file storage) ─────────────────────────────────
    // Same rule as the shared-cluster credential above: stored here in the local database,
    // loaded once at startup by SettingsService, never read from Mongo per-use.

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
    [BsonIgnore]
    public string R2Endpoint =>
        string.IsNullOrWhiteSpace(R2AccountId) ? "" : $"https://{R2AccountId.Trim()}.r2.cloudflarestorage.com";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Shallow copy for editing. <see cref="SettingsService"/> hands the cached instance to
    /// read-only consumers, so the Settings form must work on a copy — otherwise every keystroke
    /// in the form would be visible to the listener and sync services before Save.
    /// Every property here is a value type or string, so a member-wise copy is a full copy.
    /// </summary>
    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
