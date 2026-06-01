using System.Collections.ObjectModel;
using System.Windows;
using DevStrider.Desktop.Models;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Single in-memory feed of "things the app just did." Both the Activity tab and the tray
/// balloon subscribe here. Capped at <see cref="MaxEntries"/>; oldest entries fall off as
/// new ones arrive. All collection mutations are marshaled to the WPF dispatcher so view
/// bindings stay safe.
/// </summary>
public sealed class ActivityLogService
{
    private const int MaxEntries = 300;

    public ObservableCollection<ActivityEntry> Entries { get; } = new();

    /// <summary>Fires after each entry is appended. The App subscribes to fan out to the tray.</summary>
    public event Action<ActivityEntry>? OnEntry;

    public void Log(ActivityLevel level, string source, string title, string detail = "", bool silent = false)
    {
        var entry = new ActivityEntry
        {
            At = DateTime.Now,
            Level = level,
            Source = source,
            Title = title,
            Detail = detail ?? "",
            Silent = silent
        };
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            Append(entry);
        else
            dispatcher.BeginInvoke(new Action(() => Append(entry)));
    }

    private void Append(ActivityEntry entry)
    {
        // Newest first — the Activity tab renders top-to-bottom and the user wants the most
        // recent event in view.
        Entries.Insert(0, entry);
        while (Entries.Count > MaxEntries) Entries.RemoveAt(Entries.Count - 1);
        try { OnEntry?.Invoke(entry); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ActivityLog] subscriber threw: " + ex.Message); }
    }

    public void Clear()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) Entries.Clear();
        else dispatcher.BeginInvoke(new Action(() => Entries.Clear()));
    }

    public void Info(string source, string title, string detail = "", bool silent = false) =>
        Log(ActivityLevel.Info, source, title, detail, silent);
    public void Success(string source, string title, string detail = "", bool silent = false) =>
        Log(ActivityLevel.Success, source, title, detail, silent);
    public void Warning(string source, string title, string detail = "", bool silent = false) =>
        Log(ActivityLevel.Warning, source, title, detail, silent);
    public void Error(string source, string title, string detail = "", bool silent = false) =>
        Log(ActivityLevel.Error, source, title, detail, silent);
}
