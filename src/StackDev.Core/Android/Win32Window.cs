using System.Runtime.InteropServices;

namespace StackDev.Core.Android;

/// <summary>
/// The emulator has no position option. The visible qemu-system-* windows (screen and side bar) are enumerated,
/// the rectangle that contains them all is computed and they are moved together to the centre of the monitor's work area.
/// </summary>
public static class Win32Window
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    private delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO my);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

    private static bool _dpi;

    /// <summary>Centres every window of the process on the monitor of the main one. Returns false if it could not.</summary>
    public static bool Center(int pid, IntPtr main)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (!_dpi) { SetProcessDPIAware(); _dpi = true; }
            if (IsIconic(main)) ShowWindow(main, 9);   // SW_RESTORE

            var windows = new List<IntPtr>();
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out var p);
                if (p == (uint)pid && IsWindowVisible(h))
                {
                    GetWindowRect(h, out var r);
                    if (r.Right - r.Left > 40 && r.Bottom - r.Top > 40) windows.Add(h);
                }
                return true;
            }, IntPtr.Zero);
            if (windows.Count == 0) return false;

            int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
            foreach (var h in windows)
            {
                GetWindowRect(h, out var r);
                left = Math.Min(left, r.Left); top = Math.Min(top, r.Top);
                right = Math.Max(right, r.Right); bottom = Math.Max(bottom, r.Bottom);
            }

            var my = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(MonitorFromWindow(main, 2), ref my)) return false;
            var w = my.rcWork;

            var dx = (w.Left + w.Right) / 2 - (left + right) / 2;
            var dy = (w.Top + w.Bottom) / 2 - (top + bottom) / 2;
            if (top + dy < w.Top) dy = w.Top - top;       // the title bar always visible
            if (left + dx < w.Left) dx = w.Left - left;

            // Secondary windows first and the main one last (the side bar follows it)
            if (windows.Remove(main)) windows.Add(main);
            foreach (var h in windows)
            {
                GetWindowRect(h, out var r);
                SetWindowPos(h, IntPtr.Zero, r.Left + dx, r.Top + dy, 0, 0, 0x0001 | 0x0004 | 0x0010);   // NOSIZE | NOZORDER | NOACTIVATE
            }
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException) { return false; }
    }
}
