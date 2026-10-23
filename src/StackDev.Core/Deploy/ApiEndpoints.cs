using System.Net;

namespace StackDev.Core.Deploy;

public enum EndpointKind { Docs, Health, Home }

/// <summary>A page a deployed API answers on.</summary>
public sealed record ApiEndpoint(EndpointKind Kind, string Label, string Path, int Status, int Ms = 0)
{
    public bool Protected => Status is 401 or 403;
}

/// <summary>
/// Finds out what a running API shows in a browser instead of assuming a layout: documentation pages (Swagger, Scalar, ReDoc, OpenAPI file),
/// health pages and the home page. Works for any kind of project because it only looks at what answers.
/// </summary>
public static class ApiEndpoints
{
    private static readonly (EndpointKind Kind, string Label, string Path)[] Candidates =
    {
        (EndpointKind.Docs, "Documentación (Swagger)", "/swagger"),
        (EndpointKind.Docs, "Documentación (Swagger)", "/swagger/index.html"),
        (EndpointKind.Docs, "Documentación (Scalar)", "/scalar"),
        (EndpointKind.Docs, "Documentación (Scalar)", "/scalar/v1"),
        (EndpointKind.Docs, "Documentación (ReDoc)", "/redoc"),
        (EndpointKind.Docs, "Documentación", "/docs"),
        (EndpointKind.Docs, "Especificación OpenAPI", "/openapi/v1.json"),
        (EndpointKind.Docs, "Especificación OpenAPI", "/swagger/v1/swagger.json"),
        (EndpointKind.Health, "Estado de salud", "/health"),
        (EndpointKind.Health, "Estado de salud (listo)", "/health/ready"),
        (EndpointKind.Health, "Estado de salud (vivo)", "/health/live"),
        (EndpointKind.Health, "Estado de salud", "/healthz"),
        (EndpointKind.Health, "Estado de salud", "/api/health"),
    };

    /// <summary>The pages that answer at <paramref name="baseUrl"/>, home first. Pages that answer for any path (a web app) are not listed twice.</summary>
    public static async Task<IReadOnlyList<ApiEndpoint>> ProbeAsync(string baseUrl, HttpClient? client = null, CancellationToken ct = default)
    {
        var own = client is null;
        client ??= new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            baseUrl = baseUrl.TrimEnd('/');
            async Task<(int Code, int Ms)> Status(string path)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using var r = await client.GetAsync(baseUrl + path, HttpCompletionOption.ResponseHeadersRead, ct);
                    return ((int)r.StatusCode, (int)clock.ElapsedMilliseconds);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException) { return (0, 0); }
            }

            var all = await Task.WhenAll(
                new[] { Status("/"), Status("/__stackdev_probe_" + Guid.NewGuid().ToString("N")[..8]) }
                    .Concat(Candidates.Select(c => Status(c.Path))));
            var home = all[0].Code;
            var catchAll = all[1].Code is >= 200 and < 300;   // anything answers 200: the paths below say nothing
            var list = new List<ApiEndpoint>();
            if (home != 0) list.Add(new(EndpointKind.Home, "Página principal", "/", home, all[0].Ms));
            if (catchAll) return list;
            var seen = new HashSet<string>();
            for (var i = 0; i < Candidates.Length; i++)
            {
                var status = all[i + 2].Code;
                var (kind, label, path) = Candidates[i];
                var exists = status is >= 200 and < 400 or 401 or 403;
                if (!exists || !seen.Add(label)) continue;
                list.Add(new(kind, label, path, status, all[i + 2].Ms));
            }
            return list;
        }
        finally { if (own) client.Dispose(); }
    }
}
