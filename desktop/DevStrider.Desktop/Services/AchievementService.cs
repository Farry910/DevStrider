using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;

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

/// <summary>
/// Progress against the account's goals.
///
/// <para>
/// Every count here is per person and deliberately not per profile — someone running three
/// bidding identities has one daily bid target, not three. The repository counters used below are
/// the ones that skip the profile filter for exactly that reason.
/// </para>
/// </summary>
public class AchievementService
{
    private readonly IBidRepository _bids;
    private readonly IInterviewRepository _interviews;
    private readonly ProfileService _account;

    public AchievementService(IBidRepository bids, IInterviewRepository interviews, ProfileService account)
    {
        _bids = bids;
        _interviews = interviews;
        _account = account;
    }

    public async Task<AchievementProgress> CurrentAsync()
    {
        var g = (await _account.GetAsync()).Goals;
        var (dayFrom, dayTo) = LocalDay(DateTime.Now);
        var (weekFrom, weekTo) = Rolling7(DateTime.Now);
        var (monFrom, monTo) = LocalMonth(DateTime.Now);

        // "Bids" = any row that has moved off draft, i.e. a bid actually sent.
        var bidsToday = await _bids.CountNonDraftUpdatedBetweenAsync(dayFrom, dayTo);

        var ivWeek = await _interviews.CountCreatedBetweenWithStatusAsync(weekFrom, weekTo, new[]
        {
            InterviewStatuses.Scheduled,
            InterviewStatuses.Completed,
            InterviewStatuses.Passed,
        });

        var offersMonth = await _bids.CountWithStatusUpdatedBetweenAsync(
            new[] { BidStatuses.Offer, BidStatuses.Accepted }, monFrom, monTo);

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
