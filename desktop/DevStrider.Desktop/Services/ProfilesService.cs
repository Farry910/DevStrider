using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace DevStrider.Desktop.Services;

/// <summary>
/// CRUD over the <see cref="Profile"/> collection. Different from the older
/// <see cref="ProfileService"/> (singular) which manages the single <c>UserProfile</c> row
/// holding the team-repo nickname.
/// </summary>
public class ProfilesService
{
    private readonly MongoContext _db;
    public ProfilesService(MongoContext db) => _db = db;

    public Task<List<Profile>> ListAsync() =>
        _db.BidProfiles
           .Find(FilterDefinition<Profile>.Empty)
           .SortBy(p => p.CreatedAt)
           .ToListAsync();

    public Task<Profile?> GetAsync(ObjectId id) =>
        _db.BidProfiles.Find(p => p.Id == id).FirstOrDefaultAsync()!;

    /// <summary>
    /// The only place a <see cref="Profile"/> is constructed — the Profiles tab and the
    /// single-profile migration both come through here.
    /// </summary>
    public async Task<Profile> CreateAsync(string name, string wordDocPath = "")
    {
        var p = new Profile
        {
            Name = (name ?? "").Trim(),
            WordDocPath = wordDocPath ?? "",
            // Every template ships with this entry point, so seed it rather than leaving the
            // field blank. Blank has always resolved to the same name at call time, but only
            // after the UI had shown an empty box that looked like something still to fill in.
            MacroName = WordMacroService.DefaultMacroName,
        };
        if (p.Name.Length == 0) p.Name = "Profile";
        await _db.BidProfiles.InsertOneAsync(p);
        return p;
    }

    public async Task UpdateAsync(Profile profile)
    {
        profile.UpdatedAt = DateTime.UtcNow;
        await _db.BidProfiles.ReplaceOneAsync(
            Builders<Profile>.Filter.Eq(x => x.Id, profile.Id),
            profile,
            new ReplaceOptions { IsUpsert = true });
    }

    public Task DeleteAsync(ObjectId id) =>
        _db.BidProfiles.DeleteOneAsync(p => p.Id == id);

    /// <summary>Counts (links, bids, interviews) owned by a profile — used by the delete guard.</summary>
    public async Task<(long links, long bids, long interviews)> OwnedRowCountsAsync(ObjectId profileId)
    {
        var links = await _db.Links.CountDocumentsAsync(l => l.ProfileId == profileId);
        var bids = await _db.Bids.CountDocumentsAsync(b => b.ProfileId == profileId);
        var ivs = await _db.Interviews.CountDocumentsAsync(i => i.ProfileId == profileId);
        return (links, bids, ivs);
    }
}
