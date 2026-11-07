using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using StackDev.Core.Config;
using StackDev.Core.Docker;
using StackDev.Core.Proc;
using StackDev.Core.Ui;

namespace StackDev.Core.Services;

public enum ServiceCode { Stopped, Starting, Running, Error, NoConfig, WaitingInput }

public sealed record ServiceState(ServiceCode Code, string Text, string? Url = null, int? Pid = null, string? Prompt = null)
{
    public bool Running => Code == ServiceCode.Running;
}

/// <summary>
/// API and web: they run hidden with their output going to a log file (they stay alive even if you close the tool).
/// "Running" = listening on its port; "starting" = the process is alive but not listening yet;
/// "error" = it ended with a non-zero code (saved in a marker file).
/// </summary>
public sealed class ServiceManager
{
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private readonly Session _s;

    public ServiceManager(Session s)
    {
        _s = s;
        Registry = new ProcessRegistry(Path.Combine(s.State.Dir, $"ventanas-{s.Key}.txt"));
    }

    public ProcessRegistry Registry { get; }

    public static string KeyOf(ProjectComponent c) => c switch
    {
        ProjectComponent.Api => "api", ProjectComponent.Web => "web", _ => throw new ArgumentException("Solo API y web son servicios con puerto."),
    };

    private string Mark(string key) => Path.Combine(Path.GetTempPath(), $"devmenu-{_s.Key}-{key}.exit");

    public string LogPath(string key) => Path.Combine(_s.State.Dir, "logs", _s.Key, $"{key}.log");

    // ------------------------------------------------------------ Estado

    public ServiceState State(ProjectComponent c)
    {
        if (!_s.Ctx.Has(c)) return new(ServiceCode.NoConfig, "no configurado");
        var key = KeyOf(c);
        var url = _s.Ctx.Url(c);
        var live = Registry.Live(key);
        if (Ports.IsListening(_s.Ctx.Port(c))) return new(ServiceCode.Running, "CORRIENDO", url, live?.Id);
        if (live is not null)
        {
            if (PendingPrompt(key) is { } question) return new(ServiceCode.WaitingInput, "ESPERA TU RESPUESTA", url, live.Id, question);
            return new(ServiceCode.Starting, "ARRANCANDO", url, live.Id);
        }
        var code = ReadMark(key);
        if (code is not null && code != "0")
            return new(ServiceCode.Error, $"TERMINÓ (código {code}): revisa el log");
        return new(ServiceCode.Stopped, "DETENIDA");
    }

    // What a program prints when it stops to ask something: "(Y/n)", "[y/N]", "Proceed?", "Press any key", an inquirer-style "? question".
    private static readonly Regex AsksSomething = new(
        @"\((?:Y/n|y/N|y/n|Y/N)\)|\[(?:Y/n|y/N|y/n|Y/N|yes/no)\]|\bProceed\?|Press (?:any key|Enter)|Ok to proceed|^\s*\? \S.+",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    public static bool LooksLikePrompt(string line) => AsksSomething.IsMatch(line);

    private const int QuietSeconds = 4;

    /// <summary>
    /// The question a hidden service is stuck on, if any: its output ends in something that looks like a prompt and has stayed
    /// silent for a few seconds. A service started in the background has nobody to answer, so it would wait forever, unseen.
    /// </summary>
    public string? PendingPrompt(string key)
    {
        try
        {
            var log = LogPath(key);
            if (!File.Exists(log) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(log)).TotalSeconds < QuietSeconds) return null;
            var tail = LastLines(key, 12);
            for (var i = tail.Count - 1; i >= 0; i--)
                if (LooksLikePrompt(tail[i])) return tail[i].Trim();
        }
        catch (IOException) { }
        return null;
    }

    private readonly Dictionary<string, string> _asked = new();

    /// <summary>
    /// If the service is waiting for an answer, says so and offers to continue in a terminal where it can be answered.
    /// Asked once per question. True when it was opened in a terminal.
    /// </summary>
    public bool OfferPromptHelp(ProjectComponent c)
    {
        var key = KeyOf(c);
        if (State(c) is not { Code: ServiceCode.WaitingInput, Prompt: { } question }) return false;
        if (_asked.TryGetValue(key, out var before) && before == question) return false;
        _asked[key] = question;

        var name = Session.Names[c];
        _s.Ui.Write("");
        _s.Ui.Write($"{name} está esperando una respuesta que nadie puede darle en segundo plano:", Level.Warning);
        _s.Ui.Write("  " + question, Level.Warning);
        if (!_s.Ui.Interactive) return false;
        var pick = _s.Ui.Choose($"{name} pregunta:\n{question}\n\n¿Qué hago?",
            new[] { "Abrir en una terminal para responder (recomendado)", "Seguir esperando", $"Detener {name}" }, 0);
        if (pick == 2) { Stop(c, silent: true); return false; }
        return pick == 0 && OpenInTerminal(c);
    }

