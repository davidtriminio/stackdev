using System.Text.RegularExpressions;
using StackDev.Core.Config;
using StackDev.Core.Util;

namespace StackDev.Core.Detection;

/// <summary>Lo que encontró la detección y lo que falta decidir con el usuario.</summary>
public sealed class Deteccion
{
    public required ProyectoConfig Config { get; init; }

    /// <summary>La API referencia Npgsql: toca configurar Postgres (docker inspect o preguntar).</summary>
    public bool ApiUsaNpgsql { get; init; }

    /// <summary>Nombre de la cadena de conexión (ConnectionStrings:X) si se encontró.</summary>
    public string? NombreConexion { get; init; }

    /// <summary>Líneas para mostrar al usuario ("API (.NET): api/src/X -> http://...").</summary>
    public List<string> Hallazgos { get; } = new();

    public bool EncontroAlgo => Config.Api is not null || Config.Web is not null || Config.Movil is not null;
}

/// <summary>Detección automática de la API .NET, la web Angular y la app Flutter de un repo.</summary>
public static class Detector
{
    private const RegexOptions Ci = RegexOptions.IgnoreCase;

    public static Deteccion Detectar(string raiz)
    {
        raiz = Rutas.Normalizar(raiz);
        var nombre = Rutas.Slug(Rutas.NombreCarpeta(raiz));
        var archivos = ArbolArchivos.Buscar(raiz, new[] { "pubspec.yaml", "*.csproj", "package.json" }, 4);
        var hallazgos = new List<string>();
        var tareas = new List<TareaConfig>();

        var api = DetectarApi(raiz, archivos, tareas, hallazgos, out var usaNpgsql, out var nombreConexion);
        var web = DetectarWeb(raiz, archivos, hallazgos);
        var movil = DetectarMovil(raiz, archivos, tareas, hallazgos);

        if (api is null && web is null && movil is null)
            hallazgos.Add("No encontré API .NET, web Angular ni app Flutter.");

        var config = new ProyectoConfig
        {
            Nombre = nombre,
            Api = api,
            Web = web,
            Movil = movil,
            Tareas = tareas,
        };
        config.LevantarTodo = LevantarTodoPorDefecto(config);

        var res = new Deteccion { Config = config, ApiUsaNpgsql = usaNpgsql, NombreConexion = nombreConexion };
        res.Hallazgos.AddRange(hallazgos);
        return res;
    }

    // ------------------------------------------------------------------ API

    private static ApiConfig? DetectarApi(string raiz, List<string> archivos, List<TareaConfig> tareas,
        List<string> hallazgos, out bool usaNpgsql, out string? nombreConexion)
    {
        usaNpgsql = false;
        nombreConexion = null;

        var csprojs = archivos
            .Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Regex.IsMatch(Path.GetFileNameWithoutExtension(f), "Test", Ci))
            .Where(f => Regex.IsMatch(ArbolArchivos.Leer(f), @"Microsoft\.NET\.Sdk\.Web", Ci))
            .OrderBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (csprojs.Count == 0) return null;

        var texto = ArbolArchivos.Leer(csprojs[0]);
        var dirApi = Path.GetDirectoryName(csprojs[0])!;
        var dirSol = CarpetaSolucion(raiz, dirApi);
        var relProj = Rutas.Relativa(dirSol, dirApi);
        if (Regex.IsMatch(relProj, @"\s")) relProj = $"\"{relProj}\"";

        usaNpgsql = Regex.IsMatch(texto, "Npgsql", Ci);
        var url = UrlLaunchSettings(dirApi);
        var abrir = Regex.IsMatch(texto, @"Scalar\.AspNetCore", Ci) ? "/scalar"
                  : Regex.IsMatch(texto, "Swashbuckle|NSwag", Ci) ? "/swagger"
                  : "";

        var api = new ApiConfig
        {
            Carpeta = Rutas.Relativa(raiz, dirSol),
            Url = url,
            Abrir = abrir,
            Env = new Dictionary<string, string>(),
            Iniciar = $"dotnet run --project {relProj}",
            Test = "dotnet test --artifacts-path .art",
            Build = "dotnet build --artifacts-path .art",
        };
        hallazgos.Add($"API (.NET):      {Rutas.Relativa(raiz, dirApi)}  ->  {url}");

        nombreConexion = NombreConexion(dirApi, dirSol);

