namespace BidAssistantApp;

/// <summary>
/// Centralized logging utility with console and file output
/// </summary>
static class Logger
{
    private static readonly object _lock = new();
    private static readonly object _cleanupLock = new();
    private static string? _logDirectory;

    static Logger()
    {
        try
        {
            _logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BidAssistant", "logs"
            );
            Directory.CreateDirectory(_logDirectory);
        }
        catch
        {
            _logDirectory = null; // Disable file logging if setup fails
        }
    }

    /// <summary>
    /// Log an informational message
    /// </summary>
    public static void Info(string message) => Log("INFO", message);

    /// <summary>
    /// Log an error message
    /// </summary>
    public static void Error(string message) => Log("ERROR", message);

    /// <summary>
    /// Log an error with exception details
    /// </summary>
    public static void Error(string message, Exception ex) =>
        Log("ERROR", $"{message}\n{ex.Message}\n{ex.StackTrace}");

    /// <summary>
    /// Log a warning message
    /// </summary>
    public static void Warning(string message) => Log("WARN", message);

    private static void Log(string level, string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var logMessage = $"[{timestamp}] [{level}] {message}";

        lock (_lock)
        {
            // Console output
            Console.WriteLine(logMessage);

            // File output
            if (_logDirectory != null)
            {
                try
                {
                    var logFile = Path.Combine(_logDirectory, $"{DateTime.Now:yyyy-MM-dd}.log");
                    File.AppendAllText(logFile, logMessage + Environment.NewLine);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"Log file write failed: {ex.Message}");
                }
            }
        }

        // Cleanup outside main lock to avoid blocking logging during file deletion
        CleanupOldLogs();
    }

    private static DateTime _lastCleanup = DateTime.MinValue;

    private static void CleanupOldLogs()
    {
        // Quick check without lock — cleanup at most once per day
        if ((DateTime.Now - _lastCleanup).TotalDays < 1)
            return;

        if (!Monitor.TryEnter(_cleanupLock))
            return; // Another thread is already cleaning up

        try
        {
            // Double-check after acquiring lock
            if ((DateTime.Now - _lastCleanup).TotalDays < 1)
                return;

            _lastCleanup = DateTime.Now;

            if (_logDirectory == null) return;

            var cutoffDate = DateTime.Now.AddDays(-7);
            var files = Directory.GetFiles(_logDirectory, "*.log");

            foreach (var file in files)
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.LastWriteTime < cutoffDate)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Log cleanup failed: {ex.Message}");
        }
        finally
        {
            Monitor.Exit(_cleanupLock);
        }
    }
}
