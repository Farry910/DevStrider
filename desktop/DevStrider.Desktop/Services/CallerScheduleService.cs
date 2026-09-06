using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Bson;

namespace DevStrider.Desktop.Services;

/// <summary>
/// One interview placed on the caller's calendar: the row itself, when it actually starts and
/// ends, whose profile it belongs to, and whether this session may edit it.
/// </summary>
public sealed class CallerCalendarEvent
{
    public required Interview Interview { get; init; }

    /// <summary>Profile this interview sits under — "Chen Chen", not the account name.</summary>
    public required string ProfileName { get; init; }

    /// <summary>
    /// True only for the active profile's own rows. Everything else on the calendar belongs to
    /// another profile the same caller covers and is shown to be worked around, not edited: it may
    /// well belong to a different account entirely, and editing someone else's booking from inside
    /// a profile switcher is not a thing a person can mean to do.
    /// </summary>
    public required bool IsActiveProfile { get; init; }

    /// <summary>Local wall-clock start. See <see cref="CallerScheduleService.TryResolveStart"/>.</summary>
    public required DateTime Start { get; init; }

    /// <summary>Start plus the duration, defaulted when the row carries none.</summary>
    public required DateTime End { get; init; }

    public TimeSpan Duration => End - Start;

    /// <summary>"Acme · Tech 1", the label drawn in the block.</summary>
    public string Title =>
        string.Join(" · ", new[] { Interview.Company, Interview.InterviewType }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

    // ── layout, filled in by the view once overlaps are known ────────────────

    /// <summary>Which parallel column this block occupies within its overlap cluster.</summary>
    public int Column { get; set; }

    /// <summary>How many columns that cluster was split into. 1 means the block spans the day.</summary>
    public int ColumnCount { get; set; } = 1;
}

/// <summary>
/// The caller's diary: every interview belonging to every profile the active profile's caller
/// covers, whoever owns those profiles.
///
/// <para>
/// <b>Why this reads across accounts.</b> A caller is one person and has one diary. Their profiles
/// are commonly spread over several DevStrider accounts, so "everything this caller is booked for"
/// is not a question the account-scoped interview repository can answer — it is
/// <see cref="IPeerDirectory"/>'s, which reads the whole team by design. The filter that turns the
/// team's interviews into one caller's is <c>ds_profiles.caller_id</c>, carried on every
/// <see cref="PeerIdentity"/> so this costs one identities call rather than one per profile.
/// </para>
///
/// <para>
/// <b>Nothing here writes.</b> Rescheduling goes through <see cref="InterviewService"/> like every
/// other edit, and only ever for the active profile's own rows.
/// </para>
/// </summary>
public sealed class CallerScheduleService
{
    /// <summary>
    /// Assumed length of an interview whose row says nothing. An hour is what the scheduling
    /// dialog implies and what a reader assumes when they see a block with no end — and for a
    /// calendar whose job is spotting collisions, guessing too short would hide them.
    /// </summary>
    public const int DefaultDurationMinutes = 60;

    private readonly IPeerDirectory _peers;
    private readonly ProfileContext _profileContext;

    public CallerScheduleService(IPeerDirectory peers, ProfileContext profileContext)
    {
        _peers = peers;
        _profileContext = profileContext;
    }

    /// <summary>The active profile's caller, or null when it has none assigned.</summary>
    public long? ActiveCallerId => _profileContext.Current?.CallerId;

    /// <summary>
    /// A week of the caller's diary, split into what can be placed on the grid and what can't.
    /// </summary>
    /// <param name="Timed">Interviews with a resolvable start, in start order.</param>
    /// <param name="Untimed">
    /// Interviews in the window carrying a date but no usable time. They are real commitments, so
    /// they are handed back rather than dropped — a conflict screen that quietly loses bookings is
    /// worse than none.
    /// </param>
    public sealed record CallerWeek(
        List<CallerCalendarEvent> Timed,
        List<CallerCalendarEvent> Untimed);

