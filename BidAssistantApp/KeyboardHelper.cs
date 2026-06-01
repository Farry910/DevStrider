using System.Runtime.InteropServices;

namespace BidAssistantApp;

static class KeyboardHelper
{
    private const int KEYEVENTF_KEYUP = 0x0002;
    private const int INPUT_KEYBOARD = 1;
    private const int VK_RETURN = 0x0D;
    private const int VK_CONTROL = 0x11;
    private const int VK_V = 0x56;
    private const int VK_MENU = 0x12;
    private const int VK_TAB = 0x09;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    private static readonly Dictionary<string, int> VkNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73, ["F5"] = 0x74,
        ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77, ["F9"] = 0x78, ["F10"] = 0x79,
        ["F11"] = 0x7A, ["F12"] = 0x7B, ["F13"] = 0x7C, ["F14"] = 0x7D, ["F15"] = 0x7E,
        ["F16"] = 0x7F, ["F17"] = 0x80, ["F18"] = 0x81, ["F19"] = 0x82, ["F20"] = 0x83,
        ["F21"] = 0x84, ["F22"] = 0x85, ["F23"] = 0x86, ["F24"] = 0x87,
        ["Ctrl"] = 0x11, ["Control"] = 0x11, ["Shift"] = 0x10, ["Alt"] = 0x12,
        ["Win"] = 0x5B, ["Windows"] = 0x5B, ["Meta"] = 0x5B,
        ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Return"] = 0x0D, ["Escape"] = 0x1B, ["Esc"] = 0x1B,
        ["Space"] = 0x20, ["Spacebar"] = 0x20,
        ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E,
        ["Backspace"] = 0x08, ["CapsLock"] = 0x14,
        ["NumLock"] = 0x90, ["ScrollLock"] = 0x91,
        ["Left"] = 0x25, ["Right"] = 0x27, ["Up"] = 0x26, ["Down"] = 0x28,
    };

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern ushort MapVirtualKeyW(uint uCode, uint uMapType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr NativeGetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    private static extern bool NativeSetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_UNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUT_UNION u;
    }

    public static (List<int> modifiers, int keyVk)? ParseHotkey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        
        // Normalize: trim, handle multiple separators
        raw = raw.Trim();
        raw = System.Text.RegularExpressions.Regex.Replace(raw, @"\s*[\+\-_]\s*", "+");
        
        var parts = raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;

        var modifiers = new List<int>();
        var keyName = parts[^1];

        // Modifier aliases for better parsing
        var modifierAliases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["ctrl"] = 0x11, ["control"] = 0x11, ["ctl"] = 0x11,
            ["shift"] = 0x10, ["shft"] = 0x10,
            ["alt"] = 0x12, ["menu"] = 0x12,
            ["win"] = 0x5B, ["windows"] = 0x5B, ["meta"] = 0x5B, ["cmd"] = 0x5B
        };

        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (modifierAliases.TryGetValue(parts[i], out var mod))
                modifiers.Add(mod);
            else if (VkNames.TryGetValue(parts[i], out mod))
                modifiers.Add(mod);
            else
                return null; // Invalid modifier
        }

        // Try VK names first (case-insensitive)
        if (VkNames.TryGetValue(keyName, out var keyVk))
            return (modifiers, keyVk);

        // Handle single character keys
        if (keyName.Length == 1)
        {
            var c = char.ToUpperInvariant(keyName[0]);
            if (c >= 'A' && c <= 'Z') return (modifiers, c);
            if (c >= '0' && c <= '9') return (modifiers, c);
        }
        
        // Handle numpad keys
        if (keyName.StartsWith("numpad", StringComparison.OrdinalIgnoreCase) && keyName.Length == 7)
        {
            var digit = keyName[6];
            if (digit >= '0' && digit <= '9')
                return (modifiers, 0x60 + (digit - '0')); // VK_NUMPAD0-9
        }
        
        return null;
    }

    public static void PasteSubmit()
    {
        SendKey(VK_CONTROL, false);
        SendKey(VK_V, false);
        SendKey(VK_V, true);
        SendKey(VK_CONTROL, true);
        Thread.Sleep(Constants.KEY_PRESS_DELAY_MS);
        SendKey(VK_RETURN, false);
        SendKey(VK_RETURN, true);
    }

    public static IntPtr GetForegroundWindow() => NativeGetForegroundWindow();

    public static bool WaitForCondition(Func<bool> condition, int timeoutMs = 5000, int pollMs = Constants.POLL_INTERVAL_MS)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(pollMs);
        }
        return false;
    }

    public static IntPtr FindWordWindow()
    {
        // Try Word's window class first (most reliable)
        var hwnd = FindWindowW("OpusApp", null);
        if (hwnd != IntPtr.Zero) return hwnd;

        // Fallback: find by full window title (avoids matching "WordPad", etc.)
        return FindWindowByTitleContains("Microsoft Word");
    }

    private static IntPtr FindWindowByTitleContains(string titleContains)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, lParam) =>
        {
            var length = GetWindowTextLength(hwnd);
            if (length > 0)
            {
                var sb = new System.Text.StringBuilder(length + 1);
                GetWindowText(hwnd, sb, sb.Capacity);
                if (sb.ToString().Contains(titleContains, StringComparison.OrdinalIgnoreCase))
                {
                    found = hwnd;
                    return false; // Stop enumeration
                }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    public static bool SetForegroundWindow(IntPtr hwnd)
    {
        try
        {
            var currentFg = NativeGetForegroundWindow();
            if (IsIconic(hwnd))
            {
                ShowWindow(hwnd, SW_RESTORE);
                if (!WaitForCondition(() => !IsIconic(hwnd), Constants.WINDOW_RESTORE_TIMEOUT_MS, Constants.POLL_INTERVAL_MS))
                    Thread.Sleep(Constants.WINDOW_SWITCH_DELAY_MS); // Fallback
            }
            else
            {
                ShowWindow(hwnd, SW_SHOW);
                Thread.Sleep(Constants.KEY_PRESS_DELAY_MS);
            }

            var currentThread = GetWindowThreadProcessId(currentFg, IntPtr.Zero);
            var targetThread = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
            bool attached = false;
            try
            {
                if (currentThread != targetThread)
                {
                    AttachThreadInput(currentThread, targetThread, true);
                    attached = true;
                }

                BringWindowToTop(hwnd);
                ShowWindow(hwnd, SW_SHOW);
                NativeSetForegroundWindow(hwnd);

                // Wait for window to become foreground with early exit
                var success = WaitForCondition(() => NativeGetForegroundWindow() == hwnd, Constants.FOREGROUND_TIMEOUT_MS, Constants.POLL_INTERVAL_MS);

                if (!success)
                    Logger.Warning("SetForegroundWindow: Window did not become foreground");

                return success;
            }
            finally
            {
                if (attached)
                    AttachThreadInput(currentThread, targetThread, false);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("SetForegroundWindow failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Sends a single key (no modifiers) to a window via PostMessage.
    /// Returns false if modifiers are present — use PressHotkey with SetForegroundWindow instead.
    /// </summary>
    public static bool PostSingleKeyToWindow(IntPtr hwnd, List<int> modifiers, int keyVk)
    {
        if (modifiers.Count > 0) return false;
        var scan = MapVirtualKeyW((uint)keyVk, 0);
        var lparamDown = (IntPtr)((scan << 16) | 1);
        var lparamUp = (IntPtr)((scan << 16) | 1 | (1 << 30) | (1 << 31));
        PostMessageW(hwnd, WM_KEYDOWN, (IntPtr)keyVk, lparamDown);
        Thread.Sleep(Constants.KEY_PRESS_DELAY_MS);
        PostMessageW(hwnd, WM_KEYUP, (IntPtr)keyVk, lparamUp);
        return true;
    }

    public static void PressHotkey(List<int> modifiers, int keyVk)
    {
        foreach (var vk in modifiers)
            SendKey(vk, false);
        SendKey(keyVk, false);
        Thread.Sleep(Constants.MODIFIER_DELAY_MS);
        SendKey(keyVk, true);
        foreach (var vk in modifiers.AsEnumerable().Reverse())
            SendKey(vk, true);
    }

    public static bool AltTabToWindow(IntPtr targetHwnd, int maxAttempts = 10)
    {
        SendKey(VK_MENU, false);
        Thread.Sleep(Constants.ALT_TAB_DELAY_MS);

        for (int i = 0; i < maxAttempts; i++)
        {
            SendKey(VK_TAB, false);
            Thread.Sleep(Constants.KEY_PRESS_DELAY_MS);
            SendKey(VK_TAB, true);
            Thread.Sleep(Constants.WINDOW_SWITCH_DELAY_MS);
            if (NativeGetForegroundWindow() == targetHwnd)
                break;
        }

        SendKey(VK_MENU, true);
        Thread.Sleep(Constants.WINDOW_SWITCH_DELAY_MS);
        return NativeGetForegroundWindow() == targetHwnd;
    }

    public static bool WaitForWordClose(int timeoutSeconds = Constants.WORD_CLOSE_TIMEOUT_SECONDS)
    {
        var checks = timeoutSeconds * 10;
        for (int i = 0; i < checks; i++)
        {
            Thread.Sleep(Constants.WORD_CLOSE_CHECK_INTERVAL_MS);
            if (FindWordWindow() == IntPtr.Zero)  // Use FindWordWindow for consistency
                return true;
        }
        Logger.Warning($"Word did not close within {timeoutSeconds} seconds");
        return false;
    }

    public static IntPtr OpenWordDocument(string path)
    {
        try
        {
            Logger.Info($"Opening Word document: {path}");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to start Word process: {ex.Message}");
            return IntPtr.Zero;
        }

        // Wait for Word window with early exit (up to 15 seconds)
        IntPtr foundHwnd = IntPtr.Zero;
        if (WaitForCondition(() => { foundHwnd = FindWordWindow(); return foundHwnd != IntPtr.Zero; }, Constants.WORD_OPEN_TIMEOUT_MS, Constants.WINDOW_SWITCH_DELAY_MS))
        {
            Logger.Info($"Word window found: {foundHwnd}");
            return foundHwnd;
        }
        
        Logger.Error("Word window not found after timeout");
        return IntPtr.Zero;
    }

    public static void ReturnToChrome(IntPtr chromeHwnd)
    {
        if (chromeHwnd != IntPtr.Zero && NativeGetForegroundWindow() != chromeHwnd)
            NativeSetForegroundWindow(chromeHwnd);
    }

    private static void SendKey(int vk, bool keyUp)
    {
        var flags = keyUp ? KEYEVENTF_KEYUP : 0u;
        var scan = MapVirtualKeyW((uint)vk, 0);
        var inp = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUT_UNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    wScan = (ushort)scan,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, [inp], Marshal.SizeOf<INPUT>());
    }
}