        // Migraciones de Entity Framework
        var snapshot = ArbolArchivos.Buscar(dirSol, new[] { "*ModelSnapshot.cs" }, 6).FirstOrDefault();
        if (snapshot is not null)
        {
            var d = Path.GetDirectoryName(snapshot);
            while (!string.IsNullOrEmpty(d) && !Directory.EnumerateFiles(d, "*.csproj").Any())
                d = Path.GetDirectoryName(d);
            if (!string.IsNullOrEmpty(d))
            {
                var relMig = Rutas.Relativa(dirSol, d);
                if (Regex.IsMatch(relMig, @"\s")) relMig = $"\"{relMig}\"";
                var proy = $"--project {relMig}";
                if (relMig != relProj) proy += $" --startup-project {relProj}";
                tareas.Add(new TareaConfig { Nombre = "EF: aplicar migraciones", Carpeta = api.Carpeta, Comando = $"dotnet ef database update {proy}" });
                tareas.Add(new TareaConfig { Nombre = "EF: nueva migracion", Carpeta = api.Carpeta, Comando = $"dotnet ef migrations add {{?Nombre de la migracion}} {proy}" });
            }
        }
        return api;
    }

    /// <summary>La primera carpeta hacia arriba (hasta la raíz) con .sln o .slnx; si no hay, la primera carpeta bajo la raíz.</summary>
    public static string CarpetaSolucion(string raiz, string dir)
    {
        var raizN = Rutas.Normalizar(raiz);
        var d = Rutas.Normalizar(dir);
        while (d.Length >= raizN.Length)
        {
            if (TieneSolucion(d)) return d;
            if (string.Equals(d, raizN, Rutas.Comparacion)) break;
            var padre = Path.GetDirectoryName(d);
            if (string.IsNullOrEmpty(padre)) break;
            d = Rutas.Normalizar(padre);
        }
        var primero = Rutas.Relativa(raiz, dir).Split('/')[0];
        if (primero.Length > 0 && primero != ".") return Path.Combine(raiz, primero);
        return dir;
    }

    private static bool TieneSolucion(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir).Any(f =>
                f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>El primer http:// de applicationUrl en launchSettings.json; si no hay, el primero que aparezca.</summary>
    internal static string UrlLaunchSettings(string dirProyecto)
    {
        var texto = ArbolArchivos.Leer(Path.Combine(dirProyecto, "Properties", "launchSettings.json"));
        var ms = Regex.Matches(texto, "\"applicationUrl\"\\s*:\\s*\"([^\"]+)\"");
        foreach (Match m in ms)
        {
            foreach (var u in m.Groups[1].Value.Split(';'))
            {
                if (u.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return u.Trim();
            }
        }
        if (ms.Count > 0) return ms[0].Groups[1].Value.Split(';')[0].Trim();
        return "http://localhost:5000";
    }

    /// <summary>Nombre de la cadena de conexión: de appsettings*.json o, si no está, de GetConnectionString("X") en los .cs.</summary>
    internal static string? NombreConexion(string dirProyecto, string dirSolucion)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dirProyecto, "appsettings*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                var m = Regex.Match(ArbolArchivos.Leer(f), "\"ConnectionStrings\"\\s*:\\s*\\{\\s*\"([^\"]+)\"");
                if (m.Success) return m.Groups[1].Value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        foreach (var f in ArbolArchivos.Buscar(dirSolucion, new[] { "*.cs" }, 5))
        {
            var m = Regex.Match(ArbolArchivos.Leer(f), "GetConnectionString\\(\\s*\"([^\"]+)\"");
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    // ------------------------------------------------------------------ Web

    private static WebConfig? DetectarWeb(string raiz, List<string> archivos, List<string> hallazgos)
    {
        var pkgs = archivos
            .Where(f => Path.GetFileName(f).Equals("package.json", StringComparison.OrdinalIgnoreCase))
            .Where(f => Regex.IsMatch(ArbolArchivos.Leer(f), "\"@angular/core\"", Ci))
            .OrderBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (pkgs.Count == 0) return null;

        var dirWeb = Path.GetDirectoryName(pkgs[0])!;
        var pm = GestorPaquetes(raiz, dirWeb);
        var puerto = 4200;
        var m = Regex.Match(ArbolArchivos.Leer(Path.Combine(dirWeb, "angular.json")), "\"port\"\\s*:\\s*(\\d+)");
        if (m.Success) puerto = int.Parse(m.Groups[1].Value);
        m = Regex.Match(ArbolArchivos.Leer(pkgs[0]), "\"start\"\\s*:\\s*\"[^\"]*--port[= ](\\d+)");
        if (m.Success) puerto = int.Parse(m.Groups[1].Value);

        var (iniciar, test, build, instalar) = pm switch
        {
            "pnpm" => ("pnpm start", "pnpm test --watch=false", "pnpm build", "pnpm install"),
            "yarn" => ("yarn start", "yarn test --watch=false", "yarn build", "yarn install"),
            _ => ("npm start", "npm test -- --watch=false", "npm run build", "npm install"),
        };
        var web = new WebConfig
        {
            Carpeta = Rutas.Relativa(raiz, dirWeb),
            Url = $"http://localhost:{puerto}",
            Iniciar = iniciar,
            Test = test,
            Build = build,
            Instalar = instalar,
        };
        hallazgos.Add($"Web (Angular):   {web.Carpeta}  ->  {web.Url}  ({pm})");
        return web;
    }

    /// <summary>pnpm, yarn o npm: por el lockfile (en la carpeta de la web o en la raíz) o por el campo packageManager.</summary>
    internal static string GestorPaquetes(string raiz, string dir)
    {
        foreach (var d in new[] { dir, raiz })
        {
            if (File.Exists(Path.Combine(d, "pnpm-lock.yaml"))) return "pnpm";
            if (File.Exists(Path.Combine(d, "yarn.lock"))) return "yarn";
            if (File.Exists(Path.Combine(d, "package-lock.json"))) return "npm";
        }
        var m = Regex.Match(ArbolArchivos.Leer(Path.Combine(dir, "package.json")), "\"packageManager\"\\s*:\\s*\"(pnpm|yarn|npm)@");
        return m.Success ? m.Groups[1].Value : "npm";
    }

    // ------------------------------------------------------------------ Flutter

    private static MovilConfig? DetectarMovil(string raiz, List<string> archivos, List<TareaConfig> tareas, List<string> hallazgos)
    {
        var pubs = archivos
            .Where(f => Path.GetFileName(f).Equals("pubspec.yaml", StringComparison.OrdinalIgnoreCase))
            .Where(f => Regex.IsMatch(ArbolArchivos.Leer(f), @"^\s*sdk:\s*flutter\s*$", Ci | RegexOptions.Multiline))
            .OrderBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (pubs.Count == 0) return null;

        var dirMov = Path.GetDirectoryName(pubs[0])!;
        var pub = ArbolArchivos.Leer(pubs[0]);
        var formato = "dart format lib";
        if (Directory.Exists(Path.Combine(dirMov, "test"))) formato += " test";

        var movil = new MovilConfig
        {
            Carpeta = Rutas.Relativa(raiz, dirMov),
            AndroidSdk = "",
            Avd = "",
            ArgsRun = "",
            Analizar = "flutter analyze",
            Test = "flutter test",
            Formato = formato,
            Generar = Regex.IsMatch(pub, "build_runner", Ci) ? "dart run build_runner build --delete-conflicting-outputs" : "",
        };
        hallazgos.Add($"Móvil (Flutter): {movil.Carpeta}");
        tareas.Add(new TareaConfig { Nombre = "Flutter: compilar APK (release)", Carpeta = movil.Carpeta, Comando = "flutter build apk --release" });
        return movil;
    }

    // ------------------------------------------------------------------ Postgres y "levantar todo"

    /// <summary>Agrega Postgres a la configuración y la cadena de conexión (ConnectionStrings__X) a la API.</summary>
    public static void AplicarPostgres(ProyectoConfig config, PostgresConfig pg, string nombreConexion)
    {
        config.Postgres = pg;
        if (config.Api is not null)
        {
            config.Api.Env ??= new Dictionary<string, string>();
            config.Api.Env[$"ConnectionStrings__{nombreConexion}"] = CadenaConexion(pg);
        }
        config.LevantarTodo = LevantarTodoPorDefecto(config);
    }

    public static string CadenaConexion(PostgresConfig pg) =>
        $"Host=127.0.0.1;Port={pg.PuertoEf};Database={pg.BaseDatosEf};Username={pg.UsuarioEf};Password={pg.PasswordEf}";

    public static List<string> LevantarTodoPorDefecto(ProyectoConfig c)
    {
        var l = new List<string>();
        if (c.Postgres is not null) l.Add("postgres");
        if (c.Api is not null) l.Add("api");
        if (c.Web is not null) l.Add("web");
        if (c.Movil is not null) l.Add("emulador");
        return l;
    }
}
