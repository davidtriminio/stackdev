using StackDev.Core.Servers;
using StackDev.Core.Ui;

namespace StackDev.Core.Deploy;

public enum CheckLevel { Ok, Warn, Error }

/// <summary>What StackDev can install by itself.</summary>
public enum CheckFix { None, WebApache, WebNginx, Certbot, Rsync, Runtime }

/// <summary>One line of the server check: what was looked at, what was found and, when there is one, how to fix it.</summary>
public sealed record CheckItem(string Id, CheckLevel Level, string Title, string Detail, CheckFix Fix = CheckFix.None, string? Manual = null);

/// <summary>
/// Turns what the server reported (preflight) into a short checklist for creating a new API, so the user sees what is missing
/// before anything is changed. Pure: the caller does the asking (ssh, DNS) and passes the answers in.
/// </summary>
public static class ServerReadiness
{
    public sealed record Input(
        IReadOnlyDictionary<string, string> Facts,
        string WantedWeb,            // "apache" or "nginx": what the server entry says
        int? RuntimeMajor,           // what the project needs
        string? Domain,
        string? DnsProblem,          // null when the domain points at the server
        bool WantHttps);

    public static string Fact(IReadOnlyDictionary<string, string> facts, string key) => facts.TryGetValue(key, out var v) ? v : "";

    /// <summary>The web server to use: the one installed, else the one the user chose.</summary>
    public static string RecommendedWeb(IReadOnlyDictionary<string, string> facts, string wanted)
    {
        var installed = Fact(facts, "web");
        return installed is "apache" or "nginx" ? installed : (string.Equals(wanted, "nginx", StringComparison.OrdinalIgnoreCase) ? "nginx" : "apache");
    }

    public static List<CheckItem> Evaluate(Input i)
    {
        var f = i.Facts;
        var list = new List<CheckItem>();
        var os = Fact(f, "os");
        list.Add(new("connection", CheckLevel.Ok, "Conexión y permisos", string.IsNullOrEmpty(os) ? "Conectado con permisos de administrador (sudo)." : $"Conectado a {os} con permisos de administrador (sudo)."));

        // web server
        var installed = Fact(f, "web");
        var wantedLabel = i.WantedWeb.Equals("nginx", StringComparison.OrdinalIgnoreCase) ? "Nginx" : "Apache";
        if (installed is "apache" or "nginx")
        {
            var name = installed == "nginx" ? "Nginx" : "Apache";
            var detail = installed.Equals(i.WantedWeb, StringComparison.OrdinalIgnoreCase)
                ? $"{name} está instalado."
                : $"El servidor tiene {name} (tu entrada decía {wantedLabel}); se usará {name}.";
            list.Add(new("web", CheckLevel.Ok, "Servidor web (proxy)", detail));
        }
        else
        {
            list.Add(new("web", CheckLevel.Error, "Servidor web (proxy)", $"No hay Apache ni Nginx instalado. Hace falta uno para publicar la API con su dominio.",
                wantedLabel == "Nginx" ? CheckFix.WebNginx : CheckFix.WebApache,
                wantedLabel == "Nginx" ? "sudo dnf install -y nginx   (o: sudo apt-get install -y nginx)" : "sudo dnf install -y httpd mod_ssl   (o: sudo apt-get install -y apache2)"));
        }

        // runtime
        var runtimes = Fact(f, "runtimes").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => int.TryParse(r, out var n) ? n : -1).Where(n => n > 0).ToList();
        if (i.RuntimeMajor is int need)
        {
            if (runtimes.Contains(need))
                list.Add(new("runtime", CheckLevel.Ok, $"ASP.NET Core {need}", "El runtime que necesita tu API está instalado."));
            else
            {
                var have = runtimes.Count > 0 ? $" (hay {string.Join(", ", runtimes)})" : " (no hay ninguno)";
                list.Add(new("runtime", CheckLevel.Error, $"ASP.NET Core {need}", $"Tu API necesita el runtime {need} y el servidor no lo tiene{have}.", CheckFix.Runtime,
                    $"sudo dnf install -y aspnetcore-runtime-{need}.0   (o: sudo apt-get install -y aspnetcore-runtime-{need}.0). Si tu distribución no lo trae: https://learn.microsoft.com/dotnet/core/install/linux"));
            }
        }
        else
        {
            list.Add(runtimes.Count > 0
                ? new CheckItem("runtime", CheckLevel.Ok, "ASP.NET Core", $"Runtimes instalados: {string.Join(", ", runtimes)}.")
                : new CheckItem("runtime", CheckLevel.Warn, "ASP.NET Core", "No encontré ningún runtime de ASP.NET Core en el servidor. Se necesitará antes de la primera subida."));
        }

