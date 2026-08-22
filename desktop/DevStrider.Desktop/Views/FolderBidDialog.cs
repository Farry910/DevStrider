using System.Windows;
using System.Windows.Controls;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;

namespace DevStrider.Desktop.Views;

/// <summary>
/// The folder back door: pick a profile, pick the day, point at the directory the Word macro wrote
/// its resume folders into, and every folder named like a fast-feed line becomes a bid.
///
/// <para>
/// Built in C# like the rest of <see cref="Dialogs"/>. Scan is a separate step from Import on
/// purpose — this writes a day's worth of rows in one click, and seeing the count first is what
/// turns that from a leap into a decision. The warning about missing URLs and job descriptions is
/// on the face of the dialog rather than in a tooltip, because it is the one thing about these
/// rows that will surprise someone later.
/// </para>
/// </summary>
public sealed class FolderBidDialog : Window
{
    private readonly ComboBox _profile = new() { DisplayMemberPath = nameof(Profile.Name) };
    private readonly TextBox _folder = new() { Tag = @"C:\Users\you\Documents\Resumes", IsReadOnly = true };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 10, 0, 0) };
    private readonly ListBox _preview = new() { Height = 150, Margin = new Thickness(0, 8, 0, 0), FontFamily = new FontFamily("Consolas"), FontSize = 11 };
    private readonly Button _import;

    private readonly FolderBidImport _importer;
    private FolderScanResult? _scan;

    /// <summary>How many bids were recorded. Zero unless Import was pressed and succeeded.</summary>
    public int Recorded { get; private set; }

    public FolderBidDialog(FolderBidImport importer, IEnumerable<Profile> profiles, ObjectId activeProfileId)
    {
        _importer = importer;

        Title = "Record bids from resume folders";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        foreach (var p in profiles) _profile.Items.Add(p);
        _profile.SelectedItem = _profile.Items.Cast<Profile>().FirstOrDefault(p => p.Id == activeProfileId)
                                ?? _profile.Items.Cast<Profile>().FirstOrDefault();

        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 7; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var blurb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 12),
            Text = "Each sub-folder named 'UID, Company, Role, Stack1, …' is recorded as a bid, "
                 + "timed by when the folder was created — that is when the macro wrote it, so it "
                 + "is when the bid was made. Check the dates below look right: folders that were "
                 + "copied or restored carry the date of the copy.\n\n"
                 + "Folder names carry nothing else, so these rows have no job URL and no job "
                 + "description, and take no part in duplicate-URL checks. Re-running on the same "
                 + "folder updates the same rows rather than duplicating them.",
        };
        Grid.SetColumnSpan(blurb, 2); Grid.SetRow(blurb, 0); grid.Children.Add(blurb);

        void AddRow(int row, string label, FrameworkElement field)
        {
            var lbl = new TextBlock { Text = label, Margin = new Thickness(0, 6, 12, 6), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0); grid.Children.Add(lbl);
            Grid.SetRow(field, row); Grid.SetColumn(field, 1);
            field.Margin = new Thickness(0, 6, 0, 6);
            grid.Children.Add(field);
        }

        AddRow(1, "Profile", _profile);

        var browse = new Button { Content = "Browse…", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        var folderRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse);
        folderRow.Children.Add(_folder);
        AddRow(2, "Folder", folderRow);

        var scanButton = new Button { Content = "Scan folder", MinWidth = 110 };
        Grid.SetRow(scanButton, 3); Grid.SetColumn(scanButton, 1);
        scanButton.HorizontalAlignment = HorizontalAlignment.Left;
        scanButton.Margin = new Thickness(0, 6, 0, 0);
        grid.Children.Add(scanButton);

        Grid.SetColumnSpan(_status, 2); Grid.SetRow(_status, 4); grid.Children.Add(_status);
        Grid.SetColumnSpan(_preview, 2); Grid.SetRow(_preview, 5); grid.Children.Add(_preview);
        _preview.Visibility = Visibility.Collapsed;

        _import = new Button { Content = "Record bids", IsDefault = true, MinWidth = 110, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        var cancel = new Button { Content = "Close", IsCancel = true, MinWidth = 88 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        buttons.Children.Add(_import); buttons.Children.Add(cancel);
        Grid.SetColumnSpan(buttons, 2); Grid.SetRow(buttons, 6); grid.Children.Add(buttons);

        browse.Click += (_, _) => Browse();
        scanButton.Click += (_, _) => DoScan();
        _import.Click += async (_, _) => await DoImportAsync();

        Content = grid;
    }

    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Folder containing the resume folders",
            Multiselect = false,
        };
        if (dlg.ShowDialog() == true)
        {
            _folder.Text = dlg.FolderName;
            DoScan();
        }
    }

    private void DoScan()
    {
        _scan = _importer.Scan(_folder.Text);
        _status.Text = _scan.Message;

        _preview.Items.Clear();
        foreach (var c in _scan.Candidates)
        {
            // The timestamp leads: it is the one value here the user cannot check by reading the
            // folder name, and the one most likely to be wrong.
            _preview.Items.Add(c.Ok
                ? $"✓  {c.CreatedAt:yyyy-MM-dd HH:mm}   {c.Parsed!.Company} · {c.Parsed.Role}   [{c.Parsed.ResumeId}]"
                : $"—  {c.CreatedAt:yyyy-MM-dd HH:mm}   {c.FolderName}");
        }
        _preview.Visibility = _scan.Candidates.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _import.IsEnabled = _scan.Recognised > 0;
    }

    private async Task DoImportAsync()
    {
        if (_scan == null || _scan.Recognised == 0) return;
        if (_profile.SelectedItem is not Profile profile)
        {
            _status.Text = "Pick a profile first.";
            return;
        }

        var when = _scan.DateRange is { } r
            ? (r.from.Date == r.to.Date
                ? $", all on {r.from:yyyy-MM-dd}"
                : $", dated {r.from:yyyy-MM-dd} to {r.to:yyyy-MM-dd}")
            : "";

        var ok = ConfirmDialog.Ask(this, "Record these bids?",
            $"{_scan.Recognised} bid{(_scan.Recognised == 1 ? "" : "s")} will be recorded under "
            + $"'{profile.Name}'{when}, timed by when each folder was created."
            + "\n\nThey will have no job URL and no job description.",
            okText: "Record", danger: false);
        if (!ok) return;

        _import.IsEnabled = false;
        try
        {
            Recorded = await _importer.ImportAsync(_scan, profile.Id);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = $"Couldn't record those: {SharedDbCredentials.Redact(ex.Message)}";
            _import.IsEnabled = true;
        }
    }
}
