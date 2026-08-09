using System.Text.RegularExpressions;

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

            // A fast-feed line is bare by contract, so a "[Label]: …" line is never one — and
            // plenty of them parse anyway ("[Skills]: React, TypeScript, Node.js" has three
            // comma-segments). Consuming one would strip a resume section and fill the bid with
            // nonsense. [FolderName] is the single exception: its value *is* the fast-feed data
            // when the prompt doesn't also emit a bare duplicate, so read it without removing
            // the line the macro needs.
            var labelled = LabelledLine.Match(trimmed);
            if (labelled.Success)
            {
                if (labelled.Groups[1].Value.Trim().Equals("FolderName", StringComparison.OrdinalIgnoreCase))
                {
                    var inlineValue = labelled.Groups[2].Value.Trim();
                    var inlineParsed = ParseLine(inlineValue);
                    if (inlineParsed != null)
                        return new SplitResult(full.TrimEnd(), inlineValue, inlineParsed);
                }
                continue;
            }

            var parsed = ParseLine(trimmed);
            if (parsed != null)
            {
                // Keep the line when it is the value under a bare "[FolderName]:" label. A prompt
                // that puts every label on its own line makes the folder name and the fast-feed
                // line the same text, so stripping it hands the macro a label with nothing after
                // it and every resume lands in a folder called "Resume". The bid still gets its
                // fields either way -- only the text passed to the macro differs, which is why
                // this deliberately diverges from the JS port.
                var take = IsFolderNameValue(lines, i) ? i + 1 : i;
                var resumePart = string.Join("\n", lines.Take(take)).TrimEnd();
                return new SplitResult(resumePart, trimmed, parsed);
            }
        }
        return new SplitResult(full.TrimEnd(), "", null);
    }

    /// <summary>
    /// A section header with its value on the same line: <c>[Label]: value</c>. Deliberately
    /// requires the colon, so a bracket-wrapped fast-feed line (<c>[id, Company, Role]</c>)
    /// does not match.
    /// </summary>
    private static readonly Regex LabelledLine = new(@"^\[([^\]]+)\]\s*:\s*(.*)$", RegexOptions.Compiled);

    /// <summary>A <c>[FolderName]:</c> label alone on its line, with its value beneath it.</summary>
    private static readonly Regex BareFolderNameLabel =
        new(@"^\[\s*foldername\s*\]\s*:?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// True when the line at <paramref name="index"/> sits directly under a bare
    /// <c>[FolderName]:</c> label — i.e. it is that section's content, not a trailing
    /// fast-feed line that happens to look the same.
    /// </summary>
    private static bool IsFolderNameValue(string[] lines, int index)
    {
        for (int j = index - 1; j >= 0; j--)
        {
            var prev = lines[j].Trim();
            if (prev.Length == 0) continue;
            return BareFolderNameLabel.IsMatch(prev);
        }
        return false;
    }

    public record SplitResult(string ResumePart, string FastFeedLine, Parsed? Parsed);
}
