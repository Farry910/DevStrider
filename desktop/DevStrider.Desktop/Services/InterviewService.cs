using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace DevStrider.Desktop.Services;

public class InterviewService
{
    private readonly MongoContext _db;
    private readonly ProfileContext _profileContext;

    public InterviewService(MongoContext db, ProfileContext profileContext)
    {
        _db = db;
        _profileContext = profileContext;
    }

    private ObjectId ActiveProfileId => _profileContext.Current?.Id ?? ObjectId.Empty;

    public Task<List<Interview>> ListAsync(DateTime fromUtc, DateTime toUtc)
    {
        var profileId = ActiveProfileId;
        if (profileId == ObjectId.Empty) return Task.FromResult(new List<Interview>());
        return _db.Interviews
            .Find(Builders<Interview>.Filter.And(
                Builders<Interview>.Filter.Eq(i => i.ProfileId, profileId),
                Builders<Interview>.Filter.Gte(i => i.ScheduledDate, fromUtc),
                Builders<Interview>.Filter.Lt(i => i.ScheduledDate, toUtc)))
            .SortBy(i => i.ScheduledDate)
            .ToListAsync();
    }

    public async Task<Interview> CreateAsync(Interview iv)
    {
        if (iv.ProfileId == ObjectId.Empty) iv.ProfileId = ActiveProfileId;
        if (iv.ProfileId == ObjectId.Empty)
            throw new InvalidOperationException("No active profile — create one in the Profiles tab first.");
        iv.CreatedAt = DateTime.UtcNow;
        iv.UpdatedAt = iv.CreatedAt;
        await _db.Interviews.InsertOneAsync(iv);
        return iv;
    }

    public async Task UpdateAsync(Interview iv)
    {
        iv.UpdatedAt = DateTime.UtcNow;
        await _db.Interviews.ReplaceOneAsync(i => i.Id == iv.Id, iv);
    }

    public Task DeleteAsync(ObjectId id) =>
        _db.Interviews.DeleteOneAsync(i => i.Id == id);

    /// <summary>True when at least one interview is attached to the given bid.</summary>
    public Task<bool> HasForBidAsync(ObjectId bidId) =>
        _db.Interviews.Find(i => i.BidId == bidId).AnyAsync();
}
