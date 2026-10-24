using System.Text.RegularExpressions;
using StackDev.Core.Deploy;

namespace StackDev.Core.Servers;

/// <summary>Which field of a form is wrong and what to tell the user.</summary>
public sealed record FieldError(string Field, string Message);

/// <summary>
/// Checks what the user types for a server and for the deployment of a project. The values end up in ssh and
/// shell command lines, so besides being friendly the rules also keep out anything that could be read as an option.
/// </summary>
public static class ServerValidation
{
    public const int NameMax = 40;
    public const int HostMax = 253;
    public const int UserMax = 32;

    private static readonly Regex HostName = new(@"^[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$");
    private static readonly Regex Ipv4Shape = new(@"^[0-9.]+$");
    private static readonly Regex Ipv6 = new(@"^[0-9A-Fa-f:]{2,45}$");
    private static readonly Regex UserName = new(@"^[A-Za-z_][A-Za-z0-9_.-]{0,31}$");

    /// <summary>Removes an accidental ssh://, user@ prefix, path or trailing spaces/slashes around a host.</summary>
    public static string CleanHost(string? text)
    {
        var h = (text ?? "").Trim();
        h = Regex.Replace(h, @"^[a-z][a-z0-9+.-]*://", "", RegexOptions.IgnoreCase);
        var cut = h.IndexOfAny(new[] { '/', '?', '#' });
        if (cut >= 0) h = h[..cut];
        return h.Trim();
    }

    public static bool ValidIpv4(string text)
    {
        var parts = text.Split('.');
        return parts.Length == 4 && parts.All(p => p.Length is >= 1 and <= 3 && p.All(char.IsAsciiDigit) && int.Parse(p) <= 255);
    }

    public static FieldError? CheckHost(string host)
    {
        if (host.Length == 0) return new("host", "Escribe la IP o el dominio del servidor (ej. 132.145.10.20 o api.midominio.com).");
        if (host.Length > HostMax) return new("host", $"El host es demasiado largo (máximo {HostMax} caracteres).");
        if (host.Contains('@')) return new("host", "Aquí va solo la IP o el dominio. El usuario se escribe en su propio campo.");
        if (host.Contains(':') && !Ipv6.IsMatch(host)) return new("host", "No escribas el puerto aquí: tiene su propio campo.");
        if (host.Any(char.IsWhiteSpace)) return new("host", "El host no puede tener espacios.");
        if (Ipv6.IsMatch(host) && host.Count(c => c == ':') >= 2) return null;
        if (Ipv4Shape.IsMatch(host)) return ValidIpv4(host) ? null : new("host", "La IP no es válida: son 4 números del 0 al 255 separados por puntos.");
        if (!HostName.IsMatch(host)) return new("host", "El host solo puede tener letras, números, puntos y guiones (ej. api.midominio.com).");
        return null;
    }

    public static FieldError? CheckPort(string text, out int port)
    {
        port = 0;
        var t = text.Trim();
        if (t.Length == 0) return new("port", "Escribe el puerto SSH (normalmente 22).");
        if (!t.All(char.IsAsciiDigit)) return new("port", "El puerto solo puede tener números.");
        if (t.Length > 5 || !int.TryParse(t, out port) || port is < 1 or > 65535) return new("port", "El puerto debe estar entre 1 y 65535 (el SSH normal es 22).");
        return null;
    }

    /// <summary>First problem found in a server form, or null. <paramref name="port"/> is set when the port is fine.</summary>
    public static FieldError? CheckServer(string name, string host, string user, string portText, string key, out int port)
    {
        port = 22;
        var n = name.Trim();
        if (n.Length == 0) return new("name", "Ponle un nombre para reconocerlo (ej. Oracle producción).");
        if (n.Length > NameMax) return new("name", $"El nombre es demasiado largo (máximo {NameMax} caracteres).");
        if (n.Any(char.IsControl)) return new("name", "El nombre tiene caracteres no válidos.");
        if (CheckHost(host) is { } h) return h;
        var u = user.Trim();
        if (u.Length == 0) return new("user", "Escribe el usuario SSH (ej. opc, ubuntu, ec2-user).");
        if (!UserName.IsMatch(u)) return new("user", $"El usuario solo puede tener letras, números, punto, guion y guion bajo (máximo {UserMax}).");
        if (CheckPort(portText, out port) is { } p) return p;
        return CheckKey(key);
    }

    public static FieldError? CheckKey(string key)
    {
        var k = key.Trim().Trim('"');
        if (k.Length == 0) return null;   // ssh may use the agent or the default key
        if (k.Contains('\n') || k.Contains('\r')) return new("key", "La ruta de la clave no es válida.");
        if (k.EndsWith(".pub", StringComparison.OrdinalIgnoreCase)) return new("key", "Ese es el archivo público (.pub). Elige el privado, el que tiene el mismo nombre sin .pub.");
        if (!File.Exists(k)) return new("key", "No encuentro ese archivo de clave. Usa «Examinar…» para elegirlo.");
        return null;
    }

    /// <summary>Checks the deployment form (service, dll and public domain).</summary>
    public static FieldError? CheckDeployment(string service, string dll, string domainText)
    {
        var s = service.Trim();
        if (s.Length == 0) return new("service", "Escribe el nombre del servicio (ej. mi-api).");
        if (!Sh.Service.IsMatch(s))
            return new("service", s.Length > 40 ? "El servicio puede tener como máximo 40 caracteres."
                : "El servicio empieza con una letra minúscula y usa solo minúsculas, números y guiones (ej. mi-api).");
        var d = dll.Trim();
        if (d.Length > 0 && !Sh.Dll.IsMatch(d)) return new("dll", "El archivo debe terminar en .dll y empezar con una letra (ej. MiApi.dll).");
        if (!string.IsNullOrWhiteSpace(domainText))
        {
            var dom = ServerService.CleanDomain(domainText);
            if (dom is null || !Sh.Domain.IsMatch(dom)) return new("domain", "El dominio no es válido (ej. api.midominio.com, sin https://).");
        }
        return null;
    }
}
