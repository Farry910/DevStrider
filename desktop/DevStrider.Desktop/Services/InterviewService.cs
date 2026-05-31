using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace DevStrider.Desktop.Services;

public class InterviewService
{
    private readonly MongoContext _db;
    public InterviewService(MongoContext db) => _db = db;

    public Task<List<Interview>> ListAsync(DateTime fromUtc, DateTime toUtc) =>
        _db.Interviews
            .Find(Builders<Interview>.Filter.And(
                Builders<Interview>.Filter.Gte(i => i.ScheduledDate, fromUtc),
                Builders<Interview>.Filter.Lt(i => i.ScheduledDate, toUtc)))
            .SortBy(i => i.ScheduledDate)
            .ToListAsync();

    public async Task<Interview> CreateAsync(Interview iv)
    {
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
}