        // https
        if (i.WantHttps)
        {
            if (Fact(f, "certbot") == "yes") list.Add(new("certbot", CheckLevel.Ok, "Certificado HTTPS", "certbot está instalado (Let's Encrypt)."));
            else list.Add(new("certbot", CheckLevel.Warn, "Certificado HTTPS", "certbot no está instalado, así que el certificado no se podría pedir.", CheckFix.Certbot,
                "sudo dnf install -y certbot python3-certbot-apache   (o: sudo apt-get install -y certbot python3-certbot-apache)"));
        }

        // DNS
        if (string.IsNullOrWhiteSpace(i.Domain))
            list.Add(new("dns", CheckLevel.Warn, "Dominio", "Aún no hay dominio. Lo escribes en el formulario; sin dominio no se puede publicar la API ni pedir HTTPS."));
        else if (i.DnsProblem is null)
            list.Add(new("dns", CheckLevel.Ok, "Dominio", $"{i.Domain} apunta a este servidor."));
        else
            list.Add(new("dns", CheckLevel.Warn, "Dominio", i.DnsProblem));

        // rsync, disk, name
        if (Fact(f, "rsync") != "yes")
            list.Add(new("rsync", CheckLevel.Warn, "rsync", "No está instalado. Las subidas funcionan igual (más lentas en proyectos grandes).", CheckFix.Rsync, "sudo dnf install -y rsync   (o: sudo apt-get install -y rsync)"));
        if (int.TryParse(Fact(f, "free_mb"), out var free))
        {
            if (free < 200) list.Add(new("disk", CheckLevel.Error, "Espacio en disco", $"Solo quedan {free} MB libres: no alcanza para instalar y guardar copias."));
            else if (free < 1000) list.Add(new("disk", CheckLevel.Warn, "Espacio en disco", $"Quedan {free} MB libres; es poco para varias versiones guardadas."));
            else list.Add(new("disk", CheckLevel.Ok, "Espacio en disco", $"{free} MB libres."));
        }
        if (Fact(f, "service_exists") == "yes")
            list.Add(new("exists", CheckLevel.Warn, "Nombre del servicio", "Ya existe un servicio con ese nombre. Elige otro, o usa «Subir API» si es el mismo."));
        return list;
    }

    /// <summary>The <c>--install</c> items for everything fixable that is not fine yet.</summary>
    public static List<CheckFix> Fixable(IEnumerable<CheckItem> items, bool errorsOnly = false) =>
        items.Where(c => c.Fix != CheckFix.None && c.Level != CheckLevel.Ok && (!errorsOnly || c.Level == CheckLevel.Error)).Select(c => c.Fix).Distinct().ToList();

    /// <summary>True when nothing blocks creating the API.</summary>
    public static bool CanContinue(IEnumerable<CheckItem> items) => items.All(c => c.Level != CheckLevel.Error);

    public static string InstallList(IEnumerable<CheckFix> fixes) => string.Join(',', fixes.Select(f => f switch
    {
        CheckFix.WebApache => "web-apache",
        CheckFix.WebNginx => "web-nginx",
        CheckFix.Certbot => "certbot",
        CheckFix.Rsync => "rsync",
        CheckFix.Runtime => "runtime",
        _ => "",
    }).Where(x => x.Length > 0));
}
