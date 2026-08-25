using System.Text.Json;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Raised when a step's input or output is not the shape the next step needs.
///
/// <para>
/// This is deliberately not the same thing as a step failing. A failure means the work did not
/// happen — the page would not load, the macro timed out. A format violation means the work
/// happened and produced something unusable, which is more dangerous, because unusable data flows
/// on and gets acted upon. A job description that is really the application form reads as 2,857
/// perfectly valid characters; nothing downstream can tell it is wrong, and the resume is tailored
/// to a cookie banner. The rule for these is to stop this link and take the next one.
/// </para>
/// </summary>
public sealed class FlowFormatException(string step, string direction, string expected, string actual)
    : Exception($"{step} {direction}: expected {expected}; got {actual}")
{
    public string Step { get; } = step;

    /// <summary>"input" or "output".</summary>
    public string Direction { get; } = direction;

    public string Expected { get; } = expected;
    public string Actual { get; } = actual;

    /// <summary>What gets recorded against the link the run gave up on.</summary>
    public string Summary => $"Gave up on format at {Step} ({Direction}): expected {Expected}; got {Actual}.";
}

/// <summary>
/// The shape rules between steps of one automatic bid.
///
/// <para>
/// Each step declares what it needs on the way in and what it promises on the way out, and both are
/// checked rather than assumed. A satisfied check is traced too, not only a broken one: the point is
/// that reading the trace tells you which contracts held, so a step that was never reached is
/// visible as an absence rather than looking the same as a step that passed.
/// </para>
/// </summary>
public sealed class FlowContract(BidTraceService? trace)
{
    /// <summary>Checks what a step was handed before it runs.</summary>
    public void Input(string step, bool ok, string expected, string actual) =>
        Check(step, "input", ok, expected, actual);

    /// <summary>Checks what a step produced before anything downstream sees it.</summary>
    public void Output(string step, bool ok, string expected, string actual) =>
        Check(step, "output", ok, expected, actual);

    /// <summary>
    /// Parses a script result and checks its top-level kind in one move, so a step that returned
    /// nothing, returned an error string, or returned an object where an array belongs is caught at
    /// the boundary it crossed instead of throwing somewhere further down.
    /// </summary>
    public JsonDocument Json(string step, string? json, JsonValueKind kind, string expected)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw Reject(step, "output", expected, "an empty result");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw Reject(step, "output", expected, $"unparseable JSON ({ex.Message}): {Clip(json)}");
        }

        if (document.RootElement.ValueKind != kind)
        {
            var actual = $"{document.RootElement.ValueKind}: {Clip(json)}";
            document.Dispose();
            throw Reject(step, "output", expected, actual);
        }

        trace?.Ok("Contract", $"{step} output ok", $"{kind}, {json.Length} chars");
        return document;
    }

    private void Check(string step, string direction, bool ok, string expected, string actual)
    {
        if (ok)
        {
            trace?.Ok("Contract", $"{step} {direction} ok", actual);
            return;
        }
        throw Reject(step, direction, expected, actual);
    }

    private FlowFormatException Reject(string step, string direction, string expected, string actual)
    {
        trace?.Fail("Contract", $"{step} {direction} rejected", $"expected {expected}; got {actual}");
        return new FlowFormatException(step, direction, expected, actual);
    }

    private static string Clip(string value) =>
        value.Length <= 160 ? value : value[..160] + "...";
}
