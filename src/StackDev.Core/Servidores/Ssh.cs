using System.Text.RegularExpressions;
using StackDev.Core.Proc;

namespace StackDev.Core.Servidores;

/// <summary>ssh y scp del sistema, con las opciones que evitan que se queden esperando (sin contraseñas, tiempo límite).</summary>
public static class Ssh
{
    public static List<string> Opciones(Servidor s, bool interactivo)
    {
        var a = new List<string> { "-o", "ConnectTimeout=8", "-o", "StrictHostKeyChecking=accept-new" };
        if (!interactivo) { a.Add("-o"); a.Add("BatchMode=yes"); }
        if (s.Puerto is > 0 and not 22) { a.Add("-p"); a.Add(s.Puerto.ToString()); }
        if (!string.IsNullOrWhiteSpace(s.Clave)) { a.Add("-i"); a.Add(s.Clave); }
        a.Add($"{s.Usuario}@{s.Host}");
        return a;
    }

    /// <summary>Ejecuta un comando en el servidor y devuelve su salida.</summary>
    public static ProcResult Ejecutar(Servidor s, string comando, int timeoutSeg = 20, CancellationToken ct = default)
    {
        var a = Opciones(s, interactivo: false);
        a.Add(comando);
        return ProcessRunner.Run("ssh", a, timeoutSeg, ct: ct);
    }

    private static string Q(string x) => "'" + x.Replace("'", "''") + "'";

    /// <summary>
    /// Línea para abrir una terminal que ejecuta ssh (con <paramref name="comando"/>, o sesión interactiva si está vacío)
    /// y, al terminar, se queda en PowerShell.
    /// </summary>
    public static string LineaTerminal(Servidor s, string comando)
    {
        var partes = new List<string> { "&", "ssh.exe", "-t" };
        partes.AddRange(Opciones(s, interactivo: true).Select(Q));
        if (!string.IsNullOrWhiteSpace(comando)) partes.Add(Q(comando));
        var cmd = string.Join(' ', partes).Replace("\"", "\\\"");
        return $"\"{Shell.PowerShellExe}\" -NoLogo -NoExit -Command \"{cmd}\"";
    }

    // ------------------------------------------------------------ Permisos de la clave (Windows)

    /// <summary>SSH rechaza una clave que otros usuarios pueden leer. En Windows se mira con icacls.</summary>
    public static bool PermisosClaveOk(string ruta)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(ruta) || !File.Exists(ruta)) return true;
        var r = ProcessRunner.Run("icacls", new[] { ruta }, 10);
        return !Regex.IsMatch(r.Salida, @"Authenticated Users|BUILTIN\\Users|Everyone|Usuarios autenticados|Todos");
    }

    private static bool Restringir(string ruta)
    {
        var yo = $"{Environment.UserDomainName}\\{Environment.UserName}";
        ProcessRunner.Run("icacls", new[] { ruta, "/inheritance:r" }, 10);
        ProcessRunner.Run("icacls", new[] { ruta, "/grant:r", $"{yo}:(R)" }, 10);
        foreach (var sid in new[] { "*S-1-5-11", "*S-1-5-32-545", "*S-1-1-0" })
            ProcessRunner.Run("icacls", new[] { ruta, "/remove", sid }, 10);
        return PermisosClaveOk(ruta);
    }

    /// <summary>
    /// Deja la clave legible solo por ti. En discos exFAT/externos no se puede: entonces copia la clave a ~\.ssh.
    /// Devuelve la ruta que hay que usar, o null si no se logró.
    /// </summary>
    public static string? ArreglarClave(string ruta)
    {
        if (Restringir(ruta)) return ruta;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
            Directory.CreateDirectory(dir);
            var copia = Path.Combine(dir, Path.GetFileName(ruta));
            File.Copy(ruta, copia, overwrite: true);
            return Restringir(copia) ? copia : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
