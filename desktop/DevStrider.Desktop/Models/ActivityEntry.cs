namespace DevStrider.Desktop.Models;

public enum ActivityLevel { Info, Success, Warning, Error }

/// <summary>
/// One line in the Activity feed. <see cref="Silent"/> suppresses the tray balloon for that
/// entry (still recorded in the log) — used for high-frequency events like paste-submit
/// that would otherwise spam notifications.
/// </summary>
public sealed class ActivityEntry
{
    public DateTime At { get; init; } = DateTime.Now;
    public ActivityLevel Level { get; init; }
    public string Source { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool Silent { get; init; }
}