    /// <summary>Stops the background process and starts the same command in a visible terminal, where its questions can be answered.</summary>
    public bool OpenInTerminal(ProjectComponent c)
    {
        var cmd = _s.Command(c, "iniciar");
        if (cmd is null) return false;
        var key = KeyOf(c);
        StopProcess(key);
        Thread.Sleep(500);
        if (c == ProjectComponent.Api) cmd = WithUrls(cmd, _s.Ctx.Url(c));
        var ok = _s.Executor.InWindow(key, $"{Session.Names[c]} - {_s.Ctx.Name}", _s.Ctx.Folder(c), cmd, c == ProjectComponent.Api ? _s.Ctx.Config.Api?.Env : null);
        _s.Ui.Write(ok ? $"{Session.Names[c]} sigue en una terminal: responde ahí." : "No se pudo abrir la terminal.", ok ? Level.Ok : Level.Error);
        return ok;
    }

    private string? ReadMark(string key)
    {
        try { var f = Mark(key); return File.Exists(f) ? File.ReadAllText(f).Trim() : null; }
        catch (IOException) { return null; }
    }

    /// <summary>"OK" if it is already listening; "" if it is still starting; otherwise the reason it is not going to start.</summary>
    public string TestReady(ProjectComponent c)
    {
        var port = _s.Ctx.Port(c);
        if (port <= 0) return "no tiene una url válida en la configuración";
        if (Ports.IsListening(port)) return "OK";
        var key = KeyOf(c);
        if (Registry.Live(key) is not null) return "";
        var code = ReadMark(key);
        return code is not null ? $"terminó con código {code} (revisa el log)" : "su proceso se cerró";
    }

    // ------------------------------------------------------------ Iniciar / detener

    public bool EnsureNodeModules()
    {
        var dir = _s.Ctx.Folder(ProjectComponent.Web);
        if (Directory.Exists(Path.Combine(dir, "node_modules"))) return true;
        var inst = _s.Command(ProjectComponent.Web, "instalar") ?? "npm install";
        _s.Ui.Write("Faltan las dependencias de la web (node_modules).", Level.Warning);
        if (!_s.Ui.Confirm($"¿Instalarlas ahora con '{inst}'?", true)) return false;
        return _s.Executor.Here(inst, dir) == 0;
    }

    public bool StartApi(bool wait = true, bool withoutPostgres = false)
    {
        if (!_s.AssertComp(ProjectComponent.Api)) return false;
        switch (CheckPort(ProjectComponent.Api))
        {
            case PortCheck.AlreadyRunning:
                _s.Ui.Write($"La API ya está corriendo en {_s.Ctx.Url(ProjectComponent.Api)}.", Level.Warning);
                return true;
            case PortCheck.Cancel: return false;
        }
        if (!withoutPostgres && _s.Ctx.Has(ProjectComponent.Postgres) && _s.Postgres.State().Code != PostgresCode.Running)
        {
            _s.Ui.Write("La API necesita Postgres; lo inicio primero.", Level.Info);
            if (!_s.Postgres.Start() && !_s.Ui.Confirm("Postgres no arrancó. ¿Iniciar la API de todos modos?", false)) return false;
        }
        var cmd = _s.Command(ProjectComponent.Api, "iniciar");
        if (cmd is null) { _s.Ui.Write("Falta api.iniciar en la configuración.", Level.Error); return false; }
        _s.Ui.Write("Iniciando la API en segundo plano (su salida va al log)...", Level.Info);
        if (!StartHidden("api", _s.Ctx.Folder(ProjectComponent.Api), WithUrls(cmd, _s.Ctx.Url(ProjectComponent.Api)), _s.Ctx.Config.Api?.Env)) return false;
        return !wait || _s.All.Wait(new[] { "api" });
    }

