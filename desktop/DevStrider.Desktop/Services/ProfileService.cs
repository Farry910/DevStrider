using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Driver;

namespace DevStrider.Desktop.Services;

public class ProfileService
{
    private readonly MongoContext _db;
    public ProfileService(MongoContext db) => _db = db;

    /// <summary>The local install is single-user; first read seeds an empty profile.</summary>
    public async Task<UserProfile> GetAsync()
    {
        var doc = await _db.Profiles.Find(FilterDefinition<UserProfile>.Empty).FirstOrDefaultAsync();
        if (doc != null) return doc;
        var seed = new UserProfile();
        await _db.Profiles.InsertOneAsync(seed);
        return seed;
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
