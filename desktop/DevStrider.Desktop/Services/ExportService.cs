using System.Text.Json;
using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Driver;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Build a <see cref="SnapshotPayload"/> from local data and serialize it for upload to the
/// shared GitHub repo. JSON shape is camelCase so it stays readable and round-trippable.
/// </summary>
public class ExportService
{
    private readonly MongoContext _db;
    private readonly ProfileService _profiles;

    public ExportService(MongoContext db, ProfileService profiles)
    {
        _db = db;
        _profiles = profiles;
    }

    public async Task<(string json, SnapshotPayload payload)> BuildAsync(string ownerUsername)
    {
        var profile = await _profiles.GetAsync();
        var links = await _db.Links.Find(FilterDefinition<GroupLink>.Empty).ToListAsync();
        var bids = await _db.Bids.Find(FilterDefinition<UserBid>.Empty).ToListAsync();
        var ivs = await _db.Interviews.Find(FilterDefinition<Interview>.Empty).ToListAsync();
        var achievements = await _db.Achievements.Find(FilterDefinition<Achievement>.Empty).ToListAsync();

        var payload = new SnapshotPayload
        {
            SchemaVersion = "1",
            Owner = string.IsNullOrWhiteSpace(ownerUsername) ? "me" : ownerUsername.Trim(),
            ExportedAt = DateTime.UtcNow,
            Profile = profile,
            Links = links,
            Bids = bids,
            Interviews = ivs,
            Achievements = achievements
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        return (json, payload);
    }

    /// <summary>Path inside the shared repo: <c>{yyyy-MM-dd}/{username}.json</c>.</summary>
    public static string RepoFilePath(DateOnly dayKey, string ownerUsername) =>
        $"{dayKey:yyyy-MM-dd}/{Slugify(ownerUsername)}.json";

    private static string Slugify(string s)
    {
        var clean = new string(s.Trim().Select(c =>
            char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray());
        return string.IsNullOrEmpty(clean) ? "me" : clean.ToLowerInvariant();
    }
}
