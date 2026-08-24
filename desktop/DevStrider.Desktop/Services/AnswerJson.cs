using System.Text;
using System.Text.Json;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Pulls the answers object out of whatever ChatGPT actually rendered.
///
/// <para>
/// The reply is read as the message's <c>innerText</c>, so it arrives with the code block's chrome
/// around it ("json", "Copy", "Edit"), sometimes a sentence before or after, and sometimes more than
/// one object — a worked example alongside the real one. Slicing from the first <c>{</c> to the last
/// <c>}</c> and parsing once, which is what this replaced, spans every object at once and fails on
/// all of them; the whole answer set was then discarded for a formatting detail.
/// </para>
///
/// <para>
/// So: find each balanced top-level object, try them longest first, and accept the first that holds
/// answers. A raw newline inside a string literal — which <c>innerText</c> can introduce when a long
/// value wraps — is repaired rather than treated as a failure, because it is a rendering artefact
/// and not something the model got wrong.
/// </para>
/// </summary>
public static class AnswerJson
{
    /// <summary>
    /// True when <paramref name="reply"/> contains a usable, non-empty answers object.
    /// <paramref name="json"/> receives it, already unwrapped from any <c>{"answers": …}</c>.
    /// </summary>
    public static bool TryExtract(string? reply, out string json)
    {
        json = "{}";
        if (string.IsNullOrWhiteSpace(reply)) return false;

        foreach (var candidate in BalancedObjects(reply).OrderByDescending(text => text.Length))
        {
            if (TryReadAnswers(candidate, out json)) return true;
            if (TryReadAnswers(RepairNewlinesInStrings(candidate), out json)) return true;
        }
        return false;
    }

    /// <summary>The answers object, or <c>{}</c> when the reply has none.</summary>
    public static string Extract(string? reply) => TryExtract(reply, out var json) ? json : "{}";

    private static bool TryReadAnswers(string candidate, out string json)
    {
        json = "{}";
        try
        {
            using var document = JsonDocument.Parse(candidate);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("answers", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
                root = wrapped;
            if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Any()) return false;
            json = JsonSerializer.Serialize(root.EnumerateObject().ToDictionary(
                property => Tidy(property.Name),
                property => Tidy(Scalar(property.Value))));
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
        _ => "",
    };

    /// <summary>
    /// Trims the ends and collapses runs of spaces and tabs.
    ///
    /// <para>
    /// Repairing a wrapped line leaves a space where the break was, and where the break sat next to
    /// existing whitespace it leaves two — so a question key came through as "Why  did you pick this
    /// concept?" and an email as "…@gmail.com ". A form reads a trailing space in an email field as
    /// no email at all, which is how a field the log said was filled came back required. Real line
    /// breaks arrive escaped and survive: only the raw ones were ever an artefact.
    /// </para>
    /// </summary>
    private static string Tidy(string value)
    {
        if (value.Length == 0) return value;
        var text = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var c in value)
        {
            var isSpace = c is ' ' or '\t';
            if (isSpace && lastWasSpace) continue;
            text.Append(isSpace ? ' ' : c);
            lastWasSpace = isSpace;
        }
        return text.ToString().Trim();
    }

    /// <summary>
    /// Every top-level <c>{ … }</c> span, counting braces only outside string literals so a brace
    /// inside an answer cannot end the object early.
    /// </summary>
    private static IEnumerable<string> BalancedObjects(string text)
    {
        var depth = 0;
        var start = -1;
        var inString = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{':
                    if (depth == 0) start = i;
                    depth++;
                    break;
                case '}':
                    if (depth == 0) break;
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        yield return text[start..(i + 1)];
                        start = -1;
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Replaces raw line breaks and tabs inside string literals with spaces. A long answer that
    /// wrapped in the rendered code block comes back with real newlines in it, which JSON forbids —
    /// a display artefact that should not cost the answer.
    /// </summary>
    private static string RepairNewlinesInStrings(string candidate)
    {
        var repaired = new StringBuilder(candidate.Length);
        var inString = false;
        var escaped = false;

        foreach (var c in candidate)
        {
            if (inString)
            {
                if (escaped) { escaped = false; repaired.Append(c); continue; }
                if (c == '\\') { escaped = true; repaired.Append(c); continue; }
                if (c == '"') { inString = false; repaired.Append(c); continue; }
                repaired.Append(c is '\n' or '\r' or '\t' ? ' ' : c);
                continue;
            }
            if (c == '"') inString = true;
            repaired.Append(c);
        }
        return repaired.ToString();
    }
}
