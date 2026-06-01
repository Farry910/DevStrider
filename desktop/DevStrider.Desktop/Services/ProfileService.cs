using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Driver;

namespace DevStrider.Desktop.Services;

public class ProfileService
{
    private readonly MongoContext _db;
    public ProfileService(MongoContext db) => _db = db;

    /// <summary>
    /// The local install is single-user; first read seeds a fresh profile using the current
    /// Windows account name (lower-cased, spaces stripped). Sensible default for "your
    /// filename in the team repo" without forcing the user into Settings on day one.
    /// </summary>
    public async Task<UserProfile> GetAsync()
    {
        var doc = await _db.Profiles.Find(FilterDefinition<UserProfile>.Empty).FirstOrDefaultAsync();
        if (doc != null) return doc;
        var seed = new UserProfile { Username = DefaultUsername() };
        await _db.Profiles.InsertOneAsync(seed);
        return seed;
    }

    /// <summary>Lower-cased Windows account name with spaces removed; falls back to "me".</summary>
    public static string DefaultUsername()
    {
        var raw = Environment.UserName ?? "";
        var cleaned = new string(raw.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
        return string.IsNullOrEmpty(cleaned) ? "me" : cleaned;
    }

    public async Task SaveAsync(UserProfile profile)
    {
        profile.UpdatedAt = DateTime.UtcNow;
        await _db.Profiles.ReplaceOneAsync(
            Builders<UserProfile>.Filter.Eq(x => x.Id, profile.Id),
            profile,
            new ReplaceOptions { IsUpsert = true });
    }
}
