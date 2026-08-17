using MongoDB.Bson;

namespace DevStrider.Desktop.Models;

public static class AchievementKinds
{
    public const string DailyBids = "daily_bids";
    public const string WeeklyInterviews = "weekly_interviews";
    public const string MonthlyOffers = "monthly_offers";
}

/// <summary>
/// One goal actually hit, kept so a streak survives a reinstall. Per account and not per profile,
/// for the same reason the goals themselves are — see <see cref="UserProfile.Goals"/>.
/// </summary>
public class Achievement
{
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    /// <summary>Owning account — <c>app_user.id</c>. Stamped by the repository on write.</summary>
    public long UserId { get; set; }

    public string Kind { get; set; } = "";

    /// <summary>e.g. "2026-05-25" for day, "2026-W21" for week, "2026-05" for month.</summary>
    public string PeriodKey { get; set; } = "";

    public int MetricValue { get; set; }
    public int Target { get; set; }
    public DateTime AchievedAt { get; set; } = DateTime.UtcNow;
}
