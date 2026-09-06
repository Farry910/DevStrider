using System.Runtime.InteropServices;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Synthesized keystrokes for <c>/trigger-paste-submit</c>: <c>Ctrl+V</c> then <c>Enter</c> to
/// whatever the OS has focused. Pure Win32 P/Invoke; no WPF/WinForms dependencies.
///
/// <para>
/// This used to be most of a keyboard-and-window automation library — parsing a configured hotkey,
/// opening a .docm, alt-tabbing Word to the foreground, pressing the hotkey to trigger its macro,
/// waiting for Word to close, handing focus back to Chrome. All of it existed to serve
/// <c>/refresh-word</c>, and all of it is gone with that endpoint: <see cref="WordMacroService"/>
/// runs the macro over COM against an invisible Word instance, passing the resume text and job
/// description as arguments. Driving another application's UI through its window manager is a
/// last resort, and there is no longer anything here that needs one.
/// </para>
/// </summary>
internal static class KeyboardHelper
{
    /// <summary>Between the paste and the Enter that submits it.</summary>
    private const int KEY_PRESS_DELAY_MS = 50;

    private const int KEYEVENTF_KEYUP = 0x0002;
    private const int INPUT_KEYBOARD = 1;
    private const int VK_RETURN = 0x0D;
    private const int VK_CONTROL = 0x11;
    private const int VK_V = 0x56;

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

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
}
