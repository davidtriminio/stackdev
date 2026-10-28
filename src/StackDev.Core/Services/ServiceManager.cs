using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using StackDev.Core.Config;
using StackDev.Core.Docker;
using StackDev.Core.Proc;
using StackDev.Core.Ui;

namespace StackDev.Core.Services;

public enum ServiceCode { Stopped, Starting, Running, Error, NoConfig }

public sealed record ServiceState(ServiceCode Code, string Text, string? Url = null, int? Pid = null)
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
        if (live is not null) return new(ServiceCode.Starting, "ARRANCANDO", url, live.Id);
        var code = ReadMark(key);
        if (code is not null && code != "0")
            return new(ServiceCode.Error, $"TERMINÓ (código {code}): revisa el log");
        return new(ServiceCode.Stopped, "DETENIDA");
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
        if (Ports.IsListening(_s.Ctx.Port(ProjectComponent.Api)))
        {
            _s.Ui.Write($"La API ya está corriendo en {_s.Ctx.Url(ProjectComponent.Api)}.", Level.Warning);
            return true;
        }
        if (!withoutPostgres && _s.Ctx.Has(ProjectComponent.Postgres) && _s.Postgres.State().Code != PostgresCode.Running)
        {
            _s.Ui.Write("La API necesita Postgres; lo inicio primero.", Level.Info);
            if (!_s.Postgres.Start() && !_s.Ui.Confirm("Postgres no arrancó. ¿Iniciar la API de todos modos?", false)) return false;
        }
        var cmd = _s.Command(ProjectComponent.Api, "iniciar");
        if (cmd is null) { _s.Ui.Write("Falta api.iniciar en la configuración.", Level.Error); return false; }
        _s.Ui.Write("Iniciando la API en segundo plano (su salida va al log)...", Level.Info);
        if (!StartHidden("api", _s.Ctx.Folder(ProjectComponent.Api), cmd, _s.Ctx.Config.Api?.Env)) return false;
        return !wait || _s.All.Wait(new[] { "api" });
    }

    public bool StartWeb(bool wait = true)
    {
        if (!_s.AssertComp(ProjectComponent.Web)) return false;
        if (Ports.IsListening(_s.Ctx.Port(ProjectComponent.Web)))
        {
            _s.Ui.Write($"La web ya está corriendo en {_s.Ctx.Url(ProjectComponent.Web)}.", Level.Warning);
            return true;
        }
        if (!EnsureNodeModules()) return false;
        var cmd = _s.Command(ProjectComponent.Web, "iniciar");
        if (cmd is null) { _s.Ui.Write("Falta web.iniciar en la configuración.", Level.Error); return false; }
        _s.Ui.Write("Iniciando la web en segundo plano (su salida va al log)...", Level.Info);
        if (!StartHidden("web", _s.Ctx.Folder(ProjectComponent.Web), cmd, null)) return false;
        return !wait || _s.All.Wait(new[] { "web" });
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
