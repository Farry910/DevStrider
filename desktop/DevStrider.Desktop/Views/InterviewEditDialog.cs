using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;
using Brushes = System.Windows.Media.Brushes;

namespace DevStrider.Desktop.Views;

/// <summary>
/// Edit one interview from the caller calendar.
///
/// <para>
/// Distinct from <see cref="ScheduleInterviewDialog"/>, which creates a first round off a bid and
/// so has no row to start from. This one opens on an existing interview and carries the two fields
/// a calendar needs and that one has no reason to ask for: the duration, which decides how tall the
/// block is, and the status, which is the thing most often changed after the fact.
/// </para>
///
/// <para>
/// Times are wall clock, typed the same way they are everywhere else in the app — see
/// <see cref="CallerScheduleService.TryResolveStart"/> for why no timezone conversion happens to
/// them.
/// </para>
/// </summary>
public sealed class InterviewEditDialog : Window
{
    private readonly DatePicker _date = new();
    private readonly TextBox _time = new() { Tag = "HH:mm" };
    private readonly TextBox _duration = new() { Tag = "Minutes" };
    private readonly ComboBox _type = new();
    private readonly ComboBox _status = new();
    private readonly TextBox _company = new();
    private readonly TextBox _role = new();
    private readonly TextBox _recruiter = new() { Tag = "Recruiter name (optional)" };
    private readonly TextBox _meetingLink = new() { Tag = "Meeting link (optional)" };
    private readonly TextBlock _error = new()
    {
        Foreground = Brushes.IndianRed,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
        Margin = new Thickness(0, 4, 0, 0)
    };

    public DateTime ScheduledDate { get; private set; }
    public TimeSpan ScheduledTime { get; private set; }
    public int DurationMinutes { get; private set; }
    public string InterviewType => (_type.SelectedItem as string) ?? InterviewTypes.Interview;
    public string Status => (_status.SelectedItem as string) ?? InterviewStatuses.Scheduled;
    public string Company => _company.Text?.Trim() ?? "";
    public string Role => _role.Text?.Trim() ?? "";
    public string Recruiter => _recruiter.Text?.Trim() ?? "";
    public string MeetingLink => _meetingLink.Text?.Trim() ?? "";

    public InterviewEditDialog(CallerCalendarEvent ev)
    {
        var iv = ev.Interview;

        Title = "Edit interview";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        foreach (var t in InterviewTypes.All) _type.Items.Add(t);
        _type.SelectedItem = InterviewTypes.All.Contains(iv.InterviewType)
            ? iv.InterviewType
            : InterviewTypes.All.FirstOrDefault();

        foreach (var s in new[]
                 {
                     InterviewStatuses.Scheduled, InterviewStatuses.Completed, InterviewStatuses.Passed,
                     InterviewStatuses.Failed, InterviewStatuses.Cancelled,
                 })
            _status.Items.Add(s);
        _status.SelectedItem = _status.Items.Contains(iv.Status) ? iv.Status : InterviewStatuses.Scheduled;

        _date.SelectedDate = ev.Start.Date;
        _time.Text = CallerScheduleService.FormatTimeOfDay(ev.Start.TimeOfDay);
        _duration.Text = ((int)Math.Round(ev.Duration.TotalMinutes)).ToString();
        _company.Text = iv.Company ?? "";
        _role.Text = iv.Role ?? "";
        _recruiter.Text = iv.Recruiter ?? "";
        _meetingLink.Text = iv.MeetingLink ?? "";

        var header = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        header.Inlines.Add(new Run("Profile: ") { Foreground = Brushes.Gray });
        header.Inlines.Add(new Run(ev.ProfileName) { FontWeight = FontWeights.SemiBold });

        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 13; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void AddRow(int row, string label, FrameworkElement field)
        {
            var lbl = new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 6, 12, 6),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0); grid.Children.Add(lbl);
            Grid.SetRow(field, row); Grid.SetColumn(field, 1);
            field.Margin = new Thickness(0, 6, 0, 6);
            grid.Children.Add(field);
        }

        Grid.SetColumnSpan(header, 2); Grid.SetRow(header, 0); grid.Children.Add(header);
        AddRow(1,  "Date",         _date);
        AddRow(2,  "Time",         _time);
        AddRow(3,  "Duration",     _duration);
        AddRow(4,  "Type",         _type);
        AddRow(5,  "Status",       _status);
        AddRow(6,  "Company",      _company);
        AddRow(7,  "Role",         _role);
        AddRow(8,  "Recruiter",    _recruiter);
        AddRow(9,  "Meeting link", _meetingLink);

        Grid.SetColumnSpan(_error, 2); Grid.SetRow(_error, 10); grid.Children.Add(_error);

        var ok = new Button { Content = "Save", IsDefault = true, MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88 };
        ok.Click += (_, _) => { if (Validate()) { DialogResult = true; Close(); } };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetColumnSpan(buttons, 2); Grid.SetRow(buttons, 11); grid.Children.Add(buttons);

        Content = grid;
    }

    /// <summary>
    /// Parse before closing rather than after. A dialog that accepts "half past ten" and then
    /// silently writes nothing is worse than one that says what it can't read while the box is
    /// still in front of you.
    /// </summary>
    private bool Validate()
    {
        if (_date.SelectedDate is not { } date) return Fail("Pick a date.");

        if (!CallerScheduleService.TryParseTimeOfDay(_time.Text ?? "", out var time))
            return Fail($"'{_time.Text}' isn't a time. Use 24-hour HH:mm, like 14:30.");

        if (!int.TryParse((_duration.Text ?? "").Trim(), out var minutes) || minutes <= 0)
            return Fail("Duration must be a whole number of minutes.");
        if (minutes > 24 * 60)
            return Fail("Duration can't run past a day.");

        ScheduledDate = date.Date;
        ScheduledTime = time;
        DurationMinutes = minutes;
        return true;
    }

    private bool Fail(string message)
    {
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
        return false;
    }
}
