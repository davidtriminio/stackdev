using System.Text.Json;
using System.Text.RegularExpressions;
using StackDev.Core.Config;

namespace StackDev.Core.Detection;

public sealed record ContenedorPostgres(string Nombre, string Imagen);

/// <summary>Lectura de la salida de docker para detectar el Postgres del proyecto (la ejecución real llega en la fase 3).</summary>
public static class PostgresDocker
{
    /// <summary>De la salida de <c>docker ps -a --format "{{.Names}}|{{.Image}}"</c>, los contenedores con imagen postgres o postgis.</summary>
    public static List<ContenedorPostgres> FiltrarContenedores(string salidaDockerPs)
    {
        var res = new List<ContenedorPostgres>();
        foreach (var linea in salidaDockerPs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var partes = linea.Trim().Split('|', 2);
            if (partes.Length != 2) continue;
            if (Regex.IsMatch(partes[1], "post(gres|gis)", RegexOptions.IgnoreCase))
                res.Add(new ContenedorPostgres(partes[0], partes[1]));
        }
        return res;
    }

    /// <summary>
    /// Arma la configuración de Postgres desde la salida de <c>docker inspect &lt;c&gt; --format "{{json .}}"</c>:
    /// imagen, puerto publicado de 5432/tcp, POSTGRES_USER/PASSWORD/DB y volumen con nombre.
    /// Un volumen anónimo (nombre de 64 caracteres hexadecimales) cuenta como "sin volumen".
    /// </summary>
    public static PostgresConfig? DesdeInspect(string nombreContenedor, string inspectJson)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(inspectJson); }
        catch (JsonException) { return null; }

        using (doc)
        {
            var raiz = doc.RootElement;
            if (raiz.ValueKind == JsonValueKind.Array) // docker inspect sin --format devuelve una lista
            {
                if (raiz.GetArrayLength() == 0) return null;
                raiz = raiz[0];
            }
            if (raiz.ValueKind != JsonValueKind.Object) return null;

            var env = new Dictionary<string, string>();
            string imagen = "";
            if (raiz.TryGetProperty("Config", out var config) && config.ValueKind == JsonValueKind.Object)
            {
                if (config.TryGetProperty("Image", out var img) && img.ValueKind == JsonValueKind.String)
                    imagen = img.GetString() ?? "";
                if (config.TryGetProperty("Env", out var envArr) && envArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in envArr.EnumerateArray())
                    {
                        var m = Regex.Match(e.GetString() ?? "", "^([^=]+)=(.*)$");
                        if (m.Success) env[m.Groups[1].Value] = m.Groups[2].Value;
                    }
                }
            }

            var puerto = 5432;
            if (raiz.TryGetProperty("HostConfig", out var host) && host.ValueKind == JsonValueKind.Object &&
                host.TryGetProperty("PortBindings", out var pb) && pb.ValueKind == JsonValueKind.Object &&
                pb.TryGetProperty("5432/tcp", out var enlaces) && enlaces.ValueKind == JsonValueKind.Array &&
                enlaces.GetArrayLength() > 0 &&
                enlaces[0].TryGetProperty("HostPort", out var hp) &&
                int.TryParse(hp.GetString(), out var p) && p > 0)
            {
                puerto = p;
            }

            var volumen = "";
            if (raiz.TryGetProperty("Mounts", out var mounts) && mounts.ValueKind == JsonValueKind.Array)
            {
                foreach (var mt in mounts.EnumerateArray())
                {
                    var tipo = mt.TryGetProperty("Type", out var t) ? t.GetString() : null;
                    var destino = mt.TryGetProperty("Destination", out var d) ? d.GetString() ?? "" : "";
                    var nombre = mt.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                    if (tipo == "volume" && destino.StartsWith("/var/lib/postgresql", StringComparison.Ordinal) &&
                        !Regex.IsMatch(nombre, "^[0-9a-f]{64}$", RegexOptions.IgnoreCase))
                    {
                        volumen = nombre;
                        break;
                    }
                }
            }

            var usuario = env.TryGetValue("POSTGRES_USER", out var u) && u.Length > 0 ? u : "postgres";
            return new PostgresConfig
            {
                Contenedor = nombreContenedor,
                Imagen = imagen,
                Puerto = puerto,
                Usuario = usuario,
                Password = env.TryGetValue("POSTGRES_PASSWORD", out var pw) ? pw : "",
                BaseDatos = env.TryGetValue("POSTGRES_DB", out var db) && db.Length > 0 ? db : usuario,
                Volumen = volumen,
            };
        }
    }
}
