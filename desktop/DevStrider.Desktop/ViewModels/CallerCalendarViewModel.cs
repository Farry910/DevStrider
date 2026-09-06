using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;
using DevStrider.Desktop.Views;

namespace DevStrider.Desktop.ViewModels;

/// <summary>
/// The caller's week. Owns which week is shown, what is on it, and every write the calendar can
/// make — the view does layout and mouse work and calls in here to change anything.
///
/// <para>
/// The rule the whole screen exists to enforce: the active profile's interviews are editable, every
/// other profile of the same caller is drawn and left alone. Both halves matter. Showing only your
/// own would hide exactly the clashes this is for, and letting a profile switcher edit another
/// account's bookings is not something a person can mean to do.
/// </para>
/// </summary>
public partial class CallerCalendarViewModel : ViewModelBase
{
    private readonly CallerScheduleService _schedule;
    private readonly InterviewService _interviews;
    private readonly ProfileContext _profileContext;

    /// <summary>Days drawn at once. A working week's worth of context is what catches a clash.</summary>
    public const int DaysInWeek = 7;

    public ObservableCollection<CallerCalendarEvent> Events { get; } = new();

    /// <summary>Rows in the window with no usable time — shown in a band, never dropped.</summary>
    public ObservableCollection<CallerCalendarEvent> Untimed { get; } = new();

    /// <summary>Raised after <see cref="Events"/> is rebuilt so the view can redraw.</summary>
    public event Action? EventsChanged;

    private DateTime _weekStart = StartOfWeek(DateTime.Today);
    /// <summary>Monday of the displayed week, local.</summary>
    public DateTime WeekStart
    {
        get => _weekStart;
        private set
        {
            if (SetProperty(ref _weekStart, value))
            {
                OnPropertyChanged(nameof(WeekEnd));
                OnPropertyChanged(nameof(RangeLabel));
            }
        }
    }

    /// <summary>Exclusive end of the displayed week.</summary>
    public DateTime WeekEnd => WeekStart.AddDays(DaysInWeek);

    public string RangeLabel =>
        WeekStart.Month == WeekEnd.AddDays(-1).Month
            ? $"{WeekStart:MMMM yyyy}"
            : $"{WeekStart:MMM d} – {WeekEnd.AddDays(-1):MMM d, yyyy}";

    private string _callerLabel = "";
    /// <summary>Who the calendar belongs to, or why it is empty. Shown next to the week.</summary>
    public string CallerLabel { get => _callerLabel; private set => SetProperty(ref _callerLabel, value); }

    private bool _hasCaller;
    /// <summary>False when the active profile has no caller — the view shows an explanation instead of a grid.</summary>
    public bool HasCaller { get => _hasCaller; private set => SetProperty(ref _hasCaller, value); }

    public CallerCalendarViewModel(
        CallerScheduleService schedule,
        InterviewService interviews,
        ProfileContext profileContext)
    {
        _schedule = schedule;
        _interviews = interviews;
        _profileContext = profileContext;

        // A different profile is a different caller, and usually a different diary.
        profileContext.ProfileChanged += () => _ = ReloadAsync();
    }

    [RelayCommand]
    public async Task PreviousWeekAsync()
    {
        WeekStart = WeekStart.AddDays(-DaysInWeek);
        await ReloadAsync();
    }

    [RelayCommand]
    public async Task NextWeekAsync()
    {
        WeekStart = WeekStart.AddDays(DaysInWeek);
        await ReloadAsync();
    }

    [RelayCommand]
    public async Task TodayAsync()
    {
        WeekStart = StartOfWeek(DateTime.Today);
        await ReloadAsync();
    }

    [RelayCommand]
    public Task RefreshAsync() => ReloadAsync();

    public async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            var profile = _profileContext.Current;
            HasCaller = profile?.CallerId != null;

            Events.Clear();
            Untimed.Clear();

            if (!HasCaller)
            {
                CallerLabel = profile == null
                    ? "No active profile."
                    : $"'{profile.Name}' has no caller assigned. Set one on the Profiles tab to see their calendar.";
                EventsChanged?.Invoke();
                return;
            }

            var week = await _schedule.LoadAsync(WeekStart, WeekEnd);
            foreach (var e in week.Timed) Events.Add(e);
            foreach (var e in week.Untimed) Untimed.Add(e);

            var total = week.Timed.Count + week.Untimed.Count;
            var others = week.Timed.Count(e => !e.IsActiveProfile)
                       + week.Untimed.Count(e => !e.IsActiveProfile);
            CallerLabel = total == 0
                ? "Nothing booked this week."
                : others == 0
                    ? $"{total} this week, all under '{profile!.Name}'."
                    : $"{total} this week — {total - others} under '{profile!.Name}', " +
                      $"{others} under this caller's other profiles.";

