using System.Text.Json;
using StackDev.Core.Config;
using StackDev.Core.Docker;
using StackDev.Core.Proc;
using StackDev.Core.Ui;

namespace StackDev.Core.Diagnostics;

/// <summary>
/// Revisa que lo configurado coincida con la realidad (Docker, puertos, carpetas, SDK) y propone cómo arreglar cada diferencia.
/// Solo lee: ningún chequeo cambia nada; los arreglos se ejecutan aparte y piden confirmación.
/// </summary>
public sealed class DiagnosticsService
{
    private readonly Session _s;
    public DiagnosticsService(Session s) { _s = s; }

    public List<Finding> Review()
    {
        var h = new List<Finding>();
        var c = _s.Ctx;

        if (_s.Store.InRepo)
            h.Add(new(Severity.Warning, "Configuración", "La configuración está dentro del proyecto",
                $"{_s.Store.FilePath} puede acabar en un commit.", "Mover fuera del proyecto",
                s => { s.Store.CopyOut(); s.Store.Load(); return true; }));

        if (c.Has(ProjectComponent.Postgres)) CheckPostgres(h);
        else h.Add(new(Severity.Info, "Postgres", "Postgres no está configurado en este proyecto"));

        if (c.Has(ProjectComponent.Api)) { CheckFolder(h, ProjectComponent.Api, "API", new[] { "*.csproj", "*.sln", "*.slnx" }); CheckPort(h, ProjectComponent.Api, "API"); }
        if (c.Has(ProjectComponent.Web)) { CheckWeb(h); CheckPort(h, ProjectComponent.Web, "Web"); }
        if (c.Has(ProjectComponent.Mobile)) CheckMobile(h);

        // Puertos que chocan con otros proyectos, contenedores o Windows, aunque nada esté corriendo
        try
        {
            var plan = _s.Free.Plan();
            if (plan.Count > 0)
                h.Add(new(Severity.Warning, "Puertos", "Hay puertos de este proyecto que chocan con otros",
                    string.Join("\n", plan.Select(p => $"{Session.Names[p.ProjectComponent]}: {p.Current} {p.Reason} → propongo {p.Fresh}")),
                    $"Asignar puertos libres ({plan.Count})", s => { s.Free.Apply(s.Free.Plan()); return true; }));
            else if (c.Has(ProjectComponent.Api) || c.Has(ProjectComponent.Web) || c.Has(ProjectComponent.Postgres))
                h.Add(new(Severity.Ok, "Puertos", "Los puertos de este proyecto no chocan con otros proyectos ni contenedores"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }

        if (!h.Any(x => x.Severity is Severity.Error or Severity.Warning))
            h.Insert(0, new(Severity.Ok, "General", "Todo coincide: no encontré diferencias ni errores"));
        return h;
    }

    // ------------------------------------------------------------ Postgres y Docker

    private void CheckPostgres(List<Finding> h)
    {
        var cfg = _s.Ctx.Config.Postgres!;
        var name = cfg.EfContainer;

        if (!_s.Docker.Installed)
        {
            h.Add(new(Severity.Error, "Docker", "Docker no está instalado o no está en el PATH", "Postgres necesita Docker."));
            return;
        }
        if (!_s.Docker.Live())
        {
            h.Add(new(Severity.Error, "Docker", "Docker no responde (¿Docker Desktop apagado?)", "",
                "Abrir Docker Desktop", s => s.Docker.StartDesktop()));
            return;
        }
        h.Add(new(Severity.Ok, "Docker", "Docker responde"));

        var r = _s.Docker.Run(new[] { "inspect", name }, 10);
        var volumes = _s.Admin.Volumes();
        var hasVol = cfg.EfVolume.Length > 0 && volumes?.Any(v => v.Name == cfg.EfVolume) == true;

        if (!r.Ok)
        {
            var detail = cfg.EfVolume.Length == 0
                ? "No usa un volumen con nombre: al crearlo se empieza con una base vacía."
                : hasVol ? $"El volumen '{cfg.EfVolume}' sigue existiendo: al recrearlo se recuperan los datos."
                         : $"El volumen '{cfg.EfVolume}' tampoco existe: se creará vacío.";
            h.Add(new(Severity.Error, "Postgres", $"El contenedor '{name}' no existe en Docker", detail,
                "Crear con la configuración", s => s.Postgres.Start()));

            var map = _s.Free.Take(ignoreComponent: ProjectComponent.Postgres);
            var reason = map.Conflict(cfg.EfPort);
            if (reason is not null)
            {
                var sug = map.Suggest(cfg.EfPort + 1);
                h.Add(new(Severity.Warning, "Postgres", $"El puerto {cfg.EfPort} {reason}",
                    "Al crear el contenedor chocaría." + (sug is null ? "" : $" Sugerido: {sug}."),
                    sug is null ? null : $"Usar el puerto {sug}",
                    sug is null ? null : s => { PostgresService.ChangePort(s, sug.Value); return true; }));
            }

            // Otros contenedores Postgres que podrían ser el que se buscaba (renombrado)
            var other = _s.Admin.Containers()?.Where(x => x.Image.Contains("postgres", StringComparison.OrdinalIgnoreCase)).ToList();
            if (other is { Count: > 0 })
                h.Add(new(Severity.Info, "Postgres", "Hay otros contenedores Postgres en Docker",
                    string.Join(", ", other.Select(x => $"{x.Name} ({x.State})")) + ". Si uno es el tuyo, cambia el nombre en Componentes."));
            return;
        }

        // El contenedor existe: comparar lo configurado con lo real
        try
        {
            using var doc = JsonDocument.Parse(r.Output);
            var root = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement[0] : doc.RootElement;
            var real = ReadContainer(root);

            var state = _s.Postgres.State();
            h.Add(state.Running
                ? new(Severity.Ok, "Postgres", $"El contenedor '{name}' está corriendo")
                : new(Severity.Warning, "Postgres", $"El contenedor '{name}' está detenido", "", "Iniciar", s => s.Postgres.Start()));

            var diffs = new List<string>();
            void Compare(string that, string config, string? found)
            {
                if (found is not null && !string.Equals(config, found, StringComparison.Ordinal))
                    diffs.Add($"{that}: la config dice «{config}», el contenedor tiene «{found}»");
            }
            Compare("Imagen", cfg.EfImage, real.Image);
            Compare("Puerto", cfg.EfPort.ToString(), real.Port);
            Compare("Usuario", cfg.EfUser, real.User);
            Compare("Base de datos", cfg.EfDatabase, real.Database);
            Compare("Contraseña", cfg.EfPassword, real.Password);
            if (cfg.EfVolume.Length > 0 && real.Volume is not null) Compare("Volumen", cfg.EfVolume, real.Volume);
            else if (cfg.EfVolume.Length > 0 && real.Volume is null)
                diffs.Add($"Volumen: la config usa «{cfg.EfVolume}» pero el contenedor no tiene ninguno (los datos no persisten)");

            // Contenedor detenido cuyo puerto lo tomó otro mientras tanto: no arrancaría
            if (!state.Running)
            {
                var mp = _s.Free.Take(ignoreContainer: name, ignoreComponent: ProjectComponent.Postgres);
                var mot = mp.Conflict(cfg.EfPort);
                if (mot is not null)
                {
                    var sug = mp.Suggest(cfg.EfPort + 1);
                    h.Add(new(Severity.Warning, "Postgres", $"El puerto {cfg.EfPort} {mot}: el contenedor no podrá iniciar",
                        sug is null ? "" : $"Puerto libre sugerido: {sug}.",
                        sug is null ? null : $"Recrear en el puerto {sug}",
                        sug is null ? null : s => { PostgresService.ChangePort(s, sug.Value); return s.Postgres.Recreate(); }));
                }
            }

            if (diffs.Count == 0)
                h.Add(new(Severity.Ok, "Postgres", "La configuración coincide con el contenedor"));
            else
            {
                h.Add(new(Severity.Warning, "Postgres", "La configuración no coincide con el contenedor existente",
                    string.Join("\n", diffs) + "\nLos cambios de puerto, usuario o contraseña no se aplican a un contenedor ya creado.",
                    "Actualizar la config con lo del contenedor", s => AdoptFromContainer(s, real)));
                h.Add(new(Severity.Info, "Postgres", "O recrea el contenedor con la config actual",
                    cfg.EfVolume.Length > 0 ? "Los datos del volumen se conservan." : "Sin volumen con nombre se pierden los datos (se ofrece respaldo).",
                    "Recrear contenedor", s => s.Postgres.Recreate()));
            }

            if (cfg.EfVolume.Length > 0 && !hasVol && volumes is not null)
                h.Add(new(Severity.Warning, "Postgres", $"El volumen '{cfg.EfVolume}' no existe", "Docker lo crea vacío al recrear el contenedor."));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            h.Add(new(Severity.Warning, "Postgres", "No pude leer los datos del contenedor", ex.Message));
        }
    }

    private sealed record ContainerData(string? Image, string? Port, string? User, string? Database, string? Password, string? Volume);

    private static ContainerData ReadContainer(JsonElement root)
    {
        string? image = null, port = null, user = null, db = null, pass = null, volume = null;
        if (root.TryGetProperty("Config", out var cfg))
        {
            if (cfg.TryGetProperty("Image", out var im)) image = im.GetString();
            if (cfg.TryGetProperty("Env", out var env) && env.ValueKind == JsonValueKind.Array)
                foreach (var e in env.EnumerateArray())
                {
                    var t = e.GetString() ?? "";
                    var i = t.IndexOf('=');
                    if (i <= 0) continue;
                    var k = t[..i]; var v = t[(i + 1)..];
                    if (k == "POSTGRES_USER") user = v;
                    else if (k == "POSTGRES_DB") db = v;
                    else if (k == "POSTGRES_PASSWORD") pass = v;
                }
        }
        if (root.TryGetProperty("HostConfig", out var hc) && hc.TryGetProperty("PortBindings", out var pb)
            && pb.TryGetProperty("5432/tcp", out var p5) && p5.ValueKind == JsonValueKind.Array && p5.GetArrayLength() > 0
            && p5[0].TryGetProperty("HostPort", out var hp))
            port = hp.GetString();
        if (root.TryGetProperty("Mounts", out var mounts) && mounts.ValueKind == JsonValueKind.Array)
            foreach (var m in mounts.EnumerateArray())
                if (m.TryGetProperty("Type", out var tp) && tp.GetString() == "volume" && m.TryGetProperty("Name", out var nm))
                { volume = nm.GetString(); break; }
        return new(image, port, user, db, pass, volume);
    }

    /// <summary>Pone en dev-menu.json lo que tiene de verdad el contenedor (el camino seguro: no toca Docker).</summary>
    private static bool AdoptFromContainer(Session s, ContainerData real)
    {
        var copy = ConfigJson.Read(ConfigJson.Write(s.Ctx.Config));
        var pg = copy.Postgres ??= new PostgresConfig();
        if (real.Image is not null) pg.Image = real.Image;
        if (int.TryParse(real.Port, out var port)) pg.Port = port;
        if (real.User is not null) pg.User = real.User;
        if (real.Database is not null) pg.Database = real.Database;
        if (real.Password is not null) pg.Password = real.Password;
        if (real.Volume is not null) pg.Volume = real.Volume;
        s.ApplyConfig(copy);
        s.Ui.Write("Configuración actualizada con los valores del contenedor.", Level.Ok);
        return true;
    }

    // ------------------------------------------------------------ Carpetas y herramientas

    private void CheckFolder(List<Finding> h, ProjectComponent comp, string area, string[] marks)
    {
        var dir = _s.Ctx.Folder(comp);
        if (!Directory.Exists(dir))
        {
            h.Add(new(Severity.Error, area, $"No existe la carpeta de {area}", $"{dir}\nCorrígela en Configuración → Componentes."));
            return;
        }
        var has = marks.Any(m => Directory.EnumerateFiles(dir, m, SearchOption.TopDirectoryOnly).Any());
        h.Add(has
            ? new(Severity.Ok, area, $"La carpeta de {area} existe")
            : new(Severity.Warning, area, $"En la carpeta de {area} no hay un proyecto reconocible",
                $"{dir}\nSi los comandos usan rutas relativas (--project ...) es normal."));

        if (_s.Ctx.Port(comp) == 0)
            h.Add(new(Severity.Warning, area, $"{area} no tiene una URL con puerto válida", "Sin ella no se puede saber si está corriendo."));
    }

    /// <summary>El puerto de la API o la web no debe chocar con otros proyectos, contenedores ni reservas de Windows.</summary>
    private void CheckPort(List<Finding> h, ProjectComponent comp, string area)
    {
        var port = _s.Ctx.Port(comp);
        if (port <= 0) return;
        var map = _s.Free.Take(ignoreComponent: comp);
        var reason = map.ReservedReason(port);
        if (reason is null) return;
        var sug = map.Suggest(port + 1);
        h.Add(new(Severity.Warning, area, $"El puerto {port} de {area}: {reason}",
            "Si corren a la vez, chocarán." + (sug is null ? "" : $" Puerto libre sugerido: {sug} (cámbialo en Componentes, en la URL).")));
    }

    private void CheckWeb(List<Finding> h)
    {
        CheckFolder(h, ProjectComponent.Web, "Web", new[] { "package.json" });
        var dir = _s.Ctx.Folder(ProjectComponent.Web);
        if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "package.json")) && !Directory.Exists(Path.Combine(dir, "node_modules")))
            h.Add(new(Severity.Warning, "Web", "Faltan las dependencias (no hay node_modules)", "",
                "Instalar dependencias", s => s.Executor.Step(ProjectComponent.Web, "instalar") == 0));
    }

    private void CheckMobile(List<Finding> h)
    {
        CheckFolder(h, ProjectComponent.Mobile, "Móvil", new[] { "pubspec.yaml" });
        if (_s.Sdk is null)
            h.Add(new(Severity.Warning, "Móvil", "No encontré el SDK de Android", "Sin él no se pueden usar el emulador ni adb. Indícalo en Componentes."));
        else if (_s.Adb is null)
            h.Add(new(Severity.Warning, "Móvil", "No encontré adb dentro del SDK de Android", _s.Sdk));
        else h.Add(new(Severity.Ok, "Móvil", "SDK de Android encontrado", _s.Sdk));
    }
}
