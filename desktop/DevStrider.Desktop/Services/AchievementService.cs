using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Driver;

namespace DevStrider.Desktop.Services;

public class AchievementProgress
{
    public int DailyBidsValue { get; set; }
    public int DailyBidsTarget { get; set; }
    public int WeeklyInterviewsValue { get; set; }
    public int WeeklyInterviewsTarget { get; set; }
    public int MonthlyOffersValue { get; set; }
    public int MonthlyOffersTarget { get; set; }
}

public class AchievementService
{
    private readonly MongoContext _db;
    private readonly ProfileService _profiles;

    public AchievementService(MongoContext db, ProfileService profiles)
    {
        _db = db;
        _profiles = profiles;
    }

    public async Task<AchievementProgress> CurrentAsync()
    {
        var p = await _profiles.GetAsync();
        var g = p.Goals;
        var (dayFrom, dayTo) = LocalDay(DateTime.Now);
        var (weekFrom, weekTo) = Rolling7(DateTime.Now);
        var (monFrom, monTo) = LocalMonth(DateTime.Now);

        // "Bids" = any non-draft bid, gated on AppliedAt (fallback firstCreatedAt).
        var bidsToday = await _db.Bids
            .Find(b => b.Status != BidStatuses.Draft
                    && b.UpdatedAt >= dayFrom && b.UpdatedAt < dayTo)
            .CountDocumentsAsync();

        var ivWeek = await _db.Interviews
            .Find(i => i.CreatedAt >= weekFrom && i.CreatedAt < weekTo
                    && (i.Status == InterviewStatuses.Scheduled
                     || i.Status == InterviewStatuses.Completed
                     || i.Status == InterviewStatuses.Passed))
            .CountDocumentsAsync();

        var offersMonth = await _db.Bids
            .Find(b => (b.Status == BidStatuses.Offer || b.Status == BidStatuses.Accepted)
                    && b.UpdatedAt >= monFrom && b.UpdatedAt < monTo)
            .CountDocumentsAsync();

        return new AchievementProgress
        {
            DailyBidsValue = (int)bidsToday,
            DailyBidsTarget = g.BidsPerDay,
            WeeklyInterviewsValue = (int)ivWeek,
            WeeklyInterviewsTarget = g.InterviewsPerWeek,
            MonthlyOffersValue = (int)offersMonth,
            MonthlyOffersTarget = g.OffersPerMonth,
        };
    }

    private static (DateTime, DateTime) LocalDay(DateTime now)
    {
        var s = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();
        return (s, s.AddDays(1));
    }

    private static (DateTime, DateTime) Rolling7(DateTime now)
    {
        var end = now.ToUniversalTime();
        return (end.AddDays(-7), end);
    }

    private static (DateTime, DateTime) LocalMonth(DateTime now)
    {
        var s = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();
        var e = s.AddMonths(1);
        return (s, e);
    }
}
