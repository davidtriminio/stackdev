using StackDev.Core.Detection.Profiles;
using System.Text.RegularExpressions;
using StackDev.Core.Config;
using StackDev.Core.Util;

namespace StackDev.Core.Detection;

/// <summary>Lo que encontró la detección y lo que falta decidir con el usuario.</summary>
public sealed class DetectionResult
{
    public required ProjectConfig Config { get; init; }

    /// <summary>La API referencia Npgsql: toca configurar Postgres (docker inspect o preguntar).</summary>
    public bool ApiUsesNpgsql { get; init; }

    /// <summary>Nombre de la cadena de conexión (ConnectionStrings:X) si se encontró.</summary>
    public string? ConnectionName { get; init; }

    /// <summary>Líneas para mostrar al usuario ("API (.NET): api/src/X -> http://...").</summary>
    public List<string> Findings { get; } = new();

    public bool FoundAnything => Config.Api is not null || Config.Web is not null || Config.Mobile is not null;
}

/// <summary>Detección automática de la API, la web y la app Flutter de un repo: .NET y Angular directamente, otros lenguajes con perfiles.</summary>
public static class Detector
{
    private const RegexOptions Ci = RegexOptions.IgnoreCase;

    public static DetectionResult Detect(string root, ProfileCatalog? profiles = null)
    {
        root = Paths.Normalize(root);
        profiles ??= ProfileCatalog.Load();
        var name = Paths.Slug(Paths.FolderName(root));
        var files = FileTree.Search(root, new[] { "pubspec.yaml", "*.csproj", "package.json" }.Concat(profiles.FileNames).Distinct(StringComparer.OrdinalIgnoreCase), 4);
        var findings = new List<string>();
        var tasks = new List<TaskConfig>();

        var api = DetectApi(root, files, tasks, findings, out var usesNpgsql, out var nameConnection);
        var web = DetectWeb(root, files, findings);
        var mobile = DetectMobile(root, files, tasks, findings);

        // Other languages and frameworks come from data (profiles); .NET, Angular and Flutter keep their dedicated detection.
        if (api is null && ProfileDetector.Find(files, profiles, "api") is { } apiHit)
        {
            api = ProfileDetector.BuildApi(root, apiHit, tasks);
            findings.Add($"API ({apiHit.Profile.Title}):  {api.Folder}  ->  {api.Url}");
        }
        if (web is null && ProfileDetector.Find(files, profiles, "web") is { } webHit)
        {
            web = ProfileDetector.BuildWeb(root, webHit, tasks);
            findings.Add($"Web ({webHit.Profile.Title}):  {web.Folder}  ->  {web.Url}  ({PackageManager(root, webHit.Folder)})");
        }

        if (api is null && web is null && mobile is null)
            findings.Add("No reconocí ninguna API, web ni app Flutter en esta carpeta.");

        var config = new ProjectConfig
        {
            Name = name,
            Api = api,
            Web = web,
            Mobile = mobile,
            Tasks = tasks,
        };
        config.StartAll = DefaultStartAll(config);

        var res = new DetectionResult { Config = config, ApiUsesNpgsql = usesNpgsql, ConnectionName = nameConnection };
        res.Findings.AddRange(findings);
        return res;
    }

    // ------------------------------------------------------------------ API

    private static ApiConfig? DetectApi(string root, List<string> files, List<TaskConfig> tasks,
        List<string> findings, out bool usesNpgsql, out string? nameConnection)
    {
        usesNpgsql = false;
        nameConnection = null;

        var csprojs = files
            .Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Regex.IsMatch(Path.GetFileNameWithoutExtension(f), "Test", Ci))
            .Where(f => Regex.IsMatch(FileTree.Read(f), @"Microsoft\.NET\.Sdk\.Web", Ci))
            .OrderBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (csprojs.Count == 0) return null;

        var text = FileTree.Read(csprojs[0]);
        var dirApi = Path.GetDirectoryName(csprojs[0])!;
        var dirSolution = SolutionFolder(root, dirApi);
        var relProj = Paths.Relative(dirSolution, dirApi);
        if (Regex.IsMatch(relProj, @"\s")) relProj = $"\"{relProj}\"";

