using System.Text.Json;
using StackDev.Core.Ui;

namespace StackDev.Core.Docker;

public sealed record DockerContainer(string Id, string Name, string Image, string State, string Detail, string Ports)
{
    public bool Running => State.Equals("running", StringComparison.OrdinalIgnoreCase);
}

public sealed record DockerVolume(string Name, string Driver);

/// <summary>Ver y administrar contenedores y volúmenes de Docker desde la herramienta.</summary>
public sealed class DockerAdmin
{
    private readonly Session _s;
    public DockerAdmin(Session s) { _s = s; }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Todos los contenedores (también los detenidos). null si Docker no responde.</summary>
    public List<DockerContainer>? Containers()
    {
        var r = _s.Docker.Run(new[] { "ps", "-a", "--format", "{{json .}}" }, 10);
        if (!r.Ok) return null;
        var list = new List<DockerContainer>();
        foreach (var line in r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line.Trim());
                var e = doc.RootElement;
                list.Add(new DockerContainer(Text(e, "ID"), Text(e, "Names"), Text(e, "Image"),
                    Text(e, "State"), Text(e, "Status"), Text(e, "Ports")));
            }
            catch (JsonException) { }
        }
        return list.OrderByDescending(c => c.Running).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public List<DockerVolume>? Volumes()
    {
        var r = _s.Docker.Run(new[] { "volume", "ls", "--format", "{{json .}}" }, 10);
        if (!r.Ok) return null;
        var list = new List<DockerVolume>();
        foreach (var line in r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line.Trim());
                list.Add(new DockerVolume(Text(doc.RootElement, "Name"), Text(doc.RootElement, "Driver")));
            }
            catch (JsonException) { }
        }
        return list.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool VolumeExists(string name) => Volumes()?.Any(v => v.Name == name) == true;

    private bool Order(string verb, string target, string ok, int timeout = 60)
    {
        var r = _s.Docker.Run(new[] { verb, target }, timeout);
        if (r.Ok) { _s.Ui.Write(ok, Level.Ok); return true; }
        _s.Ui.Write($"docker {verb} {target}: {r.Text.Trim()}", Level.Error);
        return false;
    }

    public bool Start(string c) => Order("start", c, $"Contenedor {c} iniciado.");
    public bool Stop(string c) => Order("stop", c, $"Contenedor {c} detenido.");
    public bool Restart(string c) => Order("restart", c, $"Contenedor {c} reiniciado.");

    public bool DeleteContainer(string c)
    {
        var r = _s.Docker.Run(new[] { "rm", "-f", c }, 60);
        if (r.Ok) { _s.Ui.Write($"Contenedor {c} eliminado.", Level.Ok); return true; }
        _s.Ui.Write($"No se pudo eliminar {c}: {r.Text.Trim()}", Level.Error);
        return false;
    }

    public bool DeleteVolume(string v)
    {
        var r = _s.Docker.Run(new[] { "volume", "rm", v }, 30);
        if (r.Ok) { _s.Ui.Write($"Volumen {v} eliminado.", Level.Ok); return true; }
        _s.Ui.Write($"No se pudo eliminar el volumen {v}: {r.Text.Trim()}", Level.Error);
        return false;
    }

    public bool Logs(string c, int n = 80)
    {
        var r = _s.Docker.Run(new[] { "logs", "--tail", n.ToString(), c }, 15);
        var text = (r.Output + "\n" + r.Error).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(n);
        _s.Ui.Write($"— logs de {c} (últimas {n} líneas) —", Level.Info);
        foreach (var l in text) _s.Ui.Write(l, Level.Muted);
        return true;
    }
}