    /// <summary>
    /// The web dev server forwards "/api" to a port written in a proxy file of the project. When that is not the API's port every
    /// call fails, so the web is started with a copy of the file kept in stackdev's own folder (<c>--proxy-config</c>); the
    /// project's file is never touched, so nothing shows up in its git.
    /// </summary>
    private string WithProxyOverride(string command)
    {
        if (!_s.Ctx.Has(ProjectComponent.Api) || command.Contains("--proxy-config", StringComparison.Ordinal)) return command;
        var apiPort = _s.Ctx.Port(ProjectComponent.Api);
        var stale = WebProxy.Find(_s.Ctx.Folder(ProjectComponent.Web), apiPort);
        if (stale.Count == 0) return command;
        var source = stale.OrderBy(x => x.File.Length).First();
        try
        {
            var copy = WebProxy.WriteCopy(source.File, apiPort, Path.Combine(_s.State.Dir, "proxy", _s.Key));
            _s.Ui.Write($"La web llamaba a la API en el puerto {source.Port} y corre en el {apiPort}: la inicio con un proxy temporal (tu {_s.Rel(source.File)} no se toca).", Level.Muted);
            return AppendArg(command, $"--proxy-config \"{copy}\"");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _s.Ui.Write($"La web apunta al puerto {source.Port} pero la API corre en el {apiPort}, y no pude preparar un proxy temporal: {ex.Message}", Level.Warning);
            return command;
        }
    }

    /// <summary>Adds an argument to a script command: <c>npm start</c> needs <c>--</c> before it, the others take it as is.</summary>
    public static string AppendArg(string command, string arg) =>
        command.StartsWith("npm ", StringComparison.OrdinalIgnoreCase) && !command.Contains(" -- ", StringComparison.Ordinal)
            ? $"{command} -- {arg}"
            : $"{command} {arg}";

    public bool StartWeb(bool wait = true)
    {
        if (!_s.AssertComp(ProjectComponent.Web)) return false;
        switch (CheckPort(ProjectComponent.Web))
        {
            case PortCheck.AlreadyRunning:
                _s.Ui.Write($"La web ya está corriendo en {_s.Ctx.Url(ProjectComponent.Web)}.", Level.Warning);
                return true;
            case PortCheck.Cancel: return false;
        }
        if (!EnsureNodeModules()) return false;
        var cmd = _s.Command(ProjectComponent.Web, "iniciar");
        if (cmd is null) { _s.Ui.Write("Falta web.iniciar en la configuración.", Level.Error); return false; }
        _s.Ui.Write("Iniciando la web en segundo plano (su salida va al log)...", Level.Info);
        if (!StartHidden("web", _s.Ctx.Folder(ProjectComponent.Web), WithProxyOverride(cmd), null)) return false;
        return !wait || _s.All.Wait(new[] { "web" });
    }

