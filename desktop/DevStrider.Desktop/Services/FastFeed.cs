namespace DevStrider.Desktop.Services;

/// <summary>
/// C# port of <c>server/src/utils/parseFastFeed.js</c>. The Bid-Assistant Chrome extension
/// produces a one-line summary in the format
/// <c>"resumeId, Company, Role, Skill1, Skill2, …"</c> (optionally wrapped in <c>[...]</c>)
/// and either sends it as a standalone <c>fastFeedInput</c> field or appends it as the last
/// non-empty line of the GPT resume body. When parsed, the bid jumps from <c>draft</c> to
/// <c>applied</c> and the structured fields are populated.
/// </summary>
public static class FastFeed
{
    public record Parsed(string ResumeId, string Company, string Role, IReadOnlyList<string> PrimaryStacks);

    /// <summary>Parse a single fast-feed line. Returns null if fewer than 3 comma-segments.</summary>
    public static Parsed? ParseLine(string? line)
    {
        var t = (line ?? "").Trim();
        if (t.Length == 0) return null;
        var core = t;
        if (core.StartsWith('[') && core.EndsWith(']')) core = core[1..^1].Trim();

        var parts = core.Split(',')
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();
        if (parts.Length < 3) return null;

        return new Parsed(
            ResumeId: parts[0],
            Company: parts[1],
            Role: parts[2],
            PrimaryStacks: parts.Length > 3 ? parts[3..] : Array.Empty<string>());
    }

    /// <summary>
    /// Walk the GPT body bottom-up looking for the first line that parses as fast-feed.
    /// Returns the parsed metadata + the body with that line stripped. If nothing parses,
    /// <see cref="SplitResult.Parsed"/> is null and the body is returned trimmed.
    /// </summary>
    public static SplitResult SplitTrailing(string? gptText)
    {
        var full = gptText ?? "";
        var lines = full.Replace("\r\n", "\n").Split('\n');
        // Walk all lines bottom-up, returning at the first that parses. Matches
        // splitTrailingFastFeed in the JS server — non-parsing lines are skipped, not break.
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0) continue;
            var parsed = ParseLine(trimmed);
            if (parsed != null)
            {
                var resumePart = string.Join("\n", lines.Take(i)).TrimEnd();
                return new SplitResult(resumePart, trimmed, parsed);
            }
        }
        return new SplitResult(full.TrimEnd(), "", null);
    }

    public record SplitResult(string ResumePart, string FastFeedLine, Parsed? Parsed);
}
