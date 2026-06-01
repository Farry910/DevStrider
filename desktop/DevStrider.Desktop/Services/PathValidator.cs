using System.Diagnostics;
using System.IO;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Port of <c>BidAssistantApp/PathValidator.cs</c>. Sanity-check the Word document path
/// before sending it to <see cref="KeyboardHelper.OpenWordDocument"/> — guards against path
/// traversal, missing files, wrong extension, oversized files.
/// </summary>
internal static class PathValidator
{
    private static readonly string[] WordExtensions = { ".doc", ".docx", ".docm" };
    private const long MaxWordFileSizeBytes = 100L * 1024 * 1024;

    public static (bool valid, string? error) ValidateWordPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (false, "Path is required");
        path = path.Trim();

        try { path = Path.GetFullPath(path); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PathValidator] Invalid path format: {ex.Message}");
            return (false, $"Invalid path format: {ex.Message}");
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext) || !WordExtensions.Contains(ext))
            return (false, $"Path must be a Word document (.doc, .docx, .docm). Got: {(string.IsNullOrEmpty(ext) ? "(no extension)" : ext)}");

        if (!File.Exists(path))
            return (false, $"File not found: {path}");

        try
        {
            var fi = new FileInfo(path);
            if (fi.Length > MaxWordFileSizeBytes)
            {
                var mb = fi.Length / (1024 * 1024);
                return (false, $"File is too large ({mb}MB). Maximum 100MB.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PathValidator] Could not check size: {ex.Message}");
        }
        return (true, null);
    }
}
