using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace StackDev.Core.Servidores;

public sealed record ResultadoSsh(bool Ok, string Mensaje);

public sealed record EstadoApi(bool Activo, string Estado, string Desde, string Errores, int? Http, DateTime? CertVence, string? Problema);

/// <summary>Consultas y acciones sobre un servidor por SSH: probar la conexión, estado de un servicio, reiniciar.</summary>
public static class ServidorService
{
    public static readonly Regex NombreServicio = new("^[a-z][a-z0-9-]*$");

    /// <summary>Deja solo el host: quita http(s)://, rutas y la barra final.</summary>
    public static string? LimpiarDominio(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var d = Regex.Replace(s.Trim(), @"^(https?:?/*)+", "", RegexOptions.IgnoreCase);
        d = Regex.Split(d, @"[/?#]")[0].Trim().ToLowerInvariant();
        return d.Length == 0 ? null : d;
    }

    private static string ExplicarFallo(Proc.ProcResult r, Servidor s)
    {
        if (r.ErrorInicio is not null) return "No encuentro ssh en este equipo (instala «Cliente OpenSSH» en las características de Windows).";
        if (r.TiempoAgotado) return "El servidor no respondió a tiempo: revisa la IP, que esté encendido y que el puerto 22 esté abierto.";
        var e = r.Error.Trim();
        if (e.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) return "El servidor rechazó la clave o el usuario. Revisa el usuario y que la clave sea la correcta.";
        if (e.Contains("UNPROTECTED PRIVATE KEY", StringComparison.OrdinalIgnoreCase) || e.Contains("bad permissions", StringComparison.OrdinalIgnoreCase))
            return "La clave tiene permisos demasiado abiertos (se puede corregir desde aquí).";
        if (e.Contains("Could not resolve", StringComparison.OrdinalIgnoreCase)) return "No se pudo resolver el nombre del servidor: revisa el host.";
        if (e.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)) return "Conexión rechazada: ¿el puerto SSH es otro, o SSH no está activo en el servidor?";
        if (e.Contains("No such file", StringComparison.OrdinalIgnoreCase)) return "No existe el archivo de la clave: " + s.Clave;
        return string.IsNullOrWhiteSpace(e) ? $"Falló (código {r.Codigo})." : e.Split('\n')[0];
    }

    public static ResultadoSsh Probar(Servidor s)
    {
        if (string.IsNullOrWhiteSpace(s.Host)) return new(false, "Falta el host.");
        if (!string.IsNullOrWhiteSpace(s.Clave) && !File.Exists(s.Clave)) return new(false, "No existe la clave: " + s.Clave);
        var r = Ssh.Ejecutar(s, "echo stackdev-ok; uname -sr; if command -v httpd >/dev/null 2>&1; then echo web=apache; elif command -v nginx >/dev/null 2>&1; then echo web=nginx; else echo web=ninguno; fi; sudo -n true 2>/dev/null && echo sudo=ok || echo sudo=password", 25);
        if (!r.Ok || !r.Salida.Contains("stackdev-ok")) return new(false, ExplicarFallo(r, s));
        var lineas = r.Salida.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var so = lineas.Length > 1 ? lineas[1] : "";
        var web = lineas.FirstOrDefault(l => l.StartsWith("web="))?[4..] ?? "?";
        var sudo = lineas.Any(l => l == "sudo=ok") ? "sudo sin contraseña" : "sudo pide contraseña (los comandos desde aquí fallarán)";
        return new(true, $"Conectado · {so} · web: {web} · {sudo}");
    }

    private static string Seguro(string svc)
    {
        if (!NombreServicio.IsMatch(svc)) throw new ArgumentException("Nombre de servicio no válido: " + svc);
        return svc;
    }

    public static EstadoApi Estado(Servidor s, string servicio, string? dominio)
    {
        var svc = Seguro(servicio);
        var cmd = $"systemctl is-active {svc} 2>&1; systemctl show {svc} -p ActiveEnterTimestamp --value 2>&1; echo '---'; sudo -n journalctl -u {svc} -p err -n 5 --no-pager 2>&1";
        var r = Ssh.Ejecutar(s, cmd, 25);
        if (r.ErrorInicio is not null || r.TiempoAgotado || (r.Codigo == 255))
            return new(false, "sin conexión", "", "", null, null, ExplicarFallo(r, s));

        var partes = r.Salida.Replace("\r", "").Split("\n---\n", 2);
        var cab = partes[0].Split('\n', StringSplitOptions.TrimEntries);
        var estado = cab.Length > 0 ? cab[0] : "?";
        var desde = cab.Length > 1 ? cab[1] : "";
        var errores = partes.Length > 1 ? partes[1].Trim() : "";
        if (errores.Contains("No entries", StringComparison.OrdinalIgnoreCase)) errores = "";

        int? http = null; DateTime? vence = null;
        if (LimpiarDominio(dominio) is { } d) (http, vence) = ComprobarHttps(d);
        return new(estado == "active", estado, desde, errores, http, vence, null);
    }

    private static (int? Codigo, DateTime? Vence) ComprobarHttps(string dominio)
    {
        X509Certificate2? cert = null;
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, c, _, _) => { if (c is not null) cert = new X509Certificate2(c); return true; },
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            var resp = http.GetAsync($"https://{dominio}/").GetAwaiter().GetResult();
            return ((int)resp.StatusCode, cert?.NotAfter);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return (null, cert?.NotAfter);
        }
    }

    /// <summary>start, stop o restart de un servicio systemd.</summary>
    public static ResultadoSsh Accion(Servidor s, string servicio, string accion)
    {
        if (accion is not ("start" or "stop" or "restart")) return new(false, "Acción no válida.");
        var svc = Seguro(servicio);
        var r = Ssh.Ejecutar(s, $"sudo -n systemctl {accion} {svc} && systemctl is-active {svc}", 40);
        if (r.Ok) return new(true, $"{accion}: {r.Salida.Trim()}");
        return new(false, string.IsNullOrWhiteSpace(r.Error) ? ExplicarFallo(r, s) : r.Error.Trim());
    }
}
