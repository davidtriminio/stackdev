using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace StackDev.Core.Seguridad;

/// <summary>
/// Cifrado de secretos con DPAPI de Windows (<c>CryptProtectData</c>, ámbito del usuario actual).
/// Solo tu usuario de Windows, en este equipo, puede descifrarlos: no hay clave que guardar ni que perder.
/// Los valores cifrados se escriben como <c>dpapi:BASE64</c>; lo que no tenga ese prefijo se lee como texto normal
/// (así un archivo escrito a mano o heredado sigue funcionando y se cifra solo la próxima vez que se guarde).
/// En sistemas que no son Windows no se cifra nada.
/// </summary>
public static class Proteccion
{
    public const string Prefijo = "dpapi:";
    private static readonly byte[] Entropia = Encoding.UTF8.GetBytes("stackdev/v1");

    public static bool Disponible => OperatingSystem.IsWindows();

    public static bool EstaCifrado(string? texto) => texto is not null && texto.StartsWith(Prefijo, StringComparison.Ordinal);

    /// <summary>Nombres de variable que casi seguro guardan un secreto (contraseñas, tokens, claves, cadenas de conexión).</summary>
    public static bool PareceSecreto(string nombre) =>
        Regex.IsMatch(nombre, "PASSWORD|PASSWD|PWD|SECRET|TOKEN|APIKEY|API_KEY|PRIVATE|CREDENTIAL|CONNECTIONSTRING|CONNECTION_STRING|__KEY|_KEY$|^KEY$", RegexOptions.IgnoreCase);

    /// <summary>Cifra un texto; si ya está cifrado, está vacío o no hay DPAPI, lo devuelve igual.</summary>
    public static string Cifrar(string? texto)
    {
        if (string.IsNullOrEmpty(texto) || EstaCifrado(texto) || !Disponible) return texto ?? "";
        try { return Prefijo + Convert.ToBase64String(Transformar(Encoding.UTF8.GetBytes(texto), cifrar: true)); }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        { return texto; }
    }

    /// <summary>Descifra un texto <c>dpapi:…</c>; si no está cifrado lo devuelve igual. Si no se puede (otro usuario o equipo), devuelve "".</summary>
    public static string Descifrar(string? texto)
    {
        if (string.IsNullOrEmpty(texto) || !EstaCifrado(texto)) return texto ?? "";
        if (!Disponible) return "";
        try { return Encoding.UTF8.GetString(Transformar(Convert.FromBase64String(texto[Prefijo.Length..]), cifrar: false)); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or DllNotFoundException or EntryPointNotFoundException)
        { return ""; }
    }

    // ------------------------------------------------------------ DPAPI (crypt32.dll)

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Tamano; public IntPtr Datos; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob entrada, string? descripcion, ref Blob entropia, IntPtr reservado, IntPtr prompt, int flags, out Blob salida);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob entrada, IntPtr descripcion, ref Blob entropia, IntPtr reservado, IntPtr prompt, int flags, out Blob salida);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memoria);

    private const int SinInterfaz = 0x1;   // CRYPTPROTECT_UI_FORBIDDEN

    private static byte[] Transformar(byte[] datos, bool cifrar)
    {
        var entrada = new Blob { Tamano = datos.Length, Datos = Marshal.AllocHGlobal(datos.Length) };
        var entropia = new Blob { Tamano = Entropia.Length, Datos = Marshal.AllocHGlobal(Entropia.Length) };
        try
        {
            Marshal.Copy(datos, 0, entrada.Datos, datos.Length);
            Marshal.Copy(Entropia, 0, entropia.Datos, Entropia.Length);
            var ok = cifrar
                ? CryptProtectData(ref entrada, "stackdev", ref entropia, IntPtr.Zero, IntPtr.Zero, SinInterfaz, out var salida)
                : CryptUnprotectData(ref entrada, IntPtr.Zero, ref entropia, IntPtr.Zero, IntPtr.Zero, SinInterfaz, out salida);
            if (!ok) throw new InvalidOperationException("DPAPI falló: " + Marshal.GetLastWin32Error());
            try
            {
                var res = new byte[salida.Tamano];
                Marshal.Copy(salida.Datos, res, 0, salida.Tamano);
                return res;
            }
            finally { LocalFree(salida.Datos); }
        }
        finally
        {
            Marshal.FreeHGlobal(entrada.Datos);
            Marshal.FreeHGlobal(entropia.Datos);
        }
    }
}