    private static readonly Regex DotnetRun = new(@"\bdotnet\s+run\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// <c>dotnet run</c> listens where launchSettings.json says, whatever the configured URL or ASPNETCORE_URLS is (the launch
    /// profile wins over the environment). <c>--urls</c> after <c>--</c> is the one thing that beats it, so the port stackdev
    /// checks, shows and stops is the port the API really uses.
    /// </summary>
    public static string WithUrls(string command, string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return command;
        if (!DotnetRun.IsMatch(command) || command.Contains("--urls", StringComparison.OrdinalIgnoreCase)
            || command.Contains("--no-launch-profile", StringComparison.OrdinalIgnoreCase)) return command;
        var origin = $"{u.Scheme}://{u.Host}:{u.Port}";
        return command.Contains(" -- ", StringComparison.Ordinal) || command.EndsWith(" --", StringComparison.Ordinal)
            ? $"{command} --urls {origin}"
            : $"{command} -- --urls {origin}";
    }

    private enum PortCheck { Free, AlreadyRunning, Cancel }

    /// <summary>
    /// Before starting: is the component's port free? If somebody else holds it (or Windows / a container has reserved it) the user
    /// chooses: another free port (saved in the configuration), a port typed by hand, close whoever holds it, or use it as it is.
    /// Without a person to ask, a busy port counts as "already running", as it always did.
    /// </summary>
    private PortCheck CheckPort(ProjectComponent c)
    {
        var port = _s.Ctx.Port(c);
        if (port <= 0) return PortCheck.Free;
        var key = KeyOf(c);
        var listening = Ports.IsListening(port);
        if (listening && Registry.Live(key) is not null) return PortCheck.AlreadyRunning;
        if (!_s.Ui.Interactive) return listening ? PortCheck.AlreadyRunning : PortCheck.Free;

        var name = Session.Names[c];
        for (var round = 0; round < 5; round++)
        {
            var map = _s.Free.Take(ignoreComponent: c);
            var why = map.Conflict(port);
            if (why is null) return PortCheck.Free;

            var fresh = map.Suggest(port + 1);
            var options = new List<string>();
            var actions = new List<string>();
            if (fresh is not null) { options.Add($"Usar el puerto {fresh} (libre)"); actions.Add("suggested"); }
            options.Add("Escribir otro puerto…"); actions.Add("custom");
            if (listening && Ports.Pids(port).Count > 0) { options.Add("Cerrar el proceso que lo usa"); actions.Add("kill"); }
            if (listening) { options.Add($"Ya es {name}: usarla tal como está"); actions.Add("use"); }

            var pick = _s.Ui.Choose($"El puerto {port} de {name} no está libre: {why}.\n¿Qué hago antes de iniciar?", options);
            if (pick < 0 || pick >= actions.Count) { _s.Ui.Write("Cancelado.", Level.Muted); return PortCheck.Cancel; }
            switch (actions[pick])
            {
                case "use": return PortCheck.AlreadyRunning;
                case "kill":
                    foreach (var id in Ports.Pids(port)) ProcessRunner.KillTree(id);
                    Thread.Sleep(800);
                    listening = Ports.IsListening(port);
                    continue;
                case "custom":
                    var typed = _s.Ui.Ask($"Puerto para {name} (1024–65535)", fresh?.ToString());
                    if (typed is null) { _s.Ui.Write("Cancelado.", Level.Muted); return PortCheck.Cancel; }
                    if (!int.TryParse(typed.Trim(), out var chosen) || chosen is < 1024 or > 65535)
                    {
                        _s.Ui.Write("Ese no es un puerto válido.", Level.Error);
                        continue;
                    }
                    if (map.Conflict(chosen) is { } other)
                    {
                        _s.Ui.Write($"El puerto {chosen} tampoco está libre: {other}.", Level.Warning);
                        continue;
                    }
                    fresh = chosen;
                    break;
            }
            _s.Free.Apply(new[] { new PortsService.PortChange(c, port, fresh!.Value, why) });
            port = _s.Ctx.Port(c);
            listening = Ports.IsListening(port);
        }
        return PortCheck.Cancel;
    }

    /// <summary>
    /// Launches the command in a hidden cmd with its output going to a file. When it ends it writes its code to the marker.
    /// With no window of its own it does not receive this console's Ctrl+C, so it stays alive if you close the tool.
    /// </summary>
    private bool StartHidden(string key, string folder, string command, IReadOnlyDictionary<string, string>? env)
    {
        var log = LogPath(key);
        var mark = Mark(key);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            if (File.Exists(log)) File.Copy(log, Path.ChangeExtension(log, ".anterior.log"), overwrite: true);
            File.WriteAllText(log, $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {command}  ({folder}) ===\n", new UTF8Encoding(false));
            if (File.Exists(mark)) File.Delete(mark);
        }
        catch (IOException ex) { _s.Ui.Write($"No pude preparar el log: {ex.Message}", Level.Error); return false; }

        var psi = BackgroundLaunch.Info(command, folder, log, mark);
        psi.Environment["NO_COLOR"] = "1";
        foreach (var (k, v) in _s.EnvFor(folder) ?? new Dictionary<string, string>()) psi.Environment[k] = v;
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;

        try
        {
            var p = Process.Start(psi)!;
            Registry.Save(key, p);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _s.Ui.Write($"No se pudo iniciar: {ex.Message}", Level.Error);
            return false;
        }
    }

    /// <summary>Closes the whole process tree and, if the port is still busy, asks whether to close whoever holds it.</summary>
    public void Stop(ProjectComponent c, bool silent = false)
    {
        if (!_s.Ctx.Has(c)) return;
        var key = KeyOf(c);
        var name = Session.Names[c];
        var was = StopProcess(key);
        if (was) Thread.Sleep(800);

        var port = _s.Ctx.Port(c);
        foreach (var id in Ports.Pids(port))
        {
            string n = "?";
            try { using var proc = Process.GetProcessById(id); n = proc.ProcessName; } catch (ArgumentException) { }
            if (_s.Ui.Confirm($"El puerto {port} lo usa {n} (PID {id}). ¿Cerrarlo?", true))
            {
                ProcessRunner.KillTree(id);
                was = true;
            }
        }
        try { File.Delete(Mark(key)); } catch (IOException) { }
        if (was) _s.Ui.Write($"{name} detenida.", Level.Ok);
        else if (!silent) _s.Ui.Write($"{name} no estaba corriendo.", Level.Muted);
    }

    /// <summary>The keys of the flutter run sessions: "app" (emulator) and "app-serial" (other devices).</summary>
    public static bool IsAppKey(string k) => k == "app" || k.StartsWith("app-", StringComparison.Ordinal);

    public List<string> LiveApps() => Registry.Keys().Where(k => IsAppKey(k) && Registry.Live(k) is not null).ToList();

    /// <summary>Closes every flutter run session. Returns how many there were.</summary>
    public int StopApps()
    {
        var n = 0;
        foreach (var k in Registry.Keys().Where(IsAppKey).ToList())
            if (StopProcess(k)) n++;
        return n;
    }

    /// <summary>Closes the process tree registered under that key. Returns true if one was alive.</summary>
    public bool StopProcess(string key)
    {
        var p = Registry.Live(key);
        if (p is null) return false;
        ProcessRunner.KillTree(p);
        Registry.Forget(key);
        return true;
    }

    public void Restart(ProjectComponent c)
    {
        Stop(c, silent: true);
        if (c == ProjectComponent.Api) StartApi(); else StartWeb();
    }

    public void OpenInBrowser(ProjectComponent c)
    {
        if (!_s.AssertComp(c)) return;
        var url = _s.Ctx.UrlToOpen(c);
        if (url is null) return;
        if (!Ports.IsListening(_s.Ctx.Port(c))) _s.Ui.Write($"Ojo: {Session.Names[c]} no está corriendo.", Level.Warning);
        _s.Executor.OpenBrowser(url);
    }

    // ------------------------------------------------------------ Logs

    public static string StripAnsi(string text) => Ansi.Replace(text, "");

    /// <summary>The last lines of the log (without colour codes).</summary>
    public List<string> LastLines(string key, int n)
    {
        var f = LogPath(key);
        if (!File.Exists(f)) return new();
        try
        {
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var size = Math.Min(fs.Length, 256 * 1024);
            fs.Seek(-size, SeekOrigin.End);
            var buf = new byte[size];
            var read = fs.Read(buf, 0, buf.Length);
            var text = StripAnsi(Encoding.UTF8.GetString(buf, 0, read));
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
            return lines.Count <= n ? lines : lines.GetRange(lines.Count - n, n);
        }
        catch (IOException) { return new(); }
    }

    public string? LastLine(string key) => LastLines(key, 1).FirstOrDefault();

    /// <summary>Shows the log and keeps showing what is new until cancelled (Ctrl+C).</summary>
    public void Follow(string key, int initial, CancellationToken ct, Action<string> write)
    {
        foreach (var l in LastLines(key, initial)) write(l);
        var f = LogPath(key);
        long pos = File.Exists(f) ? new FileInfo(f).Length : 0;
        var pending = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(f))
                {
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (fs.Length < pos) { pos = 0; write("--- el log se reinició ---"); }
                    if (fs.Length > pos)
                    {
                        fs.Seek(pos, SeekOrigin.Begin);
                        var buf = new byte[fs.Length - pos];
                        var n = fs.Read(buf, 0, buf.Length);
                        pos += n;
                        pending.Append(Encoding.UTF8.GetString(buf, 0, n));
                        var text = pending.ToString();
                        var cut = text.LastIndexOf('\n');
                        if (cut >= 0)
                        {
                            foreach (var l in StripAnsi(text[..cut]).Split('\n')) write(l.TrimEnd('\r'));
                            pending.Clear().Append(text[(cut + 1)..]);
                        }
                    }
                }
            }
            catch (IOException) { }
            ct.WaitHandle.WaitOne(300);
        }
    }

    /// <summary>Opens a tab (or window) that follows the log live.</summary>
    public void OpenLogInTab(string key)
    {
        var f = LogPath(key);
        if (!File.Exists(f)) { _s.Ui.Write("Todavía no hay log de este servicio.", Level.Warning); return; }
        var cmd = $"Get-Content -LiteralPath {Util.Args.PsLiteral(f)} -Wait -Tail 200 -Encoding UTF8";
        if (_s.Executor.InWindow("log-" + key, $"Log {key} - {_s.Ctx.Name}", _s.Root, cmd, waitEnter: false))
            _s.Ui.Write("Log abierto en otra pestaña.", Level.Ok);
        else _s.Ui.Write("No se pudo abrir la pestaña.", Level.Error);
    }
}
