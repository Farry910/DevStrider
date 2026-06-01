using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DevStrider.Desktop.Views;

/// <summary>
/// WPF chromeless windows (<c>WindowStyle=None</c> + <c>AllowsTransparency=True</c>) maximize
/// to the entire monitor instead of the working area, which hides whatever lives under the
/// taskbar. The OS asks every top-level window for its maximum size/position via
/// <c>WM_GETMINMAXINFO</c>; we intercept that message and reply with the working-area rect
/// of the nearest monitor so the bottom edge stops at the taskbar.
/// </summary>
internal static class MaximizeHelper
{
    private const int WM_GETMINMAXINFO = 0x0024;

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        };
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        // Pick the monitor the window currently overlaps (multi-monitor friendly).
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (GetMonitorInfo(monitor, ref info))
            {
                var work = info.rcWork;
                var screen = info.rcMonitor;
                // ptMaxPosition is relative to the monitor's top-left; on a side-mounted
                // taskbar the working area's left/top can differ from the monitor's.
                mmi.ptMaxPosition.x = Math.Abs(work.Left - screen.Left);
                mmi.ptMaxPosition.y = Math.Abs(work.Top - screen.Top);
                mmi.ptMaxSize.x = Math.Abs(work.Right - work.Left);
                mmi.ptMaxSize.y = Math.Abs(work.Bottom - work.Top);
                // Don't let WPF "expand to current display" exceed the work area either.
                mmi.ptMaxTrackSize.x = mmi.ptMaxSize.x;
                mmi.ptMaxTrackSize.y = mmi.ptMaxSize.y;
            }
        }

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }
}
