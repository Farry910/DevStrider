using System.Text.RegularExpressions;
using DevStrider.Desktop.Models;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Port of <c>client/src/utils/composeResume.ts</c>: build a paste-ready resume by combining the
/// profile header with a per-bid body that contains placeholder markers from GPT. The viewer
/// gets back both plain text (for copy) and a list of lines to render in bold/larger weight
/// (the synthesized experience headers).
/// </summary>
public static class ComposeResume
{
    public record Composed(string Text, IReadOnlyList<string> BoldLines);

    public static Composed? Build(UserProfile? profile, string body)
    {
        var trimmedBody = (body ?? "").Trim();
        if (profile == null && trimmedBody.Length == 0) return null;

        var experiences = profile?.Experiences ?? new List<Experience>();
        var processed = trimmedBody.Length > 0
            ? ApplyPlaceholders(trimmedBody, experiences, profile?.DisplayName ?? "")
            : (new List<string>(), new HashSet<string>());

        var bodyText = string.Join("\n", processed.Lines).Trim();
        // collapse 3+ blank lines (stripped placeholder leftovers).
        bodyText = Regex.Replace(bodyText, "\n{3,}", "\n\n");

        var lines = new List<string>();
        if (profile != null)
        {
            var name = string.IsNullOrEmpty(profile.DisplayName) ? "" : profile.DisplayName.ToUpperInvariant();
            if (name.Length > 0) lines.Add(name);
            if (!string.IsNullOrEmpty(profile.Headline)) lines.Add(profile.Headline);

            var contact = new List<string>();
            if (!string.IsNullOrEmpty(profile.Location)) contact.Add(profile.Location);
            if (!string.IsNullOrEmpty(profile.PersonalEmail)) contact.Add(profile.PersonalEmail);
            if (!string.IsNullOrEmpty(profile.Phone)) contact.Add(profile.Phone);
            if (contact.Count > 0) lines.Add(string.Join(" | ", contact));
            if (!string.IsNullOrEmpty(profile.LinkedinUrl)) lines.Add(profile.LinkedinUrl);
        }

        if (bodyText.Length > 0)
        {
            if (lines.Count > 0) lines.Add("");
            lines.Add(bodyText);
        }

        if (profile?.Education.Count > 0)
        {
            lines.Add("");
            lines.Add("Education");
            foreach (var e in profile.Education)
            {
                var range =
                    e.StartYear != null && e.EndYear != null ? $"{e.StartYear} - {e.EndYear}" :
                    e.StartYear != null ? $"{e.StartYear} -" :
                    e.EndYear != null ? $"- {e.EndYear}" : "";
                var parts = new[] { e.Degree, e.School, e.Location, range }
                    .Where(s => !string.IsNullOrWhiteSpace(s));
                if (parts.Any()) lines.Add(string.Join(" · ", parts));
            }
        }

        if (profile?.Certifications.Count > 0)
        {
            lines.Add("");
            lines.Add("Certifications");
            foreach (var c in profile.Certifications)
            {
                var parts = new[] { c.Name, c.Issuer, c.Year?.ToString() ?? "" }
                    .Where(s => !string.IsNullOrWhiteSpace(s));
                if (parts.Any()) lines.Add(string.Join(" · ", parts));
            }
        }

        var text = string.Join("\n", lines).Trim();
        if (text.Length == 0) return null;
        return new Composed(text, processed.Bold.ToList());
    }

    private static (List<string> Lines, HashSet<string> Bold) ApplyPlaceholders(
        string body, IReadOnlyList<Experience> experiences, string profileName)
    {
        var inLines = body.Replace("\r\n", "\n").Split('\n');
        var roleByIndex = new Dictionary<int, string>();
        var strip = new HashSet<int>();

        // Pass 1: capture [Subtitle N] roles, mark both lines stripped.
        for (int i = 0; i < inLines.Length; i++)
        {
            var t = inLines[i].Trim();
            var m = Regex.Match(t, @"^\[subtitle\s+(\d+)\]:?\s*$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            strip.Add(i);
            for (int j = i + 1; j < inLines.Length; j++)
            {
                if (inLines[j].Trim().Length == 0) continue;
                roleByIndex[int.Parse(m.Groups[1].Value)] = inLines[j].Trim();
                strip.Add(j);
                break;
            }
        }

        // Pass 2: drop a leading line that duplicates the profile display name (legacy template).
        if (!string.IsNullOrEmpty(profileName))
        {
            var pname = profileName.Trim().ToLowerInvariant();
            for (int i = 0; i < inLines.Length; i++)
            {
                var t = inLines[i].Trim();
                if (t.Length == 0) continue;
                if (t.ToLowerInvariant() == pname) strip.Add(i);
                break;
            }
        }

        var outLines = new List<string>();
        var bold = new HashSet<string>();
        for (int i = 0; i < inLines.Length; i++)
        {
            if (strip.Contains(i)) continue;
            var raw = inLines[i];
            var t = raw.Trim();
            if (Regex.IsMatch(t, @"^\[title\]:?\s*$", RegexOptions.IgnoreCase)) continue;
            if (Regex.IsMatch(t, @"^\[foldername\]:?\s*$", RegexOptions.IgnoreCase)) continue;
            if (t.Equals("edit", StringComparison.OrdinalIgnoreCase)) continue;

            var m = Regex.Match(t, @"^\[experience\s+(\d+)\]:?\s*$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var n = int.Parse(m.Groups[1].Value);
                var role = roleByIndex.GetValueOrDefault(n, "");
                var exp = n - 1 >= 0 && n - 1 < experiences.Count ? experiences[n - 1] : null;
                var range =
                    exp?.StartYear != null && exp?.EndYear != null ? $"{exp.StartYear} - {exp.EndYear}" :
                    exp?.StartYear != null ? $"{exp.StartYear} -" :
                    exp?.EndYear != null ? $"- {exp.EndYear}" : "";
                var parts = new[] { role, exp?.Company ?? "", exp?.Location ?? "", range }
                    .Select(s => (s ?? "").Trim())
                    .Where(s => s.Length > 0);
                var header = string.Join(" · ", parts);
                if (header.Length > 0)
                {
                    outLines.Add(header);
                    bold.Add(header);
                }
                continue;
            }
            outLines.Add(raw);
        }
        return (outLines, bold);
    }
}
