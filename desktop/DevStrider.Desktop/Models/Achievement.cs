using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DevStrider.Desktop.Models;

public static class AchievementKinds
{
    public const string DailyBids = "daily_bids";
    public const string WeeklyInterviews = "weekly_interviews";
    public const string MonthlyOffers = "monthly_offers";
}

public class Achievement
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    public string Kind { get; set; } = "";
    /// <summary>e.g. "2026-05-25" for day, "2026-W21" for week, "2026-05" for month.</summary>
    public string PeriodKey { get; set; } = "";
    public int MetricValue { get; set; }
    public int Target { get; set; }
    public DateTime AchievedAt { get; set; } = DateTime.UtcNow;
}
