using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using Rectangle = System.Windows.Shapes.Rectangle;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;
using DevStrider.Desktop.ViewModels;

namespace DevStrider.Desktop.Views;

/// <summary>
/// A week of the caller's diary, drawn as a time grid.
///
/// <para>
/// <b>Why this is code-behind rather than bindings.</b> Every block's position is a function of two
/// things no binding can express: the hour it starts, and how many other blocks it has to share a
/// day column with. Overlap resolution is a graph problem solved across the whole day, and the
/// answer changes for blocks that were not themselves moved — so the grid is redrawn from
/// <see cref="CallerCalendarViewModel.Events"/> whenever it changes, and the drag is done against
/// pixels because that is what the user is actually manipulating.
/// </para>
///
/// <para>
/// <b>Two kinds of block, and the difference is the point.</b> The active profile's interviews are
/// solid, draggable, and resizable from their bottom edge. Every other profile the caller covers is
/// drawn hatched and inert — there to be scheduled around, not edited. Those rows frequently belong
/// to another account entirely.
/// </para>
/// </summary>
public partial class CallerCalendarView : UserControl
{
    /// <summary>Width of the hour-label gutter down the left.</summary>
    private const double GutterWidth = 56;

    /// <summary>Pixels per hour. 48 keeps a whole day on screen at a normal window height.</summary>
    private const double HourHeight = 48;

    /// <summary>Gap between two blocks sharing a slot, so a clash reads as two things not one.</summary>
    private const double ColumnGap = 3;

    /// <summary>Drags and resizes land on quarter hours — the granularity interviews are booked at.</summary>
    private const int SnapMinutes = 15;

    /// <summary>Grab strip at the bottom of an editable block.</summary>
    private const double ResizeGripHeight = 7;

    /// <summary>Below this a press is a click, above it a drag. Stops a click nudging a booking.</summary>
    private const double DragThreshold = 4;

    /// <summary>Shortest an interview can be dragged down to.</summary>
    private static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(15);

    private CallerCalendarViewModel? Vm => DataContext as CallerCalendarViewModel;

    // ── drag state ───────────────────────────────────────────────────────────
    private FrameworkElement? _dragBlock;
    private CallerCalendarEvent? _dragEvent;
    private Point _dragOrigin;
    private double _grabOffsetY;
    private bool _isResizing;
    private bool _dragPassedThreshold;

