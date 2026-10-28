using System.Globalization;
using StackDev.Core.Config;
using StackDev.Core.Detection;
using StackDev.Core.Ui;
using StackDev.Core.Util;

namespace StackDev.Core.Orchestration;

/// <summary>First time in a project: detects the components and asks what cannot be known (Postgres, Android SDK).</summary>
public sealed class ConfigWizard
{
    private readonly Session _s;

    public ConfigWizard(Session s) { _s = s; }

    public ProjectConfig Create()
    {
        var name = Paths.Slug(Paths.FolderName(_s.Root));
        _s.Ui.Write($"\nBuscando componentes en {_s.Root} ...", Level.Muted);
        var d = Detector.Detect(_s.Root);
        foreach (var h in d.Findings) _s.Ui.Write("  " + h, d.FoundAnything ? Level.Ok : Level.Warning);

        if (d.Config.Api is not null)
        {
            var wants = d.ApiUsesNpgsql;
            if (wants) _s.Ui.Write("\nLa API usa PostgreSQL.", Level.Info);
            else if (_s.Docker.Installed) wants = _s.Ui.Confirm("¿La API usa una base de datos Postgres en Docker?", false);
            if (wants)
            {
                var pg = ReadPostgresConfig(name);
                if (pg is not null)
                {
                    var nc = d.ConnectionName ?? _s.Ui.Ask("Nombre de la cadena de conexión (ConnectionStrings:...)",
                        CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name));
                    Detector.ApplyPostgres(d.Config, pg, nc ?? "Default");
                    _s.Ui.Write($"  Postgres:          {pg.Container} ({pg.Image}) puerto {pg.Port}", Level.Ok);
                    _s.Ui.Write($"  Cadena de conexión para la API: ConnectionStrings__{nc}", Level.Ok);
                }
            }
        }
        if (d.Config.Mobile is not null) d.Config.Mobile.AndroidSdk = SearchSdk();
        return d.Config;
    }

    private string SearchSdk()
    {
        string? flutter = null;
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrEmpty(appData))
        {
            try { var f = Path.Combine(appData, ".flutter_settings"); if (File.Exists(f)) flutter = File.ReadAllText(f); } catch (IOException) { }
        }
        var sdk = AndroidSdkLocator.Search(null, Environment.GetEnvironmentVariable, flutter, Environment.GetEnvironmentVariable("LOCALAPPDATA"));
        if (sdk is not null) return sdk;
        while (true)
        {
            var p = _s.Ui.Ask(@"Ruta del Android SDK (ej: C:\Android\Sdk, Enter para omitir)")?.Trim().Trim('"');
            if (string.IsNullOrEmpty(p)) return "";
            if (AndroidSdkLocator.IsValid(p)) return p;
            _s.Ui.Write(@"En esa carpeta no está emulator\emulator.exe.", Level.Error);
            if (!_s.Ui.Interactive) return "";
        }
    }

    /// <summary>Lists the Postgres/PostGIS containers, reads the configuration of the one you choose with docker inspect, or asks for the details.</summary>
    private PostgresConfig? ReadPostgresConfig(string name)
    {
        if (_s.Ui.Interactive && _s.Docker.Installed && !_s.Docker.Live() &&
            _s.Ui.Confirm("Docker no está corriendo. ¿Abrirlo para buscar tu contenedor?", true))
            _s.Docker.StartDesktop();

        if (_s.Docker.Installed && _s.Docker.Live())
        {
            var r = _s.Docker.Run(new[] { "ps", "-a", "--format", "{{.Names}}|{{.Image}}" }, 15);
            var list = PostgresDocker.FilterContainers(r.Output);
            if (list.Count > 0)
            {
                var options = list.Select(c => $"{c.Name}  ({c.Image})").ToList();
                options.Add("Configurar uno nuevo");
                var i = _s.Ui.Choose("\nContenedores de Postgres encontrados (Enter/cancelar = sin Postgres):", options, -1);
                if (i < 0) return null;
                if (i < list.Count)
                {
                    var insp = _s.Docker.Run(new[] { "inspect", list[i].Name, "--format", "{{json .}}" }, 15);
                    var pg = insp.Ok ? PostgresDocker.FromInspect(list[i].Name, insp.Output) : null;
                    if (pg is not null) return pg;
                    _s.Ui.Write("No pude leer ese contenedor; ingresa los datos.", Level.Warning);
                }
            }
        }

        _s.Ui.Write("\nDatos del contenedor de Postgres:", Level.Info);
        var cont = _s.Ui.Ask("Nombre del contenedor", $"{name}-pg") ?? $"{name}-pg";
        var fresh = new PostgresConfig
        {
            Container = cont,
            Image = _s.Ui.Ask("Imagen", "postgres:17-alpine"),
            Port = AskPort("Puerto en tu PC", 5432),
            User = _s.Ui.Ask("Usuario", name),
            Password = _s.Ui.Ask("Contraseña", "dev"),
            Database = _s.Ui.Ask("Base de datos", name),
            Volume = "",
        };
        if (_s.Ui.Confirm("¿Guardar los datos en un volumen para no perderlos al recrear el contenedor?", true))
            fresh.Volume = $"{cont}-data";
        return fresh;
    }

    private int AskPort(string text, int fallback)
    {
        while (true)
        {
            var v = _s.Ui.Ask(text, fallback.ToString());
            if (int.TryParse(v, out var p) && p is >= 1 and <= 65535) return p;
            _s.Ui.Write("Escribe un número de puerto (1-65535).", Level.Error);
            if (!_s.Ui.Interactive) return fallback;
        }
    }
}
