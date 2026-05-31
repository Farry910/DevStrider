namespace DevStrider.Desktop.Services;

/// <summary>
/// Strict URL normalization for dedup: lowercase, trim trailing slash, keep query + hash.
/// Mirrors the strict rule the web app moved to — different queries = different jobs.
/// </summary>
public static class UrlNorm
{
    public static string Normalize(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return "";
        try
        {
            var href = (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                ? s
                : "https://" + s;
            var u = new Uri(href);
            var path = string.IsNullOrEmpty(u.AbsolutePath) ? "/" : u.AbsolutePath;
            if (path.Length > 1 && path.EndsWith("/")) path = path[..^1];
            var rebuilt = $"{u.Scheme}://{u.Host}{(u.IsDefaultPort ? "" : ":" + u.Port)}{path}{u.Query}{u.Fragment}";
            return rebuilt.ToLowerInvariant();
        }
        catch
        {
            return s.ToLowerInvariant().TrimEnd('/');
        }
    }
}
