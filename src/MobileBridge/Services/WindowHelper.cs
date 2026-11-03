using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MobileBridge.Services;

/// <summary>Finds the emulator's phone window and makes sure it is fully visible on screen.</summary>
public static class WindowHelper
{
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    const uint MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;
    const int SW_RESTORE = 9;

    static IntPtr FindPhoneWindow()
    {
        var pids = Process.GetProcessesByName("qemu-system-x86_64").Select(p => (uint)p.Id).ToHashSet();
        if (pids.Count == 0) return IntPtr.Zero;
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains(pid)) return true;
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, sb.Capacity);
            if (sb.ToString().StartsWith("Android Emulator - " + EmulatorService.AvdName, StringComparison.Ordinal))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Moves the phone window fully onto the screen (right side of the work area) if any part of
    /// it is off-screen. Returns false when the phone window could not be found.
    /// </summary>
    public static bool EnsurePhoneWindowVisible(bool bringToFront)
    {
        var h = FindPhoneWindow();
        if (h == IntPtr.Zero) return false;
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        if (!GetWindowRect(h, out var r)) return false;

        // Use the monitor that contains the window's centre (or the nearest / primary one).
        var centre = new POINT { X = (r.Left + r.Right) / 2, Y = Math.Max(r.Top, 0) + 10 };
        var mon = MonitorFromPoint(centre, MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero) mon = MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;
        var work = mi.rcWork;

        int w = r.Right - r.Left, ht = r.Bottom - r.Top;
        // Only move when the title bar is unreachable or most of the window is outside the screen,
        // so a position the user picked is left alone.
        int cx = (r.Left + r.Right) / 2;
        bool offScreen = r.Top < work.Top || r.Top > work.Bottom - 60 ||
                         cx < work.Left || cx > work.Right;
        if (offScreen)
        {
            // Leave room on the right for the emulator's side toolbar (~70 px).
            int x = Math.Max(work.Left, work.Right - w - 90);
            int y = Math.Max(work.Top, work.Top + (work.Bottom - work.Top - ht) / 2);
            SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | (bringToFront ? 0 : SWP_NOACTIVATE));
            Log.Info($"Moved the phone window on-screen (was at {r.Left},{r.Top}; now {x},{y}).");
        }
        if (bringToFront) SetForegroundWindow(h);
        return true;
    }
}
