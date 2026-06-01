using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DevStrider.Desktop.Models;

/// <summary>
/// One-row singleton holding install-level settings (mongo URI, GitHub PAT, team repo URL).
/// PAT is stored DPAPI-protected (see <see cref="DevStrider.Desktop.Services.SecretStore"/>).
/// </summary>
public class AppSettings
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    /// <summary>Defaults to the local docker / msi install.</summary>
    public string MongoUri { get; set; } = "mongodb://127.0.0.1:27017";
    public string DatabaseName { get; set; } = "devstrider";

    /// <summary>e.g. "https://github.com/your-team/devstrider-sync" (https form preferred).</summary>
    public string GitHubRepoUrl { get; set; } = "";
    /// <summary>Branch to push/pull from. Defaults to "main".</summary>
    public string GitHubBranch { get; set; } = "main";
    /// <summary>Encrypted PAT bytes (DPAPI, current-user scope). Base64 of the ciphertext.</summary>
    public string GitHubTokenProtected { get; set; } = "";

    /// <summary>
    /// Port the local Bid-Assistant listener binds to (loopback only). Default 8765 — keep in
    /// sync with the Chrome extension's configured base URL. Localhost binding means no
    /// authentication is required.
    /// </summary>
    public int ListenerPort { get; set; } = 8765;
    public bool ListenerEnabled { get; set; } = true;

    /// <summary>
    /// Path to the user's Word document containing the macro that generates resumes. Triggered
    /// by the extension's "Update Word" purple button → POST /refresh-word.
    /// </summary>
    public string WordDocPath { get; set; } = "";

    /// <summary>Hotkey assigned to the Word macro. Default F9 triggers field updates.</summary>
    public string WordHotkey { get; set; } = "F9";

    /// <summary>
    /// Folder the Word macro saves generated resume files into (filenames follow
    /// "UID, Company, Role, Stacks…"). A FileSystemWatcher on this folder auto-imports new
    /// files into the local Resumes collection.
    /// </summary>
    public string ResumeOutputFolder { get; set; } = "";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
