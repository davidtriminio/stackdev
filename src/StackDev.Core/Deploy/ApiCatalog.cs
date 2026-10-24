using StackDev.Core.Config;
using StackDev.Core.Detection;
using StackDev.Core.Proc;
using StackDev.Core.Servers;

namespace StackDev.Core.Deploy;

public enum ApiScope { Local, Server }

/// <summary>One address of the project: where it lives, whether it is up, and a line about it.</summary>
public sealed record ApiEntry(ApiScope Scope, string Name, string? Url, bool? Running, string Detail,
    ProjectComponent? Component = null, bool Configured = true, string? Suggestion = null, bool NeedsFolder = false);

/// <summary>The addresses of a project in one list: the APIs and web apps on this computer, and the API on the server.</summary>
public static class ApiCatalog
{
    /// <param name="detected">What the folder looks like (a .NET API, an Angular web), to offer it when nothing is configured yet.</param>
    public static List<ApiEntry> Build(ProjectContext ctx, Server? server, DeploymentConfig? deployment, ApiStatus? status, DetectionResult? detected = null)
    {
        var list = new List<ApiEntry>();
        foreach (var (component, name) in new[] { (ProjectComponent.Api, "API local"), (ProjectComponent.Web, "Web local") })
        {
            var guess = component == ProjectComponent.Api ? detected?.Config.Api?.Url : detected?.Config.Web?.Url;
            if (!ctx.Has(component))
            {
                // only offer a web app when one was found; the API slot is always offered
                if (component == ProjectComponent.Web && guess is null) continue;
                list.Add(new(ApiScope.Local, name, null, null,
                    guess is null ? "No encontré una API en esta carpeta. Dime dónde está y cómo se inicia." : "Encontré una en el proyecto pero todavía no está guardada.",
                    component, Configured: false, Suggestion: guess, NeedsFolder: guess is null));
                continue;
            }
            var url = ctx.Url(component);
            if (string.IsNullOrWhiteSpace(url))
            {
                list.Add(new(ApiScope.Local, name, null, null, "Falta la dirección (URL) con la que se abre.", component, Configured: false, Suggestion: guess ?? (component == ProjectComponent.Api ? "http://localhost:5000" : "http://localhost:4200")));
                continue;
            }
            var port = ctx.Port(component);
            var running = port > 0 && Ports.IsListening(port);
            list.Add(new(ApiScope.Local, name, url, running,
                running ? $"corriendo en el puerto {port} de este equipo" : "detenida: arráncala desde la sección del proyecto", component));
        }
        if (server is not null && deployment is not null && !string.IsNullOrEmpty(deployment.Service))
        {
            var url = string.IsNullOrEmpty(deployment.Domain) ? null : "https://" + deployment.Domain;
            bool? running = status is null || status.Problem is not null ? null : status.Active;
            var detail = status is null ? "sin consultar todavía"
                : status.Problem is not null ? status.Problem
                : $"{(status.Active ? "activa" : "detenida")} en {server.Name} ({server.Host})"
                  + (status.Http is int h ? $", HTTPS {h}" : "");
            list.Add(new(ApiScope.Server, $"API en el servidor · {deployment.Service}", url, running, detail, Configured: url is not null));
        }
        else
        {
            list.Add(new(ApiScope.Server, "API en el servidor", null, null, "Este proyecto todavía no sube a ningún servidor.", Configured: false));
        }
        return list;
    }

    /// <summary>Why <paramref name="url"/> cannot be saved as the address of an API, or null when it is fine.</summary>
    public static string? CheckUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "Escribe la dirección, por ejemplo http://localhost:5000.";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || string.IsNullOrEmpty(u.Host))
            return "La dirección debe empezar con http:// o https:// (por ejemplo http://localhost:5000).";
        if (u.Port is < 1 or > 65535) return "El puerto no es válido.";
        return null;
    }

    /// <summary>
    /// A copy of <paramref name="config"/> with the local API or web defined: what was detected is used as the base,
    /// then the address (and, when nothing was detected, the folder and the start command) from the form.
    /// </summary>
    public static ProjectConfig ApplyLocal(ProjectConfig config, ProjectComponent component, string url, DetectionResult? detected, string? folder = null, string? start = null)
    {
        var copy = ConfigJson.Read(ConfigJson.Write(config));
        url = url.Trim().TrimEnd('/');
        if (component == ProjectComponent.Api)
        {
            var api = copy.Api ?? CloneApi(detected?.Config.Api) ?? new ApiConfig();
            api.Url = url;
            if (!string.IsNullOrWhiteSpace(folder)) api.Folder = folder.Trim();
            if (!string.IsNullOrWhiteSpace(start)) api.Start = start.Trim();
            copy.Api = api;
        }
        else if (component == ProjectComponent.Web)
        {
            var web = copy.Web ?? CloneWeb(detected?.Config.Web) ?? new WebConfig();
            web.Url = url;
            if (!string.IsNullOrWhiteSpace(folder)) web.Folder = folder.Trim();
            if (!string.IsNullOrWhiteSpace(start)) web.Start = start.Trim();
            copy.Web = web;
        }
        return copy;
    }

    private static ApiConfig? CloneApi(ApiConfig? a) => a is null ? null : ConfigJson.Read(ConfigJson.Write(new ProjectConfig { Api = a })).Api;
    private static WebConfig? CloneWeb(WebConfig? w) => w is null ? null : ConfigJson.Read(ConfigJson.Write(new ProjectConfig { Web = w })).Web;

    /// <summary>The probe result as plain lines: what answers, with its status and time.</summary>
    public static IReadOnlyList<string> Describe(IReadOnlyList<ApiEndpoint> found)
    {
        if (found.Count == 0) return new[] { "✘ No responde. Revisa que esté corriendo y, en el servidor, el certificado HTTPS y el DNS." };
        return found.Select(e => $"{(e.Status is >= 200 and < 400 ? "✔" : e.Protected ? "🔒" : "⚠")} {e.Path}   {e.Status}   {e.Ms} ms   {e.Label}" + (e.Protected ? " (pide acceso)" : ""))
            .Append(found.Any(e => e.Kind == EndpointKind.Docs) ? "" : "Documentación: esta API no la publica aquí.")
            .Where(l => l.Length > 0).ToList();
    }
}
