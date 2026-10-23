using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace StackDev.Gui;

/// <summary>Pinta la barra de título de Windows en oscuro para que combine con el tema.</summary>
public static class TitleBar
{
    private const int UseImmersiveDarkMode = 20;   // Windows 10 (2004+) y 11
    private const int BarColor = 35;              // Windows 11
    private const int TextColor = 36;              // Windows 11

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int val, int size);

    // COLORREF es 0x00BBGGRR
    private static int Colorref(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    public static void Apply(Window window)
    {
        void Put()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var dark = 1;
            DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref dark, sizeof(int));
            var bar = Colorref(0x0B, 0x0D, 0x11);    // igual que la barra lateral
            DwmSetWindowAttribute(hwnd, BarColor, ref bar, sizeof(int));
            var text = Colorref(0xE6, 0xE8, 0xEE);
            DwmSetWindowAttribute(hwnd, TextColor, ref text, sizeof(int));
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Put();
        else window.SourceInitialized += (_, _) => Put();
    }
}
