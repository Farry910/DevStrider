using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Port of <c>BidAssistantApp/KeyboardHelper.cs</c>. Pure Win32 P/Invoke; no WPF/WinForms
/// dependencies. Used by the LocalApiServer to:
///   - Send <c>Ctrl+V</c> + <c>Enter</c> on whatever has OS focus (the ChatGPT tab).
///   - Open a Word .docm, focus it, send the user-configured hotkey to trigger its macro,
///     wait for Word to close, return focus to Chrome.
/// All timings/aliases were merged in from the BAA <c>Constants.cs</c> so we don't drag a
/// constants file across.
/// </summary>
internal static class KeyboardHelper
{
    // ─── Timings (in ms) — copied from BidAssistantApp/Constants.cs ───────────
    private const int KEY_PRESS_DELAY_MS = 50;
    private const int MODIFIER_DELAY_MS = 100;
    private const int WINDOW_SWITCH_DELAY_MS = 150;
    private const int ALT_TAB_DELAY_MS = 100;
    private const int WORD_OPEN_DELAY_MS = 500;
    public const int HOTKEY_DELAY_MS = 150;
    private const int WINDOW_RESTORE_TIMEOUT_MS = 1000;
    private const int FOREGROUND_TIMEOUT_MS = 2000;
    private const int WORD_OPEN_TIMEOUT_MS = 15000;
    public const int WORD_CLOSE_TIMEOUT_SECONDS = 10;
    private const int POLL_INTERVAL_MS = 50;
    private const int WORD_CLOSE_CHECK_INTERVAL_MS = 100;

