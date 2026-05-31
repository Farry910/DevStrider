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

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
