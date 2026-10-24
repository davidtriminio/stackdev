using System.Text.Json;
using System.Text.RegularExpressions;
using StackDev.Core.Config;

namespace StackDev.Core.Detection;

public sealed record PostgresContainer(string Name, string Image);

/// <summary>Lectura de la salida de docker para detectar el Postgres del proyecto (la ejecución real llega en la fase 3).</summary>
public static class PostgresDocker
{
    /// <summary>De la salida de <c>docker ps -a --format "{{.Names}}|{{.Image}}"</c>, los contenedores con imagen postgres o postgis.</summary>
    public static List<PostgresContainer> FilterContainers(string outputDockerPs)
    {
        var res = new List<PostgresContainer>();
        foreach (var line in outputDockerPs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split('|', 2);
            if (parts.Length != 2) continue;
            if (Regex.IsMatch(parts[1], "post(gres|gis)", RegexOptions.IgnoreCase))
                res.Add(new PostgresContainer(parts[0], parts[1]));
        }
        return res;
    }

    /// <summary>
    /// Arma la configuración de Postgres desde la salida de <c>docker inspect &lt;c&gt; --format "{{json .}}"</c>:
    /// imagen, puerto publicado de 5432/tcp, POSTGRES_USER/PASSWORD/DB y volumen con nombre.
    /// Un volumen anónimo (nombre de 64 caracteres hexadecimales) cuenta como "sin volumen".
    /// </summary>
    public static PostgresConfig? FromInspect(string nameContainer, string inspectJson)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(inspectJson); }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array) // docker inspect sin --format devuelve una lista
            {
                if (root.GetArrayLength() == 0) return null;
                root = root[0];
            }
            if (root.ValueKind != JsonValueKind.Object) return null;

            var env = new Dictionary<string, string>();
            string image = "";
            if (root.TryGetProperty("Config", out var config) && config.ValueKind == JsonValueKind.Object)
            {
                if (config.TryGetProperty("Image", out var img) && img.ValueKind == JsonValueKind.String)
                    image = img.GetString() ?? "";
                if (config.TryGetProperty("Env", out var envArr) && envArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in envArr.EnumerateArray())
                    {
                        var m = Regex.Match(e.GetString() ?? "", "^([^=]+)=(.*)$");
                        if (m.Success) env[m.Groups[1].Value] = m.Groups[2].Value;
                    }
                }
            }

            var port = 5432;
            if (root.TryGetProperty("HostConfig", out var host) && host.ValueKind == JsonValueKind.Object &&
                host.TryGetProperty("PortBindings", out var pb) && pb.ValueKind == JsonValueKind.Object &&
                pb.TryGetProperty("5432/tcp", out var links) && links.ValueKind == JsonValueKind.Array &&
                links.GetArrayLength() > 0 &&
                links[0].TryGetProperty("HostPort", out var hp) &&
                int.TryParse(hp.GetString(), out var p) && p > 0)
            {
                port = p;
            }

            var volume = "";
            if (root.TryGetProperty("Mounts", out var mounts) && mounts.ValueKind == JsonValueKind.Array)
            {
                foreach (var mt in mounts.EnumerateArray())
                {
                    var type = mt.TryGetProperty("Type", out var t) ? t.GetString() : null;
                    var destination = mt.TryGetProperty("Destination", out var d) ? d.GetString() ?? "" : "";
                    var name = mt.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                    if (type == "volume" && destination.StartsWith("/var/lib/postgresql", StringComparison.Ordinal) &&
                        !Regex.IsMatch(name, "^[0-9a-f]{64}$", RegexOptions.IgnoreCase))
                    {
                        volume = name;
                        break;
                    }
                }
            }

            var user = env.TryGetValue("POSTGRES_USER", out var u) && u.Length > 0 ? u : "postgres";
            return new PostgresConfig
            {
                Container = nameContainer,
                Image = image,
                Port = port,
                User = user,
                Password = env.TryGetValue("POSTGRES_PASSWORD", out var pw) ? pw : "",
                Database = env.TryGetValue("POSTGRES_DB", out var db) && db.Length > 0 ? db : user,
                Volume = volume,
            };
        }
    }
}
