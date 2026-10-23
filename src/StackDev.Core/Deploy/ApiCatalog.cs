using StackDev.Core.Config;
using StackDev.Core.Proc;
using StackDev.Core.Servers;

namespace StackDev.Core.Deploy;

public enum ApiScope { Local, Server }

/// <summary>One address of the project: where it lives, whether it is up, and a line about it.</summary>
public sealed record ApiEntry(ApiScope Scope, string Name, string? Url, bool? Running, string Detail);

/// <summary>The addresses of a project in one list: the APIs and web apps on this computer, and the API on the server.</summary>
public static class ApiCatalog
{
    public static List<ApiEntry> Build(ProjectContext ctx, Server? server, DeploymentConfig? deployment, ApiStatus? status)
    {
        var list = new List<ApiEntry>();
        foreach (var (component, name) in new[] { (ProjectComponent.Api, "API local"), (ProjectComponent.Web, "Web local") })
        {
            if (!ctx.Has(component)) continue;
            var url = ctx.Url(component);
            var port = ctx.Port(component);
            var running = port > 0 && Ports.IsListening(port);
            list.Add(new(ApiScope.Local, name, url, running,
                running ? $"corriendo en el puerto {port} de este equipo" : "detenida: arráncala desde la sección del proyecto"));
        }
        if (server is not null && deployment is not null && !string.IsNullOrEmpty(deployment.Service))
        {
            var url = string.IsNullOrEmpty(deployment.Domain) ? null : "https://" + deployment.Domain;
            bool? running = status is null || status.Problem is not null ? null : status.Active;
            var detail = status is null ? "sin consultar todavía"
                : status.Problem is not null ? status.Problem
                : $"{(status.Active ? "activa" : "detenida")} en {server.Name} ({server.Host})"
                  + (status.Http is int h ? $", HTTPS {h}" : "");
            list.Add(new(ApiScope.Server, $"API en el servidor · {deployment.Service}", url, running, detail));
        }
        return list;
    }

    /// <summary>The probe result as plain lines: what answers, with its status and time.</summary>
    public static IReadOnlyList<string> Describe(IReadOnlyList<ApiEndpoint> found)
    {
        if (found.Count == 0) return new[] { "✘ No responde. Revisa que esté corriendo y, en el servidor, el certificado HTTPS y el DNS." };
        return found.Select(e => $"{(e.Status is >= 200 and < 400 ? "✔" : e.Protected ? "🔒" : "⚠")} {e.Path}   {e.Status}   {e.Ms} ms   {e.Label}" + (e.Protected ? " (pide acceso)" : ""))
            .Append(found.Any(e => e.Kind == EndpointKind.Docs) ? "" : "Documentación: esta API no la publica aquí.")
            .Where(l => l.Length > 0).ToList();
    }
}
