using System.Text.Json;
using StackDev.Core.Ui;

namespace StackDev.Core.Docker;

public sealed record ContenedorDocker(string Id, string Nombre, string Imagen, string Estado, string Detalle, string Puertos)
{
    public bool Corriendo => Estado.Equals("running", StringComparison.OrdinalIgnoreCase);
}

public sealed record VolumenDocker(string Nombre, string Driver);

/// <summary>Ver y administrar contenedores y volúmenes de Docker desde la herramienta.</summary>
public sealed class DockerAdmin
{
    private readonly Sesion _s;
    public DockerAdmin(Sesion s) { _s = s; }

    private static string Texto(JsonElement e, string nombre) =>
        e.TryGetProperty(nombre, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Todos los contenedores (también los detenidos). null si Docker no responde.</summary>
    public List<ContenedorDocker>? Contenedores()
    {
        var r = _s.Docker.Run(new[] { "ps", "-a", "--format", "{{json .}}" }, 10);
        if (!r.Ok) return null;
        var lista = new List<ContenedorDocker>();
        foreach (var linea in r.Salida.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(linea.Trim());
                var e = doc.RootElement;
                lista.Add(new ContenedorDocker(Texto(e, "ID"), Texto(e, "Names"), Texto(e, "Image"),
                    Texto(e, "State"), Texto(e, "Status"), Texto(e, "Ports")));
            }
            catch (JsonException) { }
        }
        return lista.OrderByDescending(c => c.Corriendo).ThenBy(c => c.Nombre, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public List<VolumenDocker>? Volumenes()
    {
        var r = _s.Docker.Run(new[] { "volume", "ls", "--format", "{{json .}}" }, 10);
        if (!r.Ok) return null;
        var lista = new List<VolumenDocker>();
        foreach (var linea in r.Salida.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(linea.Trim());
                lista.Add(new VolumenDocker(Texto(doc.RootElement, "Name"), Texto(doc.RootElement, "Driver")));
            }
            catch (JsonException) { }
        }
        return lista.OrderBy(v => v.Nombre, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool ExisteVolumen(string nombre) => Volumenes()?.Any(v => v.Nombre == nombre) == true;

    private bool Orden(string verbo, string objetivo, string ok, int timeout = 60)
    {
        var r = _s.Docker.Run(new[] { verbo, objetivo }, timeout);
        if (r.Ok) { _s.Ui.Escribir(ok, Nivel.Ok); return true; }
        _s.Ui.Escribir($"docker {verbo} {objetivo}: {r.Texto.Trim()}", Nivel.Error);
        return false;
    }

    public bool Iniciar(string c) => Orden("start", c, $"Contenedor {c} iniciado.");
    public bool Detener(string c) => Orden("stop", c, $"Contenedor {c} detenido.");
    public bool Reiniciar(string c) => Orden("restart", c, $"Contenedor {c} reiniciado.");

    public bool EliminarContenedor(string c)
    {
        var r = _s.Docker.Run(new[] { "rm", "-f", c }, 60);
        if (r.Ok) { _s.Ui.Escribir($"Contenedor {c} eliminado.", Nivel.Ok); return true; }
        _s.Ui.Escribir($"No se pudo eliminar {c}: {r.Texto.Trim()}", Nivel.Error);
        return false;
    }

    public bool EliminarVolumen(string v)
    {
        var r = _s.Docker.Run(new[] { "volume", "rm", v }, 30);
        if (r.Ok) { _s.Ui.Escribir($"Volumen {v} eliminado.", Nivel.Ok); return true; }
        _s.Ui.Escribir($"No se pudo eliminar el volumen {v}: {r.Texto.Trim()}", Nivel.Error);
        return false;
    }

    public bool Logs(string c, int n = 80)
    {
        var r = _s.Docker.Run(new[] { "logs", "--tail", n.ToString(), c }, 15);
        var texto = (r.Salida + "\n" + r.Error).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(n);
        _s.Ui.Escribir($"— logs de {c} (últimas {n} líneas) —", Nivel.Info);
        foreach (var l in texto) _s.Ui.Escribir(l, Nivel.Tenue);
        return true;
    }
}