            StatusMessage = "";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't load the caller's calendar: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            EventsChanged?.Invoke();
        }
    }

    /// <summary>
    /// Move or resize one of the active profile's interviews, after checking the slot against
    /// every other booking the caller has.
    ///
    /// <para>
    /// The conflict prompt is the point of the feature, so it names what it collided with rather
    /// than saying "there is a conflict" — you cannot judge whether to override a clash you can't
    /// see. Returns false when the user backs out, so the view can put the block back.
    /// </para>
    /// </summary>
    public async Task<bool> TryRescheduleAsync(CallerCalendarEvent ev, DateTime newStart, TimeSpan duration)
    {
        if (!ev.IsActiveProfile)
        {
            StatusMessage = "That interview belongs to another profile of this caller — switch to it to make changes.";
            return false;
        }

        var newEnd = newStart + duration;
        var clashes = CallerScheduleService.FindConflicts(Events, newStart, newEnd, ev.Interview.Id);
        if (clashes.Count > 0 && !ConfirmOverlap(newStart, newEnd, clashes)) return false;

        try
        {
            ev.Interview.ScheduledDate = newStart.Date;
            ev.Interview.ScheduledTime = CallerScheduleService.FormatTimeOfDay(newStart.TimeOfDay);
            ev.Interview.DurationMinutes = (int)Math.Round(duration.TotalMinutes);
            await _interviews.UpdateAsync(ev.Interview);

            StatusMessage = $"{Describe(ev)} → {newStart:ddd MMM d}, {newStart:HH:mm}–{newEnd:HH:mm}.";
            await ReloadAsync();
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't save: {ex.Message}";
            await ReloadAsync();
            return false;
        }
    }

    /// <summary>
    /// Apply an edit made in the dialog. Same conflict gate as a drag — a time typed into a box
    /// can collide exactly as easily as one dropped onto the grid.
    /// </summary>
    public async Task<bool> TryApplyEditAsync(
        CallerCalendarEvent ev, DateTime date, TimeSpan time, int durationMinutes,
        string interviewType, string status, string company, string role, string recruiter, string meetingLink)
    {
        if (!ev.IsActiveProfile)
        {
            StatusMessage = "That interview belongs to another profile of this caller — switch to it to make changes.";
            return false;
        }

        var start = date.Date + time;
        var end = start.AddMinutes(durationMinutes);
        var clashes = CallerScheduleService.FindConflicts(Events, start, end, ev.Interview.Id);
        if (clashes.Count > 0 && !ConfirmOverlap(start, end, clashes)) return false;

        try
        {
            var iv = ev.Interview;
            iv.ScheduledDate = date.Date;
            iv.ScheduledTime = CallerScheduleService.FormatTimeOfDay(time);
            iv.DurationMinutes = durationMinutes;
            iv.InterviewType = interviewType;
            iv.Status = status;
            iv.Company = company;
            iv.Role = role;
            iv.Recruiter = recruiter;
            iv.MeetingLink = meetingLink;
            await _interviews.UpdateAsync(iv);

            StatusMessage = $"Saved {Describe(ev)}.";
            await ReloadAsync();
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't save: {ex.Message}";
            await ReloadAsync();
            return false;
        }
    }

    /// <summary>
    /// Name every clash and ask. Whose profile each one is under is the part that decides it: a
    /// collision with the caller's other identity is the mistake this screen exists to catch,
    /// while two rounds of your own at one hour may be something you meant.
    /// </summary>
    private static bool ConfirmOverlap(DateTime start, DateTime end, List<CallerCalendarEvent> clashes)
    {
        var lines = clashes.Take(6).Select(c =>
            $"• {c.Start:HH:mm}–{c.End:HH:mm}  {c.Title}  ({c.ProfileName})");
        var more = clashes.Count > 6 ? $"\n…and {clashes.Count - 6} more." : "";

        return ConfirmDialog.Ask(
            System.Windows.Application.Current?.MainWindow,
            "The caller is already booked",
            $"{start:ddd MMM d}, {start:HH:mm}–{end:HH:mm} overlaps:\n\n" +
            string.Join("\n", lines) + more +
            "\n\nOne caller can only be in one of these.",
            okText: "Schedule anyway", cancelText: "Pick another time", danger: true);
    }

    private static string Describe(CallerCalendarEvent ev) =>
        string.IsNullOrWhiteSpace(ev.Title) ? "interview" : ev.Title;

    /// <summary>Monday-first, which is how a working week reads.</summary>
    public static DateTime StartOfWeek(DateTime day)
    {
        var delta = (7 + (int)day.DayOfWeek - (int)DayOfWeek.Monday) % 7;
        return day.Date.AddDays(-delta);
    }
}
