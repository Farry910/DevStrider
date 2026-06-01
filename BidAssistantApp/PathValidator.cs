namespace BidAssistantApp;

/// <summary>
/// Validates Word document paths for security and correctness
/// </summary>
static class PathValidator
{
    private static readonly string[] WordExtensions = [".doc", ".docx", ".docm"];

    /// <summary>
    /// Validates a Word document path
    /// </summary>
    /// <param name="path">Path to validate</param>
    /// <returns>Tuple of (isValid, errorMessage)</returns>
    public static (bool valid, string? error) ValidateWordPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (false, "Path is required");

        path = path.Trim();
        if (path.Length == 0)
            return (false, "Path is required");

        // Resolve to absolute path and check for path traversal
        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            Logger.Warning($"Invalid path format: {ex.Message}");
            return (false, $"Invalid path format: {ex.Message}");
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext) || !WordExtensions.Contains(ext))
            return (false, $"Path must be a Word document (.doc, .docx, .docm). Got: {(string.IsNullOrEmpty(ext) ? "(no extension)" : ext)}");

        if (!File.Exists(path))
            return (false, $"File not found: {path}");

        // Check file size
        try
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length > Constants.MAX_WORD_FILE_SIZE_BYTES)
            {
                var sizeMB = fileInfo.Length / (1024 * 1024);
                var maxMB = Constants.MAX_WORD_FILE_SIZE_BYTES / (1024 * 1024);
                return (false, $"File is too large ({sizeMB}MB). Maximum size is {maxMB}MB.");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"Could not check file size: {ex.Message}");
            // Continue anyway - size check is optional
        }

        return (true, null);
    }
}
