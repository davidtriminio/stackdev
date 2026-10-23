using System.Text.RegularExpressions;
using StackDev.Core.Proc;

namespace StackDev.Core.Servers;

/// <summary>ssh y scp del sistema, con las opciones que evitan que se queden esperando (sin contraseñas, tiempo límite).</summary>
public static class Ssh
{
    public static List<string> Options(Server s, bool interactive)
    {
        var a = new List<string>
        {
            "-o", "ConnectTimeout=8",
            "-o", "StrictHostKeyChecking=yes",                       // only servers whose fingerprint the user has confirmed
            "-o", HostKeys.KnownHostsOption,   // StackDev's own list, not the user's ~/.ssh
            "-o", "HashKnownHosts=no",
        };
        if (!interactive) { a.Add("-o"); a.Add("BatchMode=yes"); }
        if (s.Port is > 0 and not 22) { a.Add("-p"); a.Add(s.Port.ToString()); }
        if (!string.IsNullOrWhiteSpace(s.Key)) { a.Add("-i"); a.Add(s.Key); a.Add("-o"); a.Add("IdentitiesOnly=yes"); }
        a.Add($"{s.User}@{s.Host}");
        return a;
    }

    /// <summary>Ejecuta un comando en el servidor y devuelve su salida.</summary>
    public static ProcResult Run(Server s, string command, int timeoutSec = 20, CancellationToken ct = default)
    {
        var a = Options(s, interactive: false);
        a.Add(command);
        return ProcessRunner.Run("ssh", a, timeoutSec, ct: ct);
    }

    /// <summary>Options for scp (same safety settings; scp takes the port as -P and the destination as user@host:path).</summary>
    public static List<string> ScpOptions(Server s)
    {
        var a = new List<string>
        {
            "-q",
            "-o", "ConnectTimeout=8",
            "-o", "BatchMode=yes",
            "-o", "StrictHostKeyChecking=yes",
            "-o", HostKeys.KnownHostsOption,
            "-o", "HashKnownHosts=no",
        };
        if (s.Port is > 0 and not 22) { a.Add("-P"); a.Add(s.Port.ToString()); }
        if (!string.IsNullOrWhiteSpace(s.Key)) { a.Add("-i"); a.Add(s.Key); a.Add("-o"); a.Add("IdentitiesOnly=yes"); }
        return a;
    }

    /// <summary>Copies one local file to a path on the server.</summary>
    public static ProcResult Upload(Server s, string localFile, string remotePath, int timeoutSec = 900, CancellationToken ct = default)
    {
        // The file is passed by name from its own folder: scp reads "C:\dir\file" as host "C".
        var full = Path.GetFullPath(localFile);
        var a = ScpOptions(s);
        a.Add(Path.GetFileName(full));
        a.Add($"{s.User}@{s.Host}:{remotePath}");
        return ProcessRunner.Run("scp", a, timeoutSec, Path.GetDirectoryName(full), ct: ct);
    }

    private static string Q(string x) => "'" + x.Replace("'", "''") + "'";

    /// <summary>
    /// Línea para abrir una terminal que ejecuta ssh (con <paramref name="command"/>, o sesión interactiva si está vacío)
    /// y, al terminar, se queda en PowerShell.
    /// </summary>
    public static string TerminalLine(Server s, string command)
    {
        var parts = new List<string> { "&", "ssh.exe", "-t" };
        parts.AddRange(Options(s, interactive: true).Select(Q));
        if (!string.IsNullOrWhiteSpace(command)) parts.Add(Q(command));
        var cmd = string.Join(' ', parts).Replace("\"", "\\\"");
        return $"\"{Shell.PowerShellExe}\" -NoLogo -NoExit -Command \"{cmd}\"";
    }

    // ------------------------------------------------------------ Permisos de la clave (Windows)

    /// <summary>SSH rechaza una clave que otros usuarios pueden leer. En Windows se mira con icacls.</summary>
    public static bool KeyPermissionsOk(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return true;
        var r = ProcessRunner.Run("icacls", new[] { path }, 10);
        return !Regex.IsMatch(r.Output, @"Authenticated Users|BUILTIN\\Users|Everyone|Usuarios autenticados|Todos");
    }

    private static bool Restrict(string path)
    {
        var me = $"{Environment.UserDomainName}\\{Environment.UserName}";
        ProcessRunner.Run("icacls", new[] { path, "/inheritance:r" }, 10);
        ProcessRunner.Run("icacls", new[] { path, "/grant:r", $"{me}:(R)" }, 10);
        foreach (var sid in new[] { "*S-1-5-11", "*S-1-5-32-545", "*S-1-1-0" })
            ProcessRunner.Run("icacls", new[] { path, "/remove", sid }, 10);
        return KeyPermissionsOk(path);
    }

    /// <summary>
    /// Deja la clave legible solo por ti. En discos exFAT/externos no se puede: entonces copia la clave a ~\.ssh.
    /// Devuelve la ruta que hay que usar, o null si no se logró.
    /// </summary>
    public static string? FixKey(string path)
    {
        if (Restrict(path)) return path;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
            Directory.CreateDirectory(dir);
            var copy = Path.Combine(dir, Path.GetFileName(path));
            File.Copy(path, copy, overwrite: true);
            return Restrict(copy) ? copy : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
