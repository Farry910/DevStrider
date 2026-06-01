namespace BidAssistantApp;

/// <summary>
/// Application-wide constants
/// </summary>
static class Constants
{
    // Keyboard timing constants (in milliseconds)
    public const int KEY_PRESS_DELAY_MS = 50;           // Delay between key press and release
    public const int MODIFIER_DELAY_MS = 100;           // Delay when pressing modifier keys
    public const int WINDOW_SWITCH_DELAY_MS = 150;      // Delay after switching windows
    public const int ALT_TAB_DELAY_MS = 100;            // Delay for Alt+Tab operations
    public const int WORD_OPEN_DELAY_MS = 500;          // Wait for Word to initialize after opening
    public const int HOTKEY_DELAY_MS = 150;             // Wait before sending hotkey to Word

    // Window management constants
    public const int WINDOW_RESTORE_TIMEOUT_MS = 1000;  // Timeout for window restore operation
    public const int FOREGROUND_TIMEOUT_MS = 2000;      // Timeout for SetForegroundWindow
    public const int WORD_OPEN_TIMEOUT_MS = 15000;      // Timeout for Word to open (15 seconds)
    public const int WORD_CLOSE_TIMEOUT_SECONDS = 10;   // Timeout for Word to close after macro

    // Polling intervals
    public const int POLL_INTERVAL_MS = 50;             // Polling interval for WaitForCondition
    public const int WORD_CLOSE_CHECK_INTERVAL_MS = 100; // Interval for checking if Word closed

    // Metrics constants
    public const int MAX_METRICS_PER_OPERATION = 1000;  // Maximum metrics entries to keep per operation

    // File validation constants
    public const long MAX_WORD_FILE_SIZE_BYTES = 100 * 1024 * 1024; // 100MB max file size

    // HTTP constants
    public const int SERVER_SHUTDOWN_TIMEOUT_MS = 2000; // Timeout for graceful server shutdown

    /// <summary>Max raw HTTP rows kept per channel for dashboard folding + pagination.</summary>
    public const int MAX_ACTIVITY_RAW_ENTRIES = 5000;

    /// <summary>Max rows per page for /api/activity (client may request lower).</summary>
    public const int MAX_ACTIVITY_PAGE_SIZE = 100;

    /// <summary>Extension-reported DevStrider outcome rows for dashboard (Option B).</summary>
    public const int MAX_CLIENT_DEVSTRIDER_OUTCOMES = 1000;
}