        usesNpgsql = Regex.IsMatch(text, "Npgsql", Ci);
        var url = UrlLaunchSettings(dirApi);
        var open = Regex.IsMatch(text, @"Scalar\.AspNetCore", Ci) ? "/scalar"
                  : Regex.IsMatch(text, "Swashbuckle|NSwag", Ci) ? "/swagger"
                  : "";

        var api = new ApiConfig
        {
            Folder = Paths.Relative(root, dirSolution),
            Url = url,
            Open = open,
            Env = new Dictionary<string, string>(),
            Start = $"dotnet run --project {relProj}",
            Test = "dotnet test --artifacts-path .art",
            Build = "dotnet build --artifacts-path .art",
        };
        findings.Add($"API (.NET):      {Paths.Relative(root, dirApi)}  ->  {url}");

        nameConnection = ConnectionName(dirApi, dirSolution);

        // Migraciones de Entity Framework
        var snapshot = FileTree.Search(dirSolution, new[] { "*ModelSnapshot.cs" }, 6).FirstOrDefault();
        if (snapshot is not null)
        {
            var d = Path.GetDirectoryName(snapshot);
            while (!string.IsNullOrEmpty(d) && !Directory.EnumerateFiles(d, "*.csproj").Any())
                d = Path.GetDirectoryName(d);
            if (!string.IsNullOrEmpty(d))
            {
                var relMig = Paths.Relative(dirSolution, d);
                if (Regex.IsMatch(relMig, @"\s")) relMig = $"\"{relMig}\"";
                var proj = $"--project {relMig}";
                if (relMig != relProj) proj += $" --startup-project {relProj}";
                tasks.Add(new TaskConfig { Name = "EF: aplicar migraciones", Folder = api.Folder, Command = $"dotnet ef database update {proj}" });
                tasks.Add(new TaskConfig { Name = "EF: nueva migracion", Folder = api.Folder, Command = $"dotnet ef migrations add {{?Nombre de la migracion}} {proj}" });
            }
        }
        return api;
    }

    /// <summary>La primera carpeta hacia arriba (hasta la raíz) con .sln o .slnx; si no hay, la primera carpeta bajo la raíz.</summary>
    public static string SolutionFolder(string root, string dir)
    {
        var rootN = Paths.Normalize(root);
        var d = Paths.Normalize(dir);
        while (d.Length >= rootN.Length)
        {
            if (HasSolution(d)) return d;
            if (string.Equals(d, rootN, Paths.Comparison)) break;
            var parent = Path.GetDirectoryName(d);
            if (string.IsNullOrEmpty(parent)) break;
            d = Paths.Normalize(parent);
        }
        var first = Paths.Relative(root, dir).Split('/')[0];
        if (first.Length > 0 && first != ".") return Path.Combine(root, first);
        return dir;
    }

    private static bool HasSolution(string dir)
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
    internal static string UrlLaunchSettings(string dirProject)
    {
        var text = FileTree.Read(Path.Combine(dirProject, "Properties", "launchSettings.json"));
        var ms = Regex.Matches(text, "\"applicationUrl\"\\s*:\\s*\"([^\"]+)\"");
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
    internal static string? ConnectionName(string dirProject, string dirSolution)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dirProject, "appsettings*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                var m = Regex.Match(FileTree.Read(f), "\"ConnectionStrings\"\\s*:\\s*\\{\\s*\"([^\"]+)\"");
                if (m.Success) return m.Groups[1].Value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        foreach (var f in FileTree.Search(dirSolution, new[] { "*.cs" }, 5))
        {
            var m = Regex.Match(FileTree.Read(f), "GetConnectionString\\(\\s*\"([^\"]+)\"");
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    // ------------------------------------------------------------------ Web

    private static WebConfig? DetectWeb(string root, List<string> files, List<string> findings)
    {
        var pkgs = files
            .Where(f => Path.GetFileName(f).Equals("package.json", StringComparison.OrdinalIgnoreCase))
            .Where(f => Regex.IsMatch(FileTree.Read(f), "\"@angular/core\"", Ci))
            .OrderBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (pkgs.Count == 0) return null;

        var dirWeb = Path.GetDirectoryName(pkgs[0])!;
        var pm = PackageManager(root, dirWeb);
        var port = 4200;
        var m = Regex.Match(FileTree.Read(Path.Combine(dirWeb, "angular.json")), "\"port\"\\s*:\\s*(\\d+)");
        if (m.Success) port = int.Parse(m.Groups[1].Value);
        m = Regex.Match(FileTree.Read(pkgs[0]), "\"start\"\\s*:\\s*\"[^\"]*--port[= ](\\d+)");
        if (m.Success) port = int.Parse(m.Groups[1].Value);

        var (start, test, build, install) = pm switch
        {
            "pnpm" => ("pnpm start", "pnpm test --watch=false", "pnpm build", "pnpm install"),
            "yarn" => ("yarn start", "yarn test --watch=false", "yarn build", "yarn install"),
            _ => ("npm start", "npm test -- --watch=false", "npm run build", "npm install"),
        };
        var web = new WebConfig
        {
            Folder = Paths.Relative(root, dirWeb),
            Url = $"http://localhost:{port}",
            Start = start,
            Test = test,
            Build = build,
            Install = install,
        };
        findings.Add($"Web (Angular):   {web.Folder}  ->  {web.Url}  ({pm})");
        return web;
    }

    /// <summary>pnpm, yarn o npm: por el lockfile (en la carpeta de la web o en la raíz) o por el campo packageManager.</summary>
    internal static string PackageManager(string root, string dir)
    {
        foreach (var d in new[] { dir, root })
        {
            if (File.Exists(Path.Combine(d, "pnpm-lock.yaml"))) return "pnpm";
            if (File.Exists(Path.Combine(d, "yarn.lock"))) return "yarn";
            if (File.Exists(Path.Combine(d, "package-lock.json"))) return "npm";
        }
        var m = Regex.Match(FileTree.Read(Path.Combine(dir, "package.json")), "\"packageManager\"\\s*:\\s*\"(pnpm|yarn|npm)@");
        return m.Success ? m.Groups[1].Value : "npm";
    }

    // ------------------------------------------------------------------ Flutter

    private static MobileConfig? DetectMobile(string root, List<string> files, List<TaskConfig> tasks, List<string> findings)
    {
        var pubs = files
            .Where(f => Path.GetFileName(f).Equals("pubspec.yaml", StringComparison.OrdinalIgnoreCase))
            .Where(f => Regex.IsMatch(FileTree.Read(f), @"^\s*sdk:\s*flutter\s*$", Ci | RegexOptions.Multiline))
            .OrderBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (pubs.Count == 0) return null;

        var dirMov = Path.GetDirectoryName(pubs[0])!;
        var pub = FileTree.Read(pubs[0]);
        var format = "dart format lib";
        if (Directory.Exists(Path.Combine(dirMov, "test"))) format += " test";

        var mobile = new MobileConfig
        {
            Folder = Paths.Relative(root, dirMov),
            AndroidSdk = "",
            Avd = "",
            ArgsRun = "",
            Parse = "flutter analyze",
            Test = "flutter test",
            Format = format,
            Generate = Regex.IsMatch(pub, "build_runner", Ci) ? "dart run build_runner build --delete-conflicting-outputs" : "",
        };
        findings.Add($"Móvil (Flutter): {mobile.Folder}");
        tasks.Add(new TaskConfig { Name = "Flutter: compilar APK (release)", Folder = mobile.Folder, Command = "flutter build apk --release" });
        return mobile;
    }

    // ------------------------------------------------------------------ Postgres y "levantar todo"

    /// <summary>Agrega Postgres a la configuración y la cadena de conexión (ConnectionStrings__X) a la API.</summary>
    public static void ApplyPostgres(ProjectConfig config, PostgresConfig pg, string nameConnection)
    {
        config.Postgres = pg;
        if (config.Api is not null)
        {
            config.Api.Env ??= new Dictionary<string, string>();
            config.Api.Env[$"ConnectionStrings__{nameConnection}"] = ConnectionString(pg);
        }
        config.StartAll = DefaultStartAll(config);
    }

    public static string ConnectionString(PostgresConfig pg) =>
        $"Host=127.0.0.1;Port={pg.EfPort};Database={pg.EfDatabase};Username={pg.EfUser};Password={pg.EfPassword}";

    public static List<string> DefaultStartAll(ProjectConfig c)
    {
        var l = new List<string>();
        if (c.Postgres is not null) l.Add("postgres");
        if (c.Api is not null) l.Add("api");
        if (c.Web is not null) l.Add("web");
        if (c.Mobile is not null) l.Add("emulador");
        return l;
    }
}
