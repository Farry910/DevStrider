using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using DevStrider.Desktop.Models;

namespace DevStrider.Desktop.Views;

/// <summary>
/// Three small modal dialogs used off the Bid board. Built in C# (no separate .xaml) so we
/// don't litter the project with one-control views.
/// </summary>

/// <summary>Single-line text prompt for the fast-feed string.</summary>
public sealed class FastFeedDialog : Window
{
    private readonly TextBox _input = new()
    {
        FontFamily = new FontFamily("Consolas"),
        Padding = new Thickness(8, 6, 8, 6),
        Margin = new Thickness(0, 0, 0, 12),
        Tag = "UID, Company, Role, Stack1, …",
    };
    public string? Subject { get; set; }
    public string Line => _input.Text?.Trim() ?? "";

    public FastFeedDialog()
    {
        Title = "Apply fast-feed line";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        BuildLayout();
        Loaded += (_, _) => _input.Focus();
    }

    private void BuildLayout()
    {
        var subjectLabel = new TextBlock { Margin = new Thickness(0, 0, 0, 4), Foreground = Brushes.Gray };
        subjectLabel.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Subject)) { Source = this });
        var hint = new TextBlock
        {
            Text = "Paste a line in the form: UID, Company, Role, Stack1, Stack2, …",
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 8),
            FontSize = 12
        };
        var okBtn = new Button { Content = "Apply", IsDefault = true, MinWidth = 88, Margin = new Thickness(0, 0, 8, 0) };
        var cancelBtn = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88 };
        okBtn.Click += (_, _) => { DialogResult = true; Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(okBtn);
        buttons.Children.Add(cancelBtn);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(subjectLabel);
        panel.Children.Add(hint);
        panel.Children.Add(_input);
        panel.Children.Add(buttons);
        Content = panel;

        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { DialogResult = true; Close(); }
        };
    }
}

/// <summary>Read-only text viewer (used for JD popup).</summary>
public sealed class TextViewerDialog : Window
{
    public new string Content
    {
        get => _body.Text;
        set => _body.Text = value;
    }
    private readonly TextBox _body = new()
    {
        AcceptsReturn = true,
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 13,
        Padding = new Thickness(10, 8, 10, 8),
    };

    public TextViewerDialog()
    {
        Width = 720;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, MinWidth = 88, HorizontalAlignment = HorizontalAlignment.Right };
        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_body, 0);
        Grid.SetRow(close, 1);
        close.Margin = new Thickness(0, 12, 0, 0);
        grid.Children.Add(_body);
        grid.Children.Add(close);
        base.Content = grid;
    }
}

/// <summary>Form to schedule an interview off a bid.</summary>
public sealed class ScheduleInterviewDialog : Window
{
    private readonly DatePicker _date = new() { SelectedDate = DateTime.Today.AddDays(1) };
    private readonly TextBox _time = new() { Tag = "HH:mm", Text = "10:00" };
    private readonly ComboBox _type = new();
    private readonly TextBox _recruiter = new() { Tag = "Recruiter name (optional)" };
    private readonly TextBox _meetingLink = new() { Tag = "Meeting link (optional)" };

    public string Company { get; set; } = "";
    public string Role { get; set; } = "";
    public string ResumeIdLabel { get; set; } = "";

    public DateTime? ScheduledDate => _date.SelectedDate;
    public string ScheduledTime => _time.Text?.Trim() ?? "";
    public string InterviewType => (_type.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? InterviewTypes.Interview;
    public string Recruiter => _recruiter.Text?.Trim() ?? "";
    public string MeetingLink => _meetingLink.Text?.Trim() ?? "";

    public ScheduleInterviewDialog()
    {
        Title = "Schedule interview";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        foreach (var t in new[] {
                     InterviewTypes.PhoneScreening,
                     InterviewTypes.Interview,
                     InterviewTypes.Assessment,
                     InterviewTypes.Offer })
            _type.Items.Add(new ComboBoxItem { Content = t });
        _type.SelectedIndex = 1; // default 'interview'

        var header = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        header.Inlines.Add(new Run("Scheduling from bid:") { Foreground = Brushes.Gray });
        Loaded += (_, _) => header.Inlines.Add(new Run($"  {Company} · {Role}") { FontWeight = FontWeights.SemiBold });

        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 7; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

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
        AddRow(1, "Date",         _date);
        AddRow(2, "Time",         _time);
        AddRow(3, "Type",         _type);
        AddRow(4, "Recruiter",    _recruiter);
        AddRow(5, "Meeting link", _meetingLink);

        var ok = new Button { Content = "Schedule", IsDefault = true, MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88 };
        ok.Click += (_, _) => { DialogResult = true; Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        Grid.SetColumnSpan(buttons, 2); Grid.SetRow(buttons, 6); grid.Children.Add(buttons);

        Content = grid;
    }
}