    // ─── Win32 constants ─────────────────────────────────────────────────────
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

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_UNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public INPUT_UNION u; }

    public static (List<int> modifiers, int keyVk)? ParseHotkey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = Regex.Replace(raw.Trim(), @"\s*[\+\-_]\s*", "+");
        var parts = raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;

        var modifiers = new List<int>();
        var keyName = parts[^1];

        var modifierAliases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["ctrl"] = 0x11, ["control"] = 0x11, ["ctl"] = 0x11,
            ["shift"] = 0x10, ["shft"] = 0x10,
            ["alt"] = 0x12, ["menu"] = 0x12,
            ["win"] = 0x5B, ["windows"] = 0x5B, ["meta"] = 0x5B, ["cmd"] = 0x5B
        };

        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (modifierAliases.TryGetValue(parts[i], out var mod)) modifiers.Add(mod);
            else if (VkNames.TryGetValue(parts[i], out mod)) modifiers.Add(mod);
            else return null;
        }

        if (VkNames.TryGetValue(keyName, out var keyVk)) return (modifiers, keyVk);
        if (keyName.Length == 1)
        {
            var c = char.ToUpperInvariant(keyName[0]);
            if (c >= 'A' && c <= 'Z') return (modifiers, c);
            if (c >= '0' && c <= '9') return (modifiers, c);
        }
        if (keyName.StartsWith("numpad", StringComparison.OrdinalIgnoreCase) && keyName.Length == 7)
        {
            var d = keyName[6];
            if (d >= '0' && d <= '9') return (modifiers, 0x60 + (d - '0'));
        }
        return null;
    }

    /// <summary>Send <c>Ctrl+V</c> then <c>Enter</c> to whatever has OS focus right now.</summary>
    public static void PasteSubmit()
    {
        SendKey(VK_CONTROL, false);
        SendKey(VK_V, false);
        SendKey(VK_V, true);
        SendKey(VK_CONTROL, true);
        Thread.Sleep(KEY_PRESS_DELAY_MS);
        SendKey(VK_RETURN, false);
        SendKey(VK_RETURN, true);
    }

    public static IntPtr GetForegroundWindow() => NativeGetForegroundWindow();

    private static bool WaitForCondition(Func<bool> condition, int timeoutMs, int pollMs = POLL_INTERVAL_MS)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(pollMs);
        }
        return false;
    }

    public static IntPtr FindWordWindow()
    {
        var hwnd = FindWindowW("OpusApp", null);
        if (hwnd != IntPtr.Zero) return hwnd;
        return FindWindowByTitleContains("Microsoft Word");
    }

    private static IntPtr FindWindowByTitleContains(string titleContains)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            var length = GetWindowTextLength(hwnd);
            if (length > 0)
            {
                var sb = new StringBuilder(length + 1);
                GetWindowText(hwnd, sb, sb.Capacity);
                if (sb.ToString().Contains(titleContains, StringComparison.OrdinalIgnoreCase))
                {
                    found = hwnd;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static bool SetForegroundWindow(IntPtr hwnd)
    {
        try
        {
            var currentFg = NativeGetForegroundWindow();
            if (IsIconic(hwnd))
            {
                ShowWindow(hwnd, SW_RESTORE);
                if (!WaitForCondition(() => !IsIconic(hwnd), WINDOW_RESTORE_TIMEOUT_MS))
                    Thread.Sleep(WINDOW_SWITCH_DELAY_MS);
            }
            else
            {
                ShowWindow(hwnd, SW_SHOW);
                Thread.Sleep(KEY_PRESS_DELAY_MS);
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

                return WaitForCondition(() => NativeGetForegroundWindow() == hwnd, FOREGROUND_TIMEOUT_MS);
            }
            finally
            {
                if (attached) AttachThreadInput(currentThread, targetThread, false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[KeyboardHelper] SetForegroundWindow failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Sends a single un-modified key to a window via <c>PostMessage</c>. Faster than
    /// foreground-switch + SendInput when no modifiers are needed.</summary>
    public static bool PostSingleKeyToWindow(IntPtr hwnd, List<int> modifiers, int keyVk)
    {
        if (modifiers.Count > 0) return false;
        var scan = MapVirtualKeyW((uint)keyVk, 0);
        var lparamDown = (IntPtr)((scan << 16) | 1);
        var lparamUp = (IntPtr)((scan << 16) | 1 | (1 << 30) | (1 << 31));
        PostMessageW(hwnd, WM_KEYDOWN, (IntPtr)keyVk, lparamDown);
        Thread.Sleep(KEY_PRESS_DELAY_MS);
        PostMessageW(hwnd, WM_KEYUP, (IntPtr)keyVk, lparamUp);
        return true;
    }

    public static void PressHotkey(List<int> modifiers, int keyVk)
    {
        foreach (var vk in modifiers) SendKey(vk, false);
        SendKey(keyVk, false);
        Thread.Sleep(MODIFIER_DELAY_MS);
        SendKey(keyVk, true);
        foreach (var vk in modifiers.AsEnumerable().Reverse()) SendKey(vk, true);
    }

    public static bool AltTabToWindow(IntPtr targetHwnd, int maxAttempts = 10)
    {
        SendKey(VK_MENU, false);
        Thread.Sleep(ALT_TAB_DELAY_MS);
        for (int i = 0; i < maxAttempts; i++)
        {
            SendKey(VK_TAB, false);
            Thread.Sleep(KEY_PRESS_DELAY_MS);
            SendKey(VK_TAB, true);
            Thread.Sleep(WINDOW_SWITCH_DELAY_MS);
            if (NativeGetForegroundWindow() == targetHwnd) break;
        }
        SendKey(VK_MENU, true);
        Thread.Sleep(WINDOW_SWITCH_DELAY_MS);
        return NativeGetForegroundWindow() == targetHwnd;
    }

    public static bool WaitForWordClose(int timeoutSeconds = WORD_CLOSE_TIMEOUT_SECONDS)
    {
        var checks = timeoutSeconds * 10;
        for (int i = 0; i < checks; i++)
        {
            Thread.Sleep(WORD_CLOSE_CHECK_INTERVAL_MS);
            if (FindWordWindow() == IntPtr.Zero) return true;
        }
        return false;
    }

    public static IntPtr OpenWordDocument(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[KeyboardHelper] Failed to start Word: {ex.Message}");
            return IntPtr.Zero;
        }
        IntPtr foundHwnd = IntPtr.Zero;
        if (WaitForCondition(() => { foundHwnd = FindWordWindow(); return foundHwnd != IntPtr.Zero; },
                             WORD_OPEN_TIMEOUT_MS, WINDOW_SWITCH_DELAY_MS))
        {
            return foundHwnd;
        }
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
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Constants needed by the LocalApiServer's refresh-word handler.</summary>
    public const int RefreshWordOpenDelayMs = WORD_OPEN_DELAY_MS;
}
