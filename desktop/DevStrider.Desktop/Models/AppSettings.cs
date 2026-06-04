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

    /// <summary>
    /// Atlas / shared-cluster connection used for peer sync. Empty disables sync.
    /// Format: <c>mongodb+srv://user:pass@cluster.mongodb.net/?retryWrites=true&amp;w=majority</c>.
    /// </summary>
    public string SharedMongoUri { get; set; } = "";

    /// <summary>Database inside the shared cluster that holds <c>peerBids</c> + <c>peerInterviews</c>.</summary>
    public string SharedDatabaseName { get; set; } = "devstrider-shared";

    /// <summary>UTC timestamp of the last successful peer sync. Drives delta queries.</summary>
    public DateTime LastSyncAt { get; set; } = DateTime.MinValue;

    /// <summary>UTC timestamp of the last successful legacy-database import (web-app schema → local).</summary>
    public DateTime LegacyMigratedAt { get; set; } = DateTime.MinValue;

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

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