    /// <summary>
    /// Every interview in the window belonging to any profile the active profile's caller covers.
    /// Returns empty — never throws — when there is no active profile or it has no caller; a
    /// profile without a caller has no diary to show, which the view says in words.
    ///
    /// <para>
    /// <paramref name="fromLocal"/> and <paramref name="toLocal"/> are wall-clock, matching how
    /// interview times are stored and displayed everywhere else in the app.
    /// </para>
    /// </summary>
    public async Task<CallerWeek> LoadAsync(DateTime fromLocal, DateTime toLocal)
    {
        var empty = new CallerWeek(new List<CallerCalendarEvent>(), new List<CallerCalendarEvent>());
        var active = _profileContext.Current;
        if (active?.CallerId is not { } callerId) return empty;

        // Every (person, profile) pair on the team, so the caller's profiles are a filter rather
        // than a second request.
        var identities = await _peers.ListIdentitiesAsync();
        var mine = identities
            .Where(i => i.CallerId == callerId)
            .GroupBy(i => i.ProfileId)
            .ToDictionary(g => g.Key, g => g.First().ProfileName);

        // The active profile may not be in the directory yet — it is if it has ever been saved,
        // but a caller just assigned to a brand-new profile shouldn't drop off its own calendar.
        if (!mine.ContainsKey(active.Id)) mine[active.Id] = active.Name;
        if (mine.Count == 0) return empty;

        // The window is widened by a day on each side before it goes to the server: rows are
        // filtered there on scheduled_date alone, and a row dated the day before the window can
        // still start inside it once its time is added.
        var interviews = await _peers.ListInterviewsScheduledBetweenAsync(
            fromLocal.Date.AddDays(-1), toLocal.Date.AddDays(1), includeUndated: false);

        var timed = new List<CallerCalendarEvent>();
        var untimed = new List<CallerCalendarEvent>();

        foreach (var iv in interviews)
        {
            if (!mine.TryGetValue(iv.ProfileId, out var profileName)) continue;

            var isOwn = iv.ProfileId == active.Id;
            var name = profileName ?? "";

            if (!TryResolveStart(iv, out var start))
            {
                // Dated but with no readable time. Keep it if the date lands in the window — it
                // still tells you the caller has something on that day.
                if (iv.ScheduledDate is not { } d || d.Date < fromLocal.Date || d.Date >= toLocal.Date) continue;
                untimed.Add(new CallerCalendarEvent
                {
                    Interview = iv,
                    ProfileName = name,
                    IsActiveProfile = isOwn,
                    Start = d.Date,
                    End = d.Date,
                });
                continue;
            }

            if (start >= toLocal || start < fromLocal) continue;

            var minutes = iv.DurationMinutes is > 0 ? iv.DurationMinutes!.Value : DefaultDurationMinutes;
            timed.Add(new CallerCalendarEvent
            {
                Interview = iv,
                ProfileName = name,
                IsActiveProfile = isOwn,
                Start = start,
                End = start.AddMinutes(minutes),
            });
        }

        return new CallerWeek(
            timed.OrderBy(e => e.Start).ToList(),
            untimed.OrderBy(e => e.Start).ToList());
    }

    /// <summary>
    /// Turn a row's date + time into a wall-clock instant.
    ///
    /// <para>
    /// <b>No timezone conversion, deliberately.</b> <c>scheduled_date</c> is a date carried in a
    /// TIMESTAMPTZ column: the repositories relabel local midnight as UTC rather than converting
    /// it (see <c>Iso.Utc</c>), so its <see cref="DateTime.Date"/> is the day someone picked, and
    /// converting it would slide the day across a timezone boundary. <c>scheduled_time</c> is
    /// free text a person typed, "HH:mm" by the dialog's prompt. Both are wall clock, and treating
    /// them as anything else is what would make an interview show up on the wrong day.
    /// </para>
    /// </summary>
    public static bool TryResolveStart(Interview iv, out DateTime start)
    {
        start = default;
        if (iv.ScheduledDate is not { } date) return false;

        var raw = (iv.ScheduledTime ?? "").Trim();
        if (raw.Length == 0) return false;

        if (!TryParseTimeOfDay(raw, out var time)) return false;

        start = date.Date + time;
        return true;
    }