    public CallerCalendarView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => { Attach(); Render(); ScrollToWorkingHours(); };
    }

    private CallerCalendarViewModel? _attached;

    private void Attach()
    {
        if (ReferenceEquals(_attached, Vm)) return;
        if (_attached != null) _attached.EventsChanged -= OnEventsChanged;
        _attached = Vm;
        if (_attached != null) _attached.EventsChanged += OnEventsChanged;
    }

    private void OnEventsChanged() => Dispatcher.BeginInvoke(new Action(Render));

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) Render();
    }

    /// <summary>Open on the working day rather than on midnight, which is never what you want to see.</summary>
    private void ScrollToWorkingHours() => Scroller.ScrollToVerticalOffset(8 * HourHeight);

    // =====================================================================================
    // Rendering
    // =====================================================================================

    private void Render()
    {
        if (!IsLoaded || Vm == null) return;

        Surface.Children.Clear();
        HeaderRow.Children.Clear();
        HeaderRow.ColumnDefinitions.Clear();
        UntimedHost.Children.Clear();

        var width = Math.Max(0, Scroller.ActualWidth - SystemParameters.VerticalScrollBarWidth);
        if (width <= GutterWidth) return;

        Surface.Width = width;
        Surface.Height = 24 * HourHeight;
        // The header sits outside the scroller, so it has to reserve the scrollbar's width itself
        // or every column would be a few pixels out of step with the grid below it.
        HeaderRow.Margin = new Thickness(0, 0, SystemParameters.VerticalScrollBarWidth, 0);

        var dayWidth = (width - GutterWidth) / CallerCalendarViewModel.DaysInWeek;

        DrawHeader(dayWidth);
        DrawGrid(width, dayWidth);

        if (!Vm.HasCaller) return;

        DrawUntimed();
        foreach (var ev in CallerScheduleService.AssignColumns(Vm.Events)) DrawEvent(ev, dayWidth);
    }

    private void DrawHeader(double dayWidth)
    {
        HeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GutterWidth) });
        for (var i = 0; i < CallerCalendarViewModel.DaysInWeek; i++)
            HeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(dayWidth) });

        for (var i = 0; i < CallerCalendarViewModel.DaysInWeek; i++)
        {
            var day = Vm!.WeekStart.AddDays(i);
            var isToday = day.Date == DateTime.Today;

            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(new TextBlock
            {
                Text = day.ToString("ddd").ToUpperInvariant(),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Themed(isToday ? "Primary" : "Muted")
            });
            panel.Children.Add(new TextBlock
            {
                Text = day.Day.ToString(),
                FontSize = 17,
                HorizontalAlignment = HorizontalAlignment.Center,
                FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
                Foreground = Themed(isToday ? "Primary" : "Text")
            });

            Grid.SetColumn(panel, i + 1);
            HeaderRow.Children.Add(panel);
        }
    }

    private void DrawGrid(double width, double dayWidth)
    {
        var line = Themed("BorderSoft");
        var strong = Themed("Border");

        for (var h = 0; h <= 24; h++)
        {
            var y = h * HourHeight;
            Surface.Children.Add(new Line
            {
                X1 = GutterWidth, X2 = width, Y1 = y, Y2 = y,
                Stroke = line, StrokeThickness = 1, SnapsToDevicePixels = true
            });

            if (h < 24)
            {
                var label = new TextBlock
                {
                    Text = $"{h:00}:00",
                    FontSize = 10,
                    Foreground = Themed("Muted")
                };
                Canvas.SetLeft(label, 8);
                Canvas.SetTop(label, y + 2);
                Surface.Children.Add(label);
            }
        }

        for (var i = 0; i <= CallerCalendarViewModel.DaysInWeek; i++)
        {
            var x = GutterWidth + i * dayWidth;
            Surface.Children.Add(new Line
            {
                X1 = x, X2 = x, Y1 = 0, Y2 = 24 * HourHeight,
                Stroke = i == 0 ? strong : line, StrokeThickness = 1, SnapsToDevicePixels = true
            });
        }

        // Today's column, tinted so the week has an anchor.
        var todayIndex = (int)(DateTime.Today - Vm!.WeekStart).TotalDays;
        if (todayIndex >= 0 && todayIndex < CallerCalendarViewModel.DaysInWeek)
        {
            var tint = new Rectangle
            {
                Width = dayWidth - 1,
                Height = 24 * HourHeight,
                Fill = Themed("PrimarySoft"),
                Opacity = 0.35,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(tint, GutterWidth + todayIndex * dayWidth + 1);
            Canvas.SetTop(tint, 0);
            Surface.Children.Insert(0, tint);
        }
    }

    private void DrawUntimed()
    {
        var untimed = Vm!.Untimed;
        UntimedBand.Visibility = untimed.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (untimed.Count == 0) return;

        UntimedHost.Children.Add(new TextBlock
        {
            Text = "No time set:",
            FontSize = 11,
            Foreground = Themed("Muted"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });

        foreach (var ev in untimed)
        {
            UntimedHost.Children.Add(new Border
            {
                Background = Themed(ev.IsActiveProfile ? "PrimarySoft" : "SurfaceAlt"),
                BorderBrush = Themed("BorderSoft"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, 6, 0),
                Child = new TextBlock
                {
                    Text = $"{ev.Start:MMM d} · {ev.Title} ({ev.ProfileName})",
                    FontSize = 11,
                    Foreground = Themed(ev.IsActiveProfile ? "Text" : "Muted")
                }
            });
        }
    }

    private void DrawEvent(CallerCalendarEvent ev, double dayWidth)
    {
        var dayIndex = (int)(ev.Start.Date - Vm!.WeekStart).TotalDays;
        if (dayIndex < 0 || dayIndex >= CallerCalendarViewModel.DaysInWeek) return;

        var slotWidth = dayWidth / ev.ColumnCount;
        var x = GutterWidth + dayIndex * dayWidth + ev.Column * slotWidth;
        var y = ev.Start.TimeOfDay.TotalHours * HourHeight;
        var h = Math.Max(HourHeight * 0.35, ev.Duration.TotalHours * HourHeight);

        var own = ev.IsActiveProfile;

        var block = new Border
        {
            Width = Math.Max(10, slotWidth - ColumnGap),
            Height = h,
            CornerRadius = new CornerRadius(4),
            Background = own ? Themed("PrimarySoft") : Themed("SurfaceAlt"),
            BorderBrush = own ? Themed("Primary") : Themed("Border"),
            BorderThickness = new Thickness(own ? 1 : 1),
            Opacity = own ? 1.0 : 0.75,
            Cursor = own ? Cursors.Hand : Cursors.Arrow,
            Tag = ev,
            ToolTip = Tooltip(ev),
            SnapsToDevicePixels = true
        };

        var content = new StackPanel { Margin = new Thickness(6, 3, 4, 3), IsHitTestVisible = false };
        content.Children.Add(new TextBlock
        {
            Text = $"{ev.Start:HH:mm}–{ev.End:HH:mm}",
            FontSize = 10,
            Foreground = Themed("Muted")
        });
        content.Children.Add(new TextBlock
        {
            Text = ev.Title,
            FontSize = 11,
            FontWeight = own ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Themed(own ? "Text" : "Muted")
        });
        // The profile name is what tells you a block is someone else's problem, so it is on the
        // block rather than only in the tooltip — but only when it isn't yours, to keep noise down.
        if (!own && h > HourHeight * 0.75)
        {
            content.Children.Add(new TextBlock
            {
                Text = ev.ProfileName,
                FontSize = 10,
                FontStyle = FontStyles.Italic,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Themed("Muted")
            });
        }
        block.Child = content;

        if (own)
        {
            block.MouseLeftButtonDown += OnBlockMouseDown;
            block.MouseMove += OnBlockMouseMove;
            block.MouseLeftButtonUp += OnBlockMouseUp;
        }
        else
        {
            // Read-only blocks say so on hover rather than looking merely disabled.
            block.Cursor = Cursors.No;
        }

        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        Panel.SetZIndex(block, own ? 20 : 10);
        Surface.Children.Add(block);
    }

    private static string Tooltip(CallerCalendarEvent ev)
    {
        var parts = new List<string>
        {
            $"{ev.Start:ddd MMM d}  {ev.Start:HH:mm}–{ev.End:HH:mm}",
            ev.Title,
            $"Profile: {ev.ProfileName}",
        };
        if (!string.IsNullOrWhiteSpace(ev.Interview.Role)) parts.Add($"Role: {ev.Interview.Role}");
        if (!string.IsNullOrWhiteSpace(ev.Interview.Recruiter)) parts.Add($"Recruiter: {ev.Interview.Recruiter}");
        if (!string.IsNullOrWhiteSpace(ev.Interview.Status)) parts.Add($"Status: {ev.Interview.Status}");
        parts.Add(ev.IsActiveProfile
            ? "Drag to move, drag the bottom edge to resize, click to edit."
            : "Another profile of this caller — read-only here.");
        return string.Join("\n", parts);
    }

    // =====================================================================================
    // Drag / resize / click
    // =====================================================================================

    private void OnBlockMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement block || block.Tag is not CallerCalendarEvent ev) return;

        _dragBlock = block;
        _dragEvent = ev;
        _dragOrigin = e.GetPosition(Surface);
        _grabOffsetY = _dragOrigin.Y - Canvas.GetTop(block);
        _isResizing = e.GetPosition(block).Y >= block.ActualHeight - ResizeGripHeight;
        _dragPassedThreshold = false;

        block.CaptureMouse();
        Panel.SetZIndex(block, 100);
        e.Handled = true;
    }

    private void OnBlockMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragBlock == null || _dragEvent == null || e.LeftButton != MouseButtonState.Pressed)
        {
            // Not dragging — just keep the cursor honest about where the resize grip is.
            if (sender is FrameworkElement fe && fe.Tag is CallerCalendarEvent { IsActiveProfile: true })
                fe.Cursor = e.GetPosition(fe).Y >= fe.ActualHeight - ResizeGripHeight
                    ? Cursors.SizeNS : Cursors.Hand;
            return;
        }

        var p = e.GetPosition(Surface);
        if (!_dragPassedThreshold &&
            (Math.Abs(p.X - _dragOrigin.X) > DragThreshold || Math.Abs(p.Y - _dragOrigin.Y) > DragThreshold))
        {
            _dragPassedThreshold = true;
        }
        if (!_dragPassedThreshold) return;

        if (_isResizing)
        {
            var top = Canvas.GetTop(_dragBlock);
            var height = SnapPixels(p.Y - top);
            _dragBlock.Height = Math.Max(MinDuration.TotalHours * HourHeight, height);
        }
        else
        {
            var dayWidth = (Surface.Width - GutterWidth) / CallerCalendarViewModel.DaysInWeek;
            var dayIndex = Math.Clamp(
                (int)Math.Floor((p.X - GutterWidth) / dayWidth), 0, CallerCalendarViewModel.DaysInWeek - 1);

            var top = SnapPixels(p.Y - _grabOffsetY);
            top = Math.Clamp(top, 0, 24 * HourHeight - _dragBlock.ActualHeight);

            Canvas.SetLeft(_dragBlock, GutterWidth + dayIndex * dayWidth + 1);
            _dragBlock.Width = Math.Max(10, dayWidth - ColumnGap - 1);
            Canvas.SetTop(_dragBlock, top);
        }
    }

    private async void OnBlockMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragBlock == null || _dragEvent == null) return;

        var block = _dragBlock;
        var ev = _dragEvent;
        var wasDrag = _dragPassedThreshold;
        var wasResize = _isResizing;

        block.ReleaseMouseCapture();
        _dragBlock = null;
        _dragEvent = null;
        _dragPassedThreshold = false;
        e.Handled = true;

        if (Vm == null) return;

        if (!wasDrag)
        {
            OpenEditor(ev);
            return;
        }

        DateTime newStart;
        TimeSpan duration;

        if (wasResize)
        {
            newStart = ev.Start;
            duration = TimeSpan.FromHours(block.Height / HourHeight);
            if (duration < MinDuration) duration = MinDuration;
        }
        else
        {
            var dayWidth = (Surface.Width - GutterWidth) / CallerCalendarViewModel.DaysInWeek;
            var dayIndex = Math.Clamp(
                (int)Math.Round((Canvas.GetLeft(block) - GutterWidth - 1) / dayWidth),
                0, CallerCalendarViewModel.DaysInWeek - 1);
            var minutes = Canvas.GetTop(block) / HourHeight * 60;
            newStart = Vm.WeekStart.AddDays(dayIndex).AddMinutes(Math.Round(minutes));
            duration = ev.Duration;
        }

        // Nothing actually changed — a drag that ended where it started shouldn't cost a write.
        if (newStart == ev.Start && duration == ev.Duration)
        {
            Render();
            return;
        }

        // Redraw regardless of the outcome: on success the reload repositions everything, and on a
        // cancel or a failure the block has to go back where it came from.
        if (!await Vm.TryRescheduleAsync(ev, newStart, duration)) Render();
    }

    private async void OpenEditor(CallerCalendarEvent ev)
    {
        if (Vm == null || !ev.IsActiveProfile) return;

        var dlg = new InterviewEditDialog(ev)
        {
            Owner = Window.GetWindow(this)
        };
        if (dlg.ShowDialog() != true) return;

        await Vm.TryApplyEditAsync(
            ev, dlg.ScheduledDate, dlg.ScheduledTime, dlg.DurationMinutes,
            dlg.InterviewType, dlg.Status, dlg.Company, dlg.Role, dlg.Recruiter, dlg.MeetingLink);
    }

    /// <summary>Round a pixel offset to the nearest <see cref="SnapMinutes"/>.</summary>
    private static double SnapPixels(double y)
    {
        var step = HourHeight * SnapMinutes / 60.0;
        return Math.Round(y / step) * step;
    }

    private static Brush Themed(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}
