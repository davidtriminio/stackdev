using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace StackDev.Core.Servers;

/// <param name="Fingerprint">Set when the server is not trusted yet: the fingerprint the user has to confirm.</param>
public sealed record SshResult(bool Ok, string Message, HostKeyInfo? Untrusted = null);

public sealed record ApiStatus(bool Active, string State, string From, string Errors, int? Http, DateTime? CertExpires, string? Problem);

/// <summary>Queries and actions on a server over SSH: test the connection, service status, restart.</summary>
public static class ServerService
{
    public static readonly Regex ServiceName = Deploy.Sh.Service;

    /// <summary>Leaves only the host: strips http(s)://, paths and the trailing slash.</summary>
    public static string? CleanDomain(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var d = Regex.Replace(s.Trim(), @"^(https?:?/*)+", "", RegexOptions.IgnoreCase);
        d = Regex.Split(d, @"[/?#]")[0].Trim().ToLowerInvariant();
        return d.Length == 0 ? null : d;
    }

    private static string ExplainFailure(Proc.ProcResult r, Server s)
    {
        if (r.ErrorHome is not null) return "No encuentro ssh en este equipo (instala «Cliente OpenSSH» en las características de Windows).";
        if (r.TimeExpired) return "El servidor no respondió a tiempo: revisa la IP, que esté encendido y que el puerto 22 esté abierto.";
        var e = r.Error.Trim();
        if (e.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase) || e.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase))
            return "La huella del servidor no coincide con la que confirmaste. Puede ser una reinstalación del servidor o un ataque: no se conectó. Si estás seguro, quita la huella guardada y verifícala de nuevo.";
        if (e.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) return "El servidor rechazó la clave o el usuario. Revisa el usuario y que la clave sea la correcta.";
        if (e.Contains("UNPROTECTED PRIVATE KEY", StringComparison.OrdinalIgnoreCase) || e.Contains("bad permissions", StringComparison.OrdinalIgnoreCase))
            return "La clave tiene permisos demasiado abiertos (se puede corregir desde aquí).";
        if (e.Contains("Could not resolve", StringComparison.OrdinalIgnoreCase)) return "No se pudo resolver el nombre del servidor: revisa el host.";
        if (e.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)) return "Conexión rechazada: ¿el puerto SSH es otro, o SSH no está activo en el servidor?";
        if (e.Contains("No such file", StringComparison.OrdinalIgnoreCase)) return "No existe el archivo de la clave: " + s.Key;
        return string.IsNullOrWhiteSpace(e) ? $"Falló (código {r.Code})." : e.Split('\n')[0];
    }

    public static SshResult Test(Server s)
    {
        if (string.IsNullOrWhiteSpace(s.Host)) return new(false, "Falta el host.");
        if (!string.IsNullOrWhiteSpace(s.Key) && !File.Exists(s.Key)) return new(false, "No existe la clave: " + s.Key);
        if (!HostKeys.IsTrusted(s))
        {
            var key = HostKeys.Scan(s, out var why);
            return key is null
                ? new(false, "No pude leer la huella del servidor: revisa la IP, el puerto y que esté encendido." + (string.IsNullOrWhiteSpace(why) ? "" : "\nDetalle: " + why))
                : new(false, "Servidor sin verificar: confirma su huella para poder conectar.", key);
        }
        var r = Ssh.Run(s, "echo stackdev-ok; uname -sr; if command -v httpd >/dev/null 2>&1; then echo web=apache; elif command -v nginx >/dev/null 2>&1; then echo web=nginx; else echo web=ninguno; fi; sudo -n true 2>/dev/null && echo sudo=ok || echo sudo=password", 25);
        if (!r.Ok || !r.Output.Contains("stackdev-ok")) return new(false, ExplainFailure(r, s));
        var lines = r.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var os = lines.Length > 1 ? lines[1] : "";
        var web = lines.FirstOrDefault(l => l.StartsWith("web="))?[4..] ?? "?";
        var sudo = lines.Any(l => l == "sudo=ok") ? "sudo sin contraseña" : "sudo pide contraseña (los comandos desde aquí fallarán)";
        return new(true, $"Conectado · {os} · web: {web} · {sudo}");
    }

    private static string Safe(string svc)
    {
        if (!ServiceName.IsMatch(svc)) throw new ArgumentException("Nombre de servicio no válido: " + svc);
        return svc;
    }

    public static ApiStatus State(Server s, string service, string? domain)
    {
        var svc = Safe(service);
        if (!HostKeys.IsTrusted(s))
            return new(false, "sin verificar", "", "", null, null, "Servidor sin verificar: usa «Verificar y probar» en su tarjeta para confirmar su huella.");
        var cmd = $"systemctl is-active {svc} 2>&1; systemctl show {svc} -p ActiveEnterTimestamp --value 2>&1; echo '---'; sudo -n journalctl -u {svc} -p err -n 5 --no-pager 2>&1";
        var r = Ssh.Run(s, cmd, 25);
        if (r.ErrorHome is not null || r.TimeExpired || (r.Code == 255))
            return new(false, "sin conexión", "", "", null, null, ExplainFailure(r, s));

        var parts = r.Output.Replace("\r", "").Split("\n---\n", 2);
        var cab = parts[0].Split('\n', StringSplitOptions.TrimEntries);
        var state = cab.Length > 0 ? cab[0] : "?";
        var since = cab.Length > 1 ? cab[1] : "";
        var errors = parts.Length > 1 ? parts[1].Trim() : "";
        if (errors.Contains("No entries", StringComparison.OrdinalIgnoreCase)) errors = "";

        int? http = null; DateTime? expires = null;
        if (CleanDomain(domain) is { } d) (http, expires) = CheckHttps(d);
        return new(state == "active", state, since, errors, http, expires, null);
    }

    private static (int? Code, DateTime? Vence) CheckHttps(string domain)
    {
        X509Certificate2? cert = null;
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, c, _, _) => { if (c is not null) cert = new X509Certificate2(c); return true; },
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            var resp = http.GetAsync($"https://{domain}/").GetAwaiter().GetResult();
            return ((int)resp.StatusCode, cert?.NotAfter);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return (null, cert?.NotAfter);
        }
    }

    /// <summary>start, stop or restart of a systemd service.</summary>
    public static SshResult Operation(Server s, string service, string action)
    {
        if (action is not ("start" or "stop" or "restart")) return new(false, "Acción no válida.");
        var svc = Safe(service);
        if (!HostKeys.IsTrusted(s)) return new(false, "Servidor sin verificar: usa «Verificar y probar» en su tarjeta.");
        var r = Ssh.Run(s, $"sudo -n systemctl {action} {svc} && systemctl is-active {svc}", 40);
        if (r.Ok) return new(true, $"{action}: {r.Output.Trim()}");
        return new(false, string.IsNullOrWhiteSpace(r.Error) ? ExplainFailure(r, s) : r.Error.Trim());
    }
}
