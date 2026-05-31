using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Driver;

namespace DevStrider.Desktop.Services;

public class SettingsService
{
    private readonly MongoContext _db;
    public SettingsService(MongoContext db) => _db = db;

    /// <summary>Returns the singleton settings row, creating defaults on first run.</summary>
    public async Task<AppSettings> GetAsync()
    {
        var doc = await _db.Settings.Find(FilterDefinition<AppSettings>.Empty).FirstOrDefaultAsync();
        if (doc != null) return doc;
        var seed = new AppSettings();
        await _db.Settings.InsertOneAsync(seed);
        return seed;
    }

    public async Task SaveAsync(AppSettings s)
    {
        s.UpdatedAt = DateTime.UtcNow;
        await _db.Settings.ReplaceOneAsync(
            Builders<AppSettings>.Filter.Eq(x => x.Id, s.Id),
            s,
            new ReplaceOptions { IsUpsert = true });
    }
}
