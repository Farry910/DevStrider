using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using DevStrider.Desktop.Models;
using DevStrider.Desktop.Services;

namespace DevStrider.Desktop.ViewModels;

public partial class ActivityViewModel : ViewModelBase
{
    private readonly ActivityLogService _log;

    public ObservableCollection<ActivityEntry> Entries => _log.Entries;

    public ActivityViewModel(ActivityLogService log)
    {
        _log = log;
    }

    [RelayCommand]
    public void Clear() => _log.Clear();

    /// <summary>
    /// The whole feed as plain text, oldest first. Selecting a few thousand grid rows by hand to
    /// report a bug is not a thing anyone should have to do.
    /// </summary>
    [RelayCommand]
    public void CopyAll() => Copy(Entries, "Copied the whole log");

    /// <summary>
    /// Just the most recent application. Runs are tagged with a short id, so this walks back from
    /// the newest entry to that run's "begin" line and copies only what belongs to it.
    /// </summary>
    [RelayCommand]
    public void CopyLastRun()
    {
        var runId = Entries.Select(RunIdOf).FirstOrDefault(id => id.Length > 0) ?? "";
        if (runId.Length == 0)
        {
            StatusMessage = "No traced application in the log yet — run one from the Job Browser.";
            return;
        }
        var run = Entries.Where(entry => RunIdOf(entry) == runId).ToList();
        Copy(run, $"Copied run {runId}");
    }

    private void Copy(IEnumerable<ActivityEntry> entries, string done)
    {
        var text = ActivityTranscript.Render(entries);
        if (text.Length == 0) { StatusMessage = "Nothing to copy."; return; }
        try
        {
            Clipboard.SetText(text);
            StatusMessage = $"{done} — {text.Length:N0} characters. Paste it straight into a bug report.";
        }
        catch (Exception ex) { StatusMessage = "Could not copy: " + ex.Message; }
    }

    /// <summary>Pulls <c>ABCD</c> out of a title that starts <c>[ABCD +12.3s] …</c>.</summary>
    private static string RunIdOf(ActivityEntry entry)
    {
        var title = entry.Title;
        if (title.Length < 3 || title[0] != '[') return "";
        var space = title.IndexOf(' ');
        return space > 1 ? title[1..space] : "";
    }
}