    /// <summary>
    /// "14:30", "2:30 PM", "9.00", "0930" — the field is a free text box, so this accepts the
    /// shapes people actually type and rejects the rest rather than guessing a time onto a row.
    /// </summary>
    public static bool TryParseTimeOfDay(string raw, out TimeSpan time)
    {
        time = default;
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return false;

        var formats = new[] { @"h\:mm", @"hh\:mm", @"h\.mm", @"hh\.mm", @"hhmm", @"h\:mm\:ss", @"hh\:mm\:ss" };
        if (TimeSpan.TryParseExact(text, formats, System.Globalization.CultureInfo.InvariantCulture, out time))
            return time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);

        // AM/PM and anything else the current culture understands as a time of day.
        if (DateTime.TryParse(text, System.Globalization.CultureInfo.CurrentCulture,
                System.Globalization.DateTimeStyles.NoCurrentDateDefault, out var dt))
        {
            time = dt.TimeOfDay;
            return true;
        }

        return false;
    }

    /// <summary>Wall-clock "HH:mm", the form the rest of the app writes and reads.</summary>
    public static string FormatTimeOfDay(TimeSpan time) =>
        $"{(int)time.TotalHours:00}:{time.Minutes:00}";

    /// <summary>
    /// Give every event a column so overlapping interviews can be drawn side by side instead of on
    /// top of each other — the visual half of the promise this feature makes.
    ///
    /// <para>
    /// Events are swept per day in start order. A <em>cluster</em> is a run that transitively
    /// overlaps; within one, each event takes the lowest-numbered column whose previous occupant
    /// has already ended, and when the cluster closes every member is told how many columns it
    /// ended up needing, so they all come out the same width. Clusters are independent, which is
    /// what stops a crowded morning from narrowing a quiet afternoon.
    /// </para>
    ///
    /// <para>
    /// Pure geometry over times — no pixels — so it lives here with the rest of the scheduling
    /// rules rather than in the view that happens to draw the result.
    /// </para>
    /// </summary>
    /// <returns>The same events, in draw order, with Column/ColumnCount filled in.</returns>
    public static List<CallerCalendarEvent> AssignColumns(IEnumerable<CallerCalendarEvent> events)
    {
        var all = events.OrderBy(e => e.Start).ThenBy(e => e.End).ToList();

        foreach (var day in all.GroupBy(e => e.Start.Date))
        {
            var cluster = new List<CallerCalendarEvent>();
            var columnEnds = new List<DateTime>();
            var clusterEnd = DateTime.MinValue;

            void CloseCluster()
            {
                foreach (var e in cluster) e.ColumnCount = Math.Max(1, columnEnds.Count);
                cluster.Clear();
                columnEnds.Clear();
                clusterEnd = DateTime.MinValue;
            }

            foreach (var ev in day.OrderBy(e => e.Start))
            {
                // Nothing in the running cluster is still open, so this starts a fresh one.
                if (cluster.Count > 0 && ev.Start >= clusterEnd) CloseCluster();

                var placed = false;
                for (var c = 0; c < columnEnds.Count; c++)
                {
                    if (columnEnds[c] <= ev.Start)
                    {
                        ev.Column = c;
                        columnEnds[c] = ev.End;
                        placed = true;
                        break;
                    }
                }
                if (!placed)
                {
                    ev.Column = columnEnds.Count;
                    columnEnds.Add(ev.End);
                }

                cluster.Add(ev);
                if (ev.End > clusterEnd) clusterEnd = ev.End;
            }

            if (cluster.Count > 0) CloseCluster();
        }

        return all;
    }

    /// <summary>
    /// The caller's other bookings that a proposed slot would land on top of. Half-open intervals,
    /// so a 10:00–11:00 and an 11:00–12:00 do not count as a clash.
    ///
    /// <para>
    /// <paramref name="ignore"/> is the interview being moved — an event always overlaps itself,
    /// and reporting that would make every drag look like a mistake.
    /// </para>
    /// </summary>
    public static List<CallerCalendarEvent> FindConflicts(
        IEnumerable<CallerCalendarEvent> all, DateTime start, DateTime end, ObjectId ignore) =>
        all.Where(e => e.Interview.Id != ignore && start < e.End && e.Start < end)
           .OrderBy(e => e.Start)
           .ToList();
}
