using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using StackDev.Core.Proc;
using StackDev.Core.Servers;
using StackDev.Core.Ui;

namespace StackDev.Core.Deploy;

/// <summary>
/// Puts a new version of an API on a server that was provisioned before: publish, inspect, ask, upload, replace with a backup,
/// check it is healthy and go back by itself if it is not. Nothing here depends on the user's own deploy scripts.
/// </summary>
public static class DeployService
{
    private static string WorkRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), State.StateStore.FolderName, "publish");

    public static DeployResult Deploy(DeployRequest req, IDeployDecisions decisions, Action<ProgressEvent> progress, CancellationToken ct)
    {
        void P(string phase, string message, Level level = Level.Normal, int percent = -1) => progress(new ProgressEvent(phase, message, level, percent));

        // ---- validation
        if (!Sh.Service.IsMatch(req.Service)) return new(DeployOutcome.Failed, "El nombre del servicio no es válido (minúsculas, números y guiones, empezando con letra).");
        if (!Sh.Dll.IsMatch(req.Dll)) return new(DeployOutcome.Failed, "El nombre del .dll no es válido.");
        if (!File.Exists(req.Project)) return new(DeployOutcome.Failed, "No existe el proyecto: " + req.Project);
        if (!HostKeys.IsTrusted(req.Server)) return new(DeployOutcome.Failed, "El servidor aún no está verificado: prueba la conexión y confirma su huella.");

        var work = Path.Combine(WorkRoot, $"{req.Service}-{DateTime.Now:yyyyMMdd-HHmmss}");
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(outDir);
        try
        {
            // ---- 1) publish
            P("Compilar", "dotnet publish (Release)…", Level.Title, 3);
            if (!Publish(req.Project, outDir, ct, line => P("Compilar", line, Level.Muted)))
            {
                if (ct.IsCancellationRequested) return new(DeployOutcome.Cancelled, "Cancelado.");
                P("Compilar", "La compilación falló.", Level.Error);
                if (!decisions.CleanAndRetry()) return new(DeployOutcome.Failed, "La compilación falló. Revisa los errores del registro.");
                CleanBuildFolders(req.Project);
                P("Compilar", "Carpetas bin y obj borradas; compilando de nuevo…", Level.Info);
                if (!Publish(req.Project, outDir, ct, line => P("Compilar", line, Level.Muted)))
                    return new(DeployOutcome.Failed, "La compilación volvió a fallar. Revisa los errores del registro.");
            }
            P("Compilar", "Publish correcto.", Level.Ok, 30);

            // ---- 2) inspect the result
            P("Revisar", "Revisando lo que se va a subir…", Level.Title, 32);
            if (!File.Exists(Path.Combine(outDir, req.Dll)))
                return new(DeployOutcome.Failed, $"Tras compilar no está {req.Dll}. " + SuggestDll(outDir));
            var runtime = ReadRuntimeMajor(outDir, req.Dll);

            var findings = PublishGuard.Scan(outDir);
            var blocked = findings.Where(f => f.Level == GuardLevel.Block).ToList();
            var removed = new List<string>();
            var routine = blocked.Where(f => PublishGuard.IsRoutine(f.RelativePath)).ToList();
            var surprising = blocked.Except(routine).ToList();
            if (routine.Count > 0)
            {
                removed.AddRange(PublishGuard.RemoveBlocked(outDir, routine));
                P("Revisar", $"Se dejan fuera {routine.Count} archivo(s) de desarrollo (solo en la copia temporal): {string.Join(", ", routine.Select(f => f.RelativePath).Take(3))}{(routine.Count > 3 ? "…" : "")}", Level.Info);
            }
            if (surprising.Count > 0)
            {
                foreach (var b in surprising) P("Revisar", $"✘ {b.RelativePath}: {b.Reason}", Level.Error);
                if (decisions.BlockedFiles(surprising) == GuardDecision.Cancel)
                    return new(DeployOutcome.Cancelled, "Cancelado: el publish contiene archivos que no deben subirse.");
                var gone = PublishGuard.RemoveBlocked(outDir, surprising);
                removed.AddRange(gone);
                P("Revisar", $"Quité {gone.Count} archivo(s) del publish (la copia temporal, no tu proyecto).", Level.Warning);
            }
            var warnings = findings.Where(f => f.Level == GuardLevel.Warn).ToList();
            foreach (var w in warnings) P("Revisar", $"⚠ {w.RelativePath}: {w.Reason}", Level.Warning);
            var files = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories).ToList();
            var size = files.Sum(f => new FileInfo(f).Length);
            P("Revisar", $"{files.Count} archivos · {Mb(size)}", Level.Ok, 38);
            if (ct.IsCancellationRequested) return new(DeployOutcome.Cancelled, "Cancelado.");

            // ---- 3) the server
            P("Servidor", "Conectando y revisando el servidor…", Level.Title, 40);
            using var session = new RemoteSession(req.Server);
            if (session.Open(ct) is { } openError) return new(DeployOutcome.Failed, openError);
            var pre = session.Execute("preflight", new[] { "--svc", req.Service }, null, ct);
            if (!pre.Ok) return new(DeployOutcome.Failed, pre.LaunchError ?? pre.FailMessage ?? "No se pudo revisar el servidor.");
            if (pre.Value("service_exists") != "yes")
                return new(DeployOutcome.NeedsProvision, $"El servicio «{req.Service}» no existe en {req.Server.Name}. Aprovisiónalo primero.");
            var known = pre.Value("runtimes").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => int.TryParse(r, out var n) ? n : -1).ToList();
            if (runtime is int rt && known.Count > 0 && !known.Contains(rt))
                return new(DeployOutcome.Failed, $"El servidor tiene el runtime de ASP.NET Core {string.Join(", ", known)} pero la API necesita el {rt}. Instálalo en el servidor (por ejemplo: sudo dnf install -y aspnetcore-runtime-{rt}.0).");
            P("Servidor", "Servicio encontrado y servidor listo.", Level.Ok, 45);
            var content = ContentHash(outDir);
            if (!req.Force && content.Length > 0 && string.Equals(pre.Value("deployed_content"), content, StringComparison.OrdinalIgnoreCase))
            {
                var running = pre.Value("active") == "active";
                P("Servidor", "Esta misma versión ya está subida: no hace falta volver a subirla.", Level.Ok, 100);
                return new(DeployOutcome.AlreadyDeployed, running
                    ? $"Esta versión ya está subida y activa en {req.Server.Name}. No se volvió a subir."
                    : $"Esta versión ya está subida en {req.Server.Name}, pero el servicio está detenido. No se volvió a subir.");
            }

            // ---- 4) confirmation
            var plan = new DeployPlan(req.Server, req.Service, req.Dll, req.Domain, Path.GetFileNameWithoutExtension(req.Project),
                files.Count, size, runtime, pre.Value("active") == "active", int.TryParse(pre.Value("backups"), out var nb) ? nb : 0, warnings, removed);
            if (!decisions.Confirm(plan)) return new(DeployOutcome.Cancelled, "Cancelado antes de tocar el servidor.");
            if (ct.IsCancellationRequested) return new(DeployOutcome.Cancelled, "Cancelado.");

            // ---- 5) package + upload
            P("Subir", "Empaquetando…", Level.Title, 48);
            var bundle = Path.Combine(work, "bundle.tgz");
            Pack(outDir, bundle);
            var sha = Sha256(bundle);
            P("Subir", $"Paquete {Mb(new FileInfo(bundle).Length)} · SHA-256 {sha[..12]}…", Level.Muted, 52);
            if (session.Upload(bundle, "bundle.tgz", ct: ct) is { } uploadError) return new(DeployOutcome.Failed, "No se pudo subir: " + uploadError);
            P("Subir", "Subida completa.", Level.Ok, 62);

            // ---- 6) replace on the server. From here on it is not cancelled: stopping halfway is what rollback exists to avoid.
            P("Servidor", "Reemplazando en el servidor (no se puede cancelar a partir de aquí; si algo falla vuelve solo a la versión anterior)…", Level.Title, 63);
            var args = new List<string>
            {
                "--svc", req.Service, "--dll", req.Dll, "--run-dir", session.RunDir!, "--bundle", session.RunDir + "/bundle.tgz",
                "--sha256", sha, "--content", content, "--keep", req.Keep.ToString(), "--wait", req.HealthWaitSeconds.ToString(),
            };
            if (runtime is int major) { args.Add("--runtime"); args.Add(major.ToString()); }
            var result = session.Execute("deploy", args, e => Relay(e, 63, 35, progress), CancellationToken.None);

            switch (result.ExitCode)
            {
                case 0 when result.Done:
                {
                    P("Verificar", "Comprobando el servicio…", Level.Title, 97);
                    ApiStatus? status = null;
                    try { status = ServerService.State(req.Server, req.Service, req.Domain); } catch (ArgumentException) { }
                    if (status?.Http is int h && (h >= 500)) P("Verificar", $"⚠ HTTPS responde {h}. El servicio está sano en el servidor, revisa el proxy o el dominio.", Level.Warning);
                    P("Listo", "Despliegue terminado.", Level.Ok, 100);
                    return new(DeployOutcome.Success, "Despliegue terminado.", result.Value("backup"), status);
                }
                case 20: return new(DeployOutcome.RolledBack, result.FailMessage ?? "La versión nueva falló; volvió la anterior.", Missing: MissingSettings.Parse(result.Value("missing")));
                case 24: return new(DeployOutcome.NotReady, result.FailMessage ?? "La versión nueva no arranca con la configuración actual. No se cambió nada.", Missing: MissingSettings.Parse(result.Value("missing")));
                case 22: return new(DeployOutcome.StartFailed, result.FailMessage ?? "Los archivos están en el servidor pero el servicio no arrancó.", Missing: MissingSettings.Parse(result.Value("missing")));
                case 21: return new(DeployOutcome.RollbackFailed, result.FailMessage ?? "La versión nueva falló y no se pudo volver a la anterior.", Missing: MissingSettings.Parse(result.Value("missing")));
                case 12: return new(DeployOutcome.Failed, (result.FailMessage ?? "La subida llegó dañada.") + " No se cambió nada.");
                default:
                    return new(DeployOutcome.Failed, result.LaunchError ?? result.FailMessage ?? $"El servidor terminó con el código {result.ExitCode}.");
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // ------------------------------------------------------------ rollback and queries

    public static List<RemoteBackup> ListBackups(Server server, string service, out string? error, CancellationToken ct = default)
    {
        error = null;
        if (!Sh.Service.IsMatch(service)) { error = "Nombre de servicio no válido."; return new(); }
        using var session = new RemoteSession(server);
        if (session.Open(ct) is { } e) { error = e; return new(); }
        var r = session.Execute("backups", new[] { "--svc", service }, null, ct);
        if (!r.Ok) error = r.LaunchError ?? r.FailMessage ?? "No se pudo consultar.";
        return r.Backups;
    }

    /// <summary>Goes back to a saved version (the latest one when <paramref name="backupId"/> is null).</summary>
    public static DeployResult Rollback(Server server, string service, string? backupId, Action<ProgressEvent> progress, CancellationToken ct = default)
    {
        if (!Sh.Service.IsMatch(service)) return new(DeployOutcome.Failed, "Nombre de servicio no válido.");
        using var session = new RemoteSession(server);
        if (session.Open(ct) is { } e) return new(DeployOutcome.Failed, e);
        var args = new List<string> { "--svc", service, "--wait", "30" };
        if (!string.IsNullOrEmpty(backupId)) { args.Add("--to"); args.Add(backupId); }
        var r = session.Execute("rollback", args, ev => Relay(ev, 10, 85, progress), CancellationToken.None);
        if (r.Ok) return new(DeployOutcome.Success, "Versión anterior restaurada.", r.Value("restored"));
        return r.ExitCode == 21
            ? new(DeployOutcome.RollbackFailed, r.FailMessage ?? "La versión restaurada tampoco arrancó bien.")
            : new(DeployOutcome.Failed, r.LaunchError ?? r.FailMessage ?? "No se pudo volver atrás.");
    }

    /// <summary>Longest a server check may take before it is reported as not answering.</summary>
    public const int PreflightLimitSec = 60;

    /// <summary>Longest the trial start of an API may take (the script itself waits about 15 s for it).</summary>
    public const int CheckLimitSec = 90;

    /// <summary>Facts about the server and one service name (does it exist, which runtimes, which port is free...).</summary>
    public static RemoteResult Preflight(Server server, string service, CancellationToken ct = default, Action<RemoteEvent>? onEvent = null)
    {
        using var session = new RemoteSession(server);
        if (session.Open(ct) is { } e) return new RemoteResult { LaunchError = e };
        return session.Execute("preflight", new[] { "--svc", service }, onEvent, ct, timeoutSec: PreflightLimitSec, idleSec: 40);
    }

    /// <summary>
    /// A fingerprint of what is about to be uploaded: the names and contents of the files, ignoring dates and the order of the archive,
    /// so building the same code twice gives the same value.
    /// </summary>
    public static string ContentHash(string dir)
    {
        if (!Directory.Exists(dir)) return "";
        var lines = new List<string>();
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            using var fs = File.OpenRead(f);
            var h = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)).ToLowerInvariant();
            lines.Add(Path.GetRelativePath(dir, f).Replace('\\', '/') + "\0" + h);
        }
        lines.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines)))).ToLowerInvariant();
    }

    /// <summary>Starts the installed application for a few seconds with the real settings, to know whether it would start. Changes nothing.</summary>
    public static DeployResult Check(Server server, string service, string dll, CancellationToken ct = default, Action<RemoteEvent>? onEvent = null)
    {
        if (!Sh.Service.IsMatch(service) || !Sh.Dll.IsMatch(dll)) return new(DeployOutcome.Failed, "Nombre de servicio o .dll no válido.");
        if (!HostKeys.IsTrusted(server)) return new(DeployOutcome.Failed, "El servidor aún no está verificado.");
        using var session = new RemoteSession(server);
        if (session.Open(ct) is { } e) return new(DeployOutcome.Failed, e);
        var r = session.Execute("check", new[] { "--svc", service, "--dll", dll }, onEvent, ct, timeoutSec: CheckLimitSec, idleSec: 60);
        if (r.Ok) return new(DeployOutcome.Success, "La API arranca con su configuración actual.");
        var missing = MissingSettings.Parse(r.Value("missing"));
        return r.ExitCode == 24
            ? new(DeployOutcome.NotReady, r.FailMessage ?? "La API no arranca con su configuración actual.", Missing: missing)
            : new(DeployOutcome.Failed, r.LaunchError ?? r.FailMessage ?? $"El servidor terminó con el código {r.ExitCode}.");
    }

    /// <summary>API services already installed on the server (to pick the right one instead of remembering its name).</summary>
    public static List<RemoteApi> DiscoverApis(Server server, out string? error, CancellationToken ct = default)
    {
        error = null;
        using var session = new RemoteSession(server);
        if (session.Open(ct) is { } e) { error = e; return new(); }
        var r = session.Execute("discover", Array.Empty<string>(), null, ct);
        if (!r.Ok) error = r.LaunchError ?? r.FailMessage ?? "No se pudo consultar.";
        return r.Apis;
    }

    // ------------------------------------------------------------ pieces

    /// <summary>Translates what the script prints into progress; steps move the bar between <paramref name="from"/> and <paramref name="from"/>+<paramref name="span"/>.</summary>
    public static void Relay(RemoteEvent e, int from, int span, Action<ProgressEvent> progress)
    {
        switch (e.Kind)
        {
            case RemoteEventKind.Step:
                progress(new ProgressEvent("Servidor", e.Text, Level.Title, from + (int)Math.Round(span * (e.Step - 1) / (double)Math.Max(1, e.Total)), e.Step, e.Total));
                break;
            case RemoteEventKind.Info: progress(new ProgressEvent("Servidor", e.Text, Level.Info)); break;
            case RemoteEventKind.Warn: progress(new ProgressEvent("Servidor", e.Text, Level.Warning)); break;
            case RemoteEventKind.Fail: progress(new ProgressEvent("Servidor", e.Text, Level.Error)); break;
            case RemoteEventKind.Log: if (!string.IsNullOrWhiteSpace(e.Text)) progress(new ProgressEvent("Servidor", e.Text, Level.Muted)); break;
        }
    }

    private static bool Publish(string project, string outDir, CancellationToken ct, Action<string> line)
    {
        var code = ProcessRunner.RunCapturing("dotnet",
            new[] { "publish", project, "-c", "Release", "-o", outDir, "-p:DebugType=none", "--nologo", "-v", "minimal" },
            Path.GetDirectoryName(project), null, ct, l => { if (!string.IsNullOrWhiteSpace(l)) line(SecretMask.Mask(l)); });
        return code == 0;
    }

    private static void CleanBuildFolders(string project)
    {
        var dir = Path.GetDirectoryName(project)!;
        foreach (var d in new[] { "bin", "obj" })
        {
            try { var p = Path.Combine(dir, d); if (Directory.Exists(p)) Directory.Delete(p, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string SuggestDll(string outDir)
    {
        var dlls = Directory.EnumerateFiles(outDir, "*.runtimeconfig.json").Select(f => Path.GetFileName(f)[..^".runtimeconfig.json".Length] + ".dll").ToList();
        return dlls.Count == 0 ? "¿El nombre del .dll es correcto?" : "El publish tiene: " + string.Join(", ", dlls) + ".";
    }

    /// <summary>Major version of ASP.NET Core the app was built for, from its runtimeconfig.json.</summary>
    public static int? ReadRuntimeMajor(string outDir, string dll)
    {
        var path = Path.Combine(outDir, Path.GetFileNameWithoutExtension(dll) + ".runtimeconfig.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("runtimeOptions", out var opts)) return null;
            var frameworks = new List<JsonElement>();
            if (opts.TryGetProperty("framework", out var one)) frameworks.Add(one);
            if (opts.TryGetProperty("frameworks", out var many) && many.ValueKind == JsonValueKind.Array) frameworks.AddRange(many.EnumerateArray());
            foreach (var f in frameworks)
            {
                var name = f.TryGetProperty("name", out var n) ? n.GetString() : null;
                var version = f.TryGetProperty("version", out var v) ? v.GetString() : null;
                if (name == "Microsoft.AspNetCore.App" && version is not null && Regex.Match(version, @"^(\d+)\.") is { Success: true } m)
                    return int.Parse(m.Groups[1].Value);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return null;
    }

    /// <summary>tar.gz of the folder's contents (files at the root of the archive).</summary>
    public static void Pack(string dir, string destination)
    {
        using var file = File.Create(destination);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        TarFile.CreateFromDirectory(dir, gzip, includeBaseDirectory: false);
    }

    public static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0") + " MB";
}
