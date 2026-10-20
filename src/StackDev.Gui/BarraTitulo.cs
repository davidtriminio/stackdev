using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace StackDev.Gui;

/// <summary>Pinta la barra de título de Windows en oscuro para que combine con el tema.</summary>
public static class BarraTitulo
{
    private const int UsarModoOscuroInmersivo = 20;   // Windows 10 (2004+) y 11
    private const int ColorDeBarra = 35;              // Windows 11
    private const int ColorDeTexto = 36;              // Windows 11

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int atributo, ref int valor, int tamano);

    // COLORREF es 0x00BBGGRR
    private static int Colorref(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    public static void Aplicar(Window ventana)
    {
        void Poner()
        {
            var hwnd = new WindowInteropHelper(ventana).Handle;
            if (hwnd == IntPtr.Zero) return;
            var oscuro = 1;
            DwmSetWindowAttribute(hwnd, UsarModoOscuroInmersivo, ref oscuro, sizeof(int));
            var barra = Colorref(0x0B, 0x0D, 0x11);    // igual que la barra lateral
            DwmSetWindowAttribute(hwnd, ColorDeBarra, ref barra, sizeof(int));
            var texto = Colorref(0xE6, 0xE8, 0xEE);
            DwmSetWindowAttribute(hwnd, ColorDeTexto, ref texto, sizeof(int));
        }

        if (new WindowInteropHelper(ventana).Handle != IntPtr.Zero) Poner();
        else ventana.SourceInitialized += (_, _) => Poner();
    }
}
