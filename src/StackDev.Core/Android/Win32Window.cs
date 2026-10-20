using System.Runtime.InteropServices;

namespace StackDev.Core.Android;

/// <summary>
/// El emulador no tiene opción de posición. Se enumeran las ventanas visibles de qemu-system-* (pantalla y barra lateral),
/// se calcula el rectángulo que las contiene a todas y se mueven juntas al centro del área de trabajo del monitor.
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
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

    private static bool _dpi;

    /// <summary>Centra todas las ventanas del proceso en el monitor de la principal. Devuelve false si no pudo.</summary>
    public static bool Centrar(int pid, IntPtr principal)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (!_dpi) { SetProcessDPIAware(); _dpi = true; }
            if (IsIconic(principal)) ShowWindow(principal, 9);   // SW_RESTORE

            var ventanas = new List<IntPtr>();
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out var p);
                if (p == (uint)pid && IsWindowVisible(h))
                {
                    GetWindowRect(h, out var r);
                    if (r.Right - r.Left > 40 && r.Bottom - r.Top > 40) ventanas.Add(h);
                }
                return true;
            }, IntPtr.Zero);
            if (ventanas.Count == 0) return false;

            int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
            foreach (var h in ventanas)
            {
                GetWindowRect(h, out var r);
                left = Math.Min(left, r.Left); top = Math.Min(top, r.Top);
                right = Math.Max(right, r.Right); bottom = Math.Max(bottom, r.Bottom);
            }

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(MonitorFromWindow(principal, 2), ref mi)) return false;
            var w = mi.rcWork;

            var dx = (w.Left + w.Right) / 2 - (left + right) / 2;
            var dy = (w.Top + w.Bottom) / 2 - (top + bottom) / 2;
            if (top + dy < w.Top) dy = w.Top - top;       // la barra de título siempre visible
            if (left + dx < w.Left) dx = w.Left - left;

            // Primero las secundarias y al final la principal (la barra lateral la sigue)
            if (ventanas.Remove(principal)) ventanas.Add(principal);
            foreach (var h in ventanas)
            {
                GetWindowRect(h, out var r);
                SetWindowPos(h, IntPtr.Zero, r.Left + dx, r.Top + dy, 0, 0, 0x0001 | 0x0004 | 0x0010);   // NOSIZE | NOZORDER | NOACTIVATE
            }
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException) { return false; }
    }
}
