using System.Diagnostics;
using System.Text;
using DevStrider.Desktop.Models;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Step-by-step trace of one automatic bid, written into the Activity feed.
///
/// <para>
/// An application crosses four subsystems — the job-site browser, ChatGPT, Word, and the form
/// filler — living in different view-models and firing through events, so when one stalls there was
/// no single place that showed how far it got. Every step now carries the same short run id and an
/// elapsed clock, which turns the Activity tab into a transcript that can be pasted whole.
/// </para>
///
/// <para>
/// Trace lines are <see cref="ActivityEntry.Silent"/>: they belong in the log, not in a tray balloon
/// every few seconds. Volume is the point, so <see cref="ActivityLogService"/> keeps a deep buffer.
/// </para>
/// </summary>
public sealed class BidTraceService
{
    private readonly ActivityLogService _log;
    private readonly Stopwatch _clock = new();
    private readonly object _gate = new();
    private string _runId = "";

    public BidTraceService(ActivityLogService log) => _log = log;

    /// <summary>The run in progress, or empty. Handy for a caller that wants to name it.</summary>
    public string RunId { get { lock (_gate) return _runId; } }

    /// <summary>
    /// Starts a run and returns its id. Deliberately short and human-sayable: it appears on every
    /// line, and its job is to let a reader tell two applications apart at a glance.
    /// </summary>
    public string Begin(Guid workItemId, string jobUrl)
    {
        lock (_gate)
        {
            _runId = workItemId.ToString("N")[..4].ToUpperInvariant();
            _clock.Restart();
        }
        Step("Run", "begin", jobUrl);
        return _runId;
    }

    public void Step(string stage, string step, string detail = "") =>
        Write(ActivityLevel.Info, stage, step, detail);

    public void Ok(string stage, string step, string detail = "") =>
        Write(ActivityLevel.Success, stage, step, detail);

    public void Warn(string stage, string step, string detail = "") =>
        Write(ActivityLevel.Warning, stage, step, detail);

    public void Fail(string stage, string step, string detail = "") =>
        Write(ActivityLevel.Error, stage, step, detail);

    public void End(string outcome, string detail = "")
    {
        Write(ActivityLevel.Info, "Run", "end: " + outcome, detail);
        lock (_gate) { _clock.Stop(); _runId = ""; }
    }

    /// <summary>
    /// Times a step and logs how long it took on completion — the number that says whether a stage
    /// hit its timeout or simply never returned.
    /// </summary>
    public IDisposable Timed(string stage, string step, string detail = "")
    {
        Step(stage, step + " …", detail);
        return new Timer(this, stage, step);
    }

    /// <summary>
    /// A value worth seeing in full but too long for one line — a prompt, a reply, a values map.
    /// Trimmed to something a person can still read after pasting the whole log into a message.
    /// </summary>
    public void Payload(string stage, string step, string? value, int limit = 600)
    {
        var text = (value ?? "").Replace('\r', ' ').Replace('\n', '⏎');
        var shown = text.Length <= limit ? text : text[..limit] + $"… (+{text.Length - limit} chars)";
        Write(ActivityLevel.Info, stage, step, $"[{text.Length} chars] {shown}");
    }

    private void Write(ActivityLevel level, string stage, string step, string detail)
    {
        string id;
        double seconds;
        lock (_gate)
        {
            id = _runId;
            seconds = _clock.Elapsed.TotalSeconds;
        }
        var prefix = id.Length == 0 ? "" : $"[{id} +{seconds,6:0.0}s] ";
        _log.Log(level, stage, prefix + step, detail ?? "", silent: true);
    }

    private sealed class Timer : IDisposable
    {
        private readonly BidTraceService _trace;
        private readonly string _stage;
        private readonly string _step;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public Timer(BidTraceService trace, string stage, string step)
        {
            _trace = trace;
            _stage = stage;
            _step = step;
        }

        public void Dispose() =>
            _trace.Step(_stage, _step + " done", $"took {_clock.Elapsed.TotalSeconds:0.0}s");
    }
}

/// <summary>Renders the whole feed as plain text, for pasting into a bug report.</summary>
public static class ActivityTranscript
{
    public static string Render(IEnumerable<ActivityEntry> entries)
    {
        var text = new StringBuilder();
        // Oldest first: a transcript reads forwards, even though the grid shows newest on top.
        foreach (var entry in entries.Reverse())
        {
            text.Append(entry.At.ToString("HH:mm:ss.fff")).Append("  ")
                .Append(entry.Level.ToString().PadRight(7)).Append("  ")
                .Append(entry.Source.PadRight(12)).Append("  ")
                .Append(entry.Title);
            if (entry.Detail.Length > 0) text.Append("  |  ").Append(entry.Detail);
            text.AppendLine();
        }
        return text.ToString();
    }
}
