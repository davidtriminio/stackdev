using System.Text.Json;
using StackDev.Core.Config;
using StackDev.Core.Docker;
using StackDev.Core.Proc;
using StackDev.Core.Ui;

namespace StackDev.Core.Diagnostico;

/// <summary>
/// Revisa que lo configurado coincida con la realidad (Docker, puertos, carpetas, SDK) y propone cómo arreglar cada diferencia.
/// Solo lee: ningún chequeo cambia nada; los arreglos se ejecutan aparte y piden confirmación.
/// </summary>
public sealed class DiagnosticoService
{
    private readonly Sesion _s;
    public DiagnosticoService(Sesion s) { _s = s; }

    public List<Hallazgo> Revisar()
    {
        var h = new List<Hallazgo>();
        var c = _s.Ctx;

        if (_s.Store.EnElRepo)
            h.Add(new(Gravedad.Aviso, "Configuración", "La configuración está dentro del proyecto",
                $"{_s.Store.Ruta} puede acabar en un commit.", "Mover fuera del proyecto",
                s => { s.Store.CopiarAfuera(); s.Store.Cargar(); return true; }));

        if (c.Tiene(Componente.Postgres)) RevisarPostgres(h);
        else h.Add(new(Gravedad.Info, "Postgres", "Postgres no está configurado en este proyecto"));

        if (c.Tiene(Componente.Api)) { RevisarCarpeta(h, Componente.Api, "API", new[] { "*.csproj", "*.sln", "*.slnx" }); RevisarPuerto(h, Componente.Api, "API"); }
        if (c.Tiene(Componente.Web)) { RevisarWeb(h); RevisarPuerto(h, Componente.Web, "Web"); }
        if (c.Tiene(Componente.Movil)) RevisarMovil(h);

        // Puertos que chocan con otros proyectos, contenedores o Windows, aunque nada esté corriendo
        try
        {
            var plan = _s.Libres.Plan();
            if (plan.Count > 0)
                h.Add(new(Gravedad.Aviso, "Puertos", "Hay puertos de este proyecto que chocan con otros",
                    string.Join("\n", plan.Select(p => $"{Sesion.Nombres[p.Componente]}: {p.Actual} {p.Motivo} → propongo {p.Nuevo}")),
                    $"Asignar puertos libres ({plan.Count})", s => { s.Libres.Aplicar(s.Libres.Plan()); return true; }));
            else if (c.Tiene(Componente.Api) || c.Tiene(Componente.Web) || c.Tiene(Componente.Postgres))
                h.Add(new(Gravedad.Ok, "Puertos", "Los puertos de este proyecto no chocan con otros proyectos ni contenedores"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }

        if (!h.Any(x => x.Gravedad is Gravedad.Error or Gravedad.Aviso))
            h.Insert(0, new(Gravedad.Ok, "General", "Todo coincide: no encontré diferencias ni errores"));
        return h;
    }

    // ------------------------------------------------------------ Postgres y Docker

    private void RevisarPostgres(List<Hallazgo> h)
    {
        var cfg = _s.Ctx.Config.Postgres!;
        var nombre = cfg.ContenedorEf;

        if (!_s.Docker.Instalado)
        {
            h.Add(new(Gravedad.Error, "Docker", "Docker no está instalado o no está en el PATH", "Postgres necesita Docker."));
            return;
        }
        if (!_s.Docker.Vivo())
        {
            h.Add(new(Gravedad.Error, "Docker", "Docker no responde (¿Docker Desktop apagado?)", "",
                "Abrir Docker Desktop", s => s.Docker.IniciarDesktop()));
            return;
        }
        h.Add(new(Gravedad.Ok, "Docker", "Docker responde"));

        var r = _s.Docker.Run(new[] { "inspect", nombre }, 10);
        var volumenes = _s.Admin.Volumenes();
        var hayVol = cfg.VolumenEf.Length > 0 && volumenes?.Any(v => v.Nombre == cfg.VolumenEf) == true;

        if (!r.Ok)
        {
            var detalle = cfg.VolumenEf.Length == 0
                ? "No usa un volumen con nombre: al crearlo se empieza con una base vacía."
                : hayVol ? $"El volumen '{cfg.VolumenEf}' sigue existiendo: al recrearlo se recuperan los datos."
                         : $"El volumen '{cfg.VolumenEf}' tampoco existe: se creará vacío.";
            h.Add(new(Gravedad.Error, "Postgres", $"El contenedor '{nombre}' no existe en Docker", detalle,
                "Crear con la configuración", s => s.Postgres.Iniciar()));

            var mapa = _s.Libres.Tomar(ignorarComponente: Componente.Postgres);
            var motivo = mapa.Conflicto(cfg.PuertoEf);
            if (motivo is not null)
            {
                var sug = mapa.Sugerir(cfg.PuertoEf + 1);
                h.Add(new(Gravedad.Aviso, "Postgres", $"El puerto {cfg.PuertoEf} {motivo}",
                    "Al crear el contenedor chocaría." + (sug is null ? "" : $" Sugerido: {sug}."),
                    sug is null ? null : $"Usar el puerto {sug}",
                    sug is null ? null : s => { PostgresService.CambiarPuerto(s, sug.Value); return true; }));
            }

            // Otros contenedores Postgres que podrían ser el que se buscaba (renombrado)
            var otros = _s.Admin.Contenedores()?.Where(x => x.Imagen.Contains("postgres", StringComparison.OrdinalIgnoreCase)).ToList();
            if (otros is { Count: > 0 })
                h.Add(new(Gravedad.Info, "Postgres", "Hay otros contenedores Postgres en Docker",
                    string.Join(", ", otros.Select(x => $"{x.Nombre} ({x.Estado})")) + ". Si uno es el tuyo, cambia el nombre en Componentes."));
            return;
        }

        // El contenedor existe: comparar lo configurado con lo real
        try
        {
            using var doc = JsonDocument.Parse(r.Salida);
            var raiz = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement[0] : doc.RootElement;
            var real = LeerContenedor(raiz);

            var estado = _s.Postgres.Estado();
            h.Add(estado.Corriendo
                ? new(Gravedad.Ok, "Postgres", $"El contenedor '{nombre}' está corriendo")
                : new(Gravedad.Aviso, "Postgres", $"El contenedor '{nombre}' está detenido", "", "Iniciar", s => s.Postgres.Iniciar()));

            var difs = new List<string>();
            void Comparar(string que, string config, string? encontrado)
            {
                if (encontrado is not null && !string.Equals(config, encontrado, StringComparison.Ordinal))
                    difs.Add($"{que}: la config dice «{config}», el contenedor tiene «{encontrado}»");
            }
            Comparar("Imagen", cfg.ImagenEf, real.Imagen);
            Comparar("Puerto", cfg.PuertoEf.ToString(), real.Puerto);
            Comparar("Usuario", cfg.UsuarioEf, real.Usuario);
            Comparar("Base de datos", cfg.BaseDatosEf, real.BaseDatos);
            Comparar("Contraseña", cfg.PasswordEf, real.Password);
            if (cfg.VolumenEf.Length > 0 && real.Volumen is not null) Comparar("Volumen", cfg.VolumenEf, real.Volumen);
            else if (cfg.VolumenEf.Length > 0 && real.Volumen is null)
                difs.Add($"Volumen: la config usa «{cfg.VolumenEf}» pero el contenedor no tiene ninguno (los datos no persisten)");

            // Contenedor detenido cuyo puerto lo tomó otro mientras tanto: no arrancaría
            if (!estado.Corriendo)
            {
                var mp = _s.Libres.Tomar(ignorarContenedor: nombre, ignorarComponente: Componente.Postgres);
                var mot = mp.Conflicto(cfg.PuertoEf);
                if (mot is not null)
                {
                    var sug = mp.Sugerir(cfg.PuertoEf + 1);
                    h.Add(new(Gravedad.Aviso, "Postgres", $"El puerto {cfg.PuertoEf} {mot}: el contenedor no podrá iniciar",
                        sug is null ? "" : $"Puerto libre sugerido: {sug}.",
                        sug is null ? null : $"Recrear en el puerto {sug}",
                        sug is null ? null : s => { PostgresService.CambiarPuerto(s, sug.Value); return s.Postgres.Recrear(); }));
                }
            }

            if (difs.Count == 0)
                h.Add(new(Gravedad.Ok, "Postgres", "La configuración coincide con el contenedor"));
            else
            {
                h.Add(new(Gravedad.Aviso, "Postgres", "La configuración no coincide con el contenedor existente",
                    string.Join("\n", difs) + "\nLos cambios de puerto, usuario o contraseña no se aplican a un contenedor ya creado.",
                    "Actualizar la config con lo del contenedor", s => AdoptarDelContenedor(s, real)));
                h.Add(new(Gravedad.Info, "Postgres", "O recrea el contenedor con la config actual",
                    cfg.VolumenEf.Length > 0 ? "Los datos del volumen se conservan." : "Sin volumen con nombre se pierden los datos (se ofrece respaldo).",
                    "Recrear contenedor", s => s.Postgres.Recrear()));
            }

            if (cfg.VolumenEf.Length > 0 && !hayVol && volumenes is not null)
                h.Add(new(Gravedad.Aviso, "Postgres", $"El volumen '{cfg.VolumenEf}' no existe", "Docker lo crea vacío al recrear el contenedor."));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            h.Add(new(Gravedad.Aviso, "Postgres", "No pude leer los datos del contenedor", ex.Message));
        }
    }

    private sealed record DatosContenedor(string? Imagen, string? Puerto, string? Usuario, string? BaseDatos, string? Password, string? Volumen);

    private static DatosContenedor LeerContenedor(JsonElement raiz)
    {
        string? imagen = null, puerto = null, usuario = null, bd = null, pass = null, volumen = null;
        if (raiz.TryGetProperty("Config", out var cfg))
        {
            if (cfg.TryGetProperty("Image", out var im)) imagen = im.GetString();
            if (cfg.TryGetProperty("Env", out var env) && env.ValueKind == JsonValueKind.Array)
                foreach (var e in env.EnumerateArray())
                {
                    var t = e.GetString() ?? "";
                    var i = t.IndexOf('=');
                    if (i <= 0) continue;
                    var k = t[..i]; var v = t[(i + 1)..];
                    if (k == "POSTGRES_USER") usuario = v;
                    else if (k == "POSTGRES_DB") bd = v;
                    else if (k == "POSTGRES_PASSWORD") pass = v;
                }
        }
        if (raiz.TryGetProperty("HostConfig", out var hc) && hc.TryGetProperty("PortBindings", out var pb)
            && pb.TryGetProperty("5432/tcp", out var p5) && p5.ValueKind == JsonValueKind.Array && p5.GetArrayLength() > 0
            && p5[0].TryGetProperty("HostPort", out var hp))
            puerto = hp.GetString();
        if (raiz.TryGetProperty("Mounts", out var mounts) && mounts.ValueKind == JsonValueKind.Array)
            foreach (var m in mounts.EnumerateArray())
                if (m.TryGetProperty("Type", out var tp) && tp.GetString() == "volume" && m.TryGetProperty("Name", out var nm))
                { volumen = nm.GetString(); break; }
        return new(imagen, puerto, usuario, bd, pass, volumen);
    }

    /// <summary>Pone en dev-menu.json lo que tiene de verdad el contenedor (el camino seguro: no toca Docker).</summary>
    private static bool AdoptarDelContenedor(Sesion s, DatosContenedor real)
    {
        var copia = ConfigJson.Leer(ConfigJson.Escribir(s.Ctx.Config));
        var pg = copia.Postgres ??= new PostgresConfig();
        if (real.Imagen is not null) pg.Imagen = real.Imagen;
        if (int.TryParse(real.Puerto, out var puerto)) pg.Puerto = puerto;
        if (real.Usuario is not null) pg.Usuario = real.Usuario;
        if (real.BaseDatos is not null) pg.BaseDatos = real.BaseDatos;
        if (real.Password is not null) pg.Password = real.Password;
        if (real.Volumen is not null) pg.Volumen = real.Volumen;
        s.AplicarConfig(copia);
        s.Ui.Escribir("Configuración actualizada con los valores del contenedor.", Nivel.Ok);
        return true;
    }

    // ------------------------------------------------------------ Carpetas y herramientas

    private void RevisarCarpeta(List<Hallazgo> h, Componente comp, string area, string[] marcas)
    {
        var dir = _s.Ctx.Carpeta(comp);
        if (!Directory.Exists(dir))
        {
            h.Add(new(Gravedad.Error, area, $"No existe la carpeta de {area}", $"{dir}\nCorrígela en Configuración → Componentes."));
            return;
        }
        var hay = marcas.Any(m => Directory.EnumerateFiles(dir, m, SearchOption.TopDirectoryOnly).Any());
        h.Add(hay
            ? new(Gravedad.Ok, area, $"La carpeta de {area} existe")
            : new(Gravedad.Aviso, area, $"En la carpeta de {area} no hay un proyecto reconocible",
                $"{dir}\nSi los comandos usan rutas relativas (--project ...) es normal."));

        if (_s.Ctx.Puerto(comp) == 0)
            h.Add(new(Gravedad.Aviso, area, $"{area} no tiene una URL con puerto válida", "Sin ella no se puede saber si está corriendo."));
    }

    /// <summary>El puerto de la API o la web no debe chocar con otros proyectos, contenedores ni reservas de Windows.</summary>
    private void RevisarPuerto(List<Hallazgo> h, Componente comp, string area)
    {
        var puerto = _s.Ctx.Puerto(comp);
        if (puerto <= 0) return;
        var mapa = _s.Libres.Tomar(ignorarComponente: comp);
        var motivo = mapa.Reservado(puerto);
        if (motivo is null) return;
        var sug = mapa.Sugerir(puerto + 1);
        h.Add(new(Gravedad.Aviso, area, $"El puerto {puerto} de {area}: {motivo}",
            "Si corren a la vez, chocarán." + (sug is null ? "" : $" Puerto libre sugerido: {sug} (cámbialo en Componentes, en la URL).")));
    }

    private void RevisarWeb(List<Hallazgo> h)
    {
        RevisarCarpeta(h, Componente.Web, "Web", new[] { "package.json" });
        var dir = _s.Ctx.Carpeta(Componente.Web);
        if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "package.json")) && !Directory.Exists(Path.Combine(dir, "node_modules")))
            h.Add(new(Gravedad.Aviso, "Web", "Faltan las dependencias (no hay node_modules)", "",
                "Instalar dependencias", s => s.Ejecutor.Paso(Componente.Web, "instalar") == 0));
    }

    private void RevisarMovil(List<Hallazgo> h)
    {
        RevisarCarpeta(h, Componente.Movil, "Móvil", new[] { "pubspec.yaml" });
        if (_s.Sdk is null)
            h.Add(new(Gravedad.Aviso, "Móvil", "No encontré el SDK de Android", "Sin él no se pueden usar el emulador ni adb. Indícalo en Componentes."));
        else if (_s.Adb is null)
            h.Add(new(Gravedad.Aviso, "Móvil", "No encontré adb dentro del SDK de Android", _s.Sdk));
        else h.Add(new(Gravedad.Ok, "Móvil", "SDK de Android encontrado", _s.Sdk));
    }
}
