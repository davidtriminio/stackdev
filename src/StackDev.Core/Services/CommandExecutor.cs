using System.Diagnostics;
using StackDev.Core.Config;
using StackDev.Core.Proc;
using StackDev.Core.Ui;
using StackDev.Core.Util;

namespace StackDev.Core.Services;

/// <summary>Correr comandos del proyecto: en esta consola (con tiempo y código de salida) o en otra ventana.</summary>
public sealed class CommandExecutor
{
    private readonly Session _s;

    public CommandExecutor(Session s) { _s = s; }

    /// <summary>
    /// Ejecuta en esta misma consola mostrando la salida, el tiempo y el código. Corre en un PowerShell hijo
    /// (conserva los colores y un error no rompe la herramienta). Ctrl+C cancela solo este comando.
    /// </summary>
    public int Here(string command, string? folder = null, IReadOnlyDictionary<string, string>? env = null)
    {
        folder ??= _s.Root;
        if (!Directory.Exists(folder))
        {
            _s.Ui.Write($"No existe la carpeta: {folder}", Level.Error);
            return 1;
        }
        var environment = Mix(_s.EnvFor(folder), env);
        _s.Ui.Write($"\n[{_s.Rel(folder)}] > {command}", Level.Info);
        var home = DateTime.Now;
        var lines = new[] { command, "$__ok = $?; if ($LASTEXITCODE) { exit $LASTEXITCODE }; if (-not $__ok) { exit 1 }" };
        var code = _s.RunVisible(Shell.PowerShellExe, Shell.ArgsScript(lines, textFlat: true), folder, environment);
        var sec = (int)(DateTime.Now - home).TotalSeconds;
        if (code == 130) _s.Ui.Write($"Cancelado ({sec}s).", Level.Warning);
        else if (code != 0) _s.Ui.Write($"Terminó con código {code} ({sec}s).", Level.Error);
        else _s.Ui.Write($"Terminado ({sec}s).", Level.Ok);
        return code;
    }

    /// <summary>Un comando de la configuración (api.test, web.build, movil.analizar...). Devuelve su código de salida.</summary>
    public int Step(ProjectComponent c, string key)
    {
        if (!_s.AssertComp(c)) return 1;
        var cmd = _s.Command(c, key);
        if (cmd is null)
        {
            _s.Ui.Write($"No hay comando '{key}' para {Session.Names[c]} en la configuración.", Level.Warning);
            return 1;
        }
        if (c == ProjectComponent.Web && key != "instalar" && !_s.Services.EnsureNodeModules()) return 1;
        return Here(cmd, _s.Ctx.Folder(c));
    }

    private static IReadOnlyDictionary<string, string>? Mix(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b)
    {
        if (b is null || b.Count == 0) return a;
        if (a is null) return b;
        var r = new Dictionary<string, string>(a);
        foreach (var (k, v) in b) r[k] = v;
        return r;
    }

    // ------------------------------------------------------------ Ventanas / pestañas

    private string PidFile(string key) => Path.Combine(Path.GetTempPath(), $"stackdev-{_s.Key}-{key}.pid");

    /// <summary>"auto": Windows Terminal si está instalado, si no una ventana de consola nueva.</summary>
    public bool UseWindowsTerminal()
    {
        var t = (_s.Ctx.Config.Terminal ?? "auto").ToLowerInvariant();
        if (t == "ventana") return false;
        return Shell.WindowsTerminal is not null;
    }

    /// <summary>
    /// Abre un comando en su propia ventana o pestaña (para lo interactivo: flutter run con r/R/q, psql, logs).
    /// El script guarda su PID, así se puede cerrar el árbol completo después. Devuelve false si no se pudo abrir.
    /// </summary>
    public bool InWindow(string key, string title, string folder, string command, IReadOnlyDictionary<string, string>? env = null,
        bool waitEnter = true)
    {
        var pidFile = PidFile(key);
        try { File.Delete(pidFile); } catch (IOException) { }
        _s.Services.Registry.Forget(key);

        var environment = Mix(_s.EnvFor(folder), env);
        var titleClean = title.Replace(';', ',').Replace('"', '\'');
        var lines = new List<string>
        {
            $"$host.UI.RawUI.WindowTitle = {Args.PsLiteral(titleClean)}",
            $"Set-Location -LiteralPath {Args.PsLiteral(folder)}",
            $"Set-Content -LiteralPath {Args.PsLiteral(pidFile)} -Value ('' + $PID + '|' + (Get-Process -Id $PID).StartTime.Ticks)",
        };
        lines.AddRange(Shell.EnvLines(environment));
        lines.Add($"Write-Host {Args.PsLiteral("> " + command)} -ForegroundColor Cyan");
        lines.Add(command);
        lines.Add("$__ok = $?; $__c = if ($LASTEXITCODE) { $LASTEXITCODE } elseif ($__ok) { 0 } else { 1 }");
        if (waitEnter)
            lines.Add("Write-Host ''; Write-Host ('Terminó (código ' + $__c + '). Enter para cerrar esta ventana.') -ForegroundColor Yellow; [void](Read-Host)");

        var ps = Shell.PowerShellExe;
        var psArgs = Shell.ArgsScript(lines);
        bool isOpen;
        if (UseWindowsTerminal())
        {
            var args = $"-w 0 new-tab --title {Args.Quote(titleClean)} -d {Args.Quote(folder)} {Args.Quote(ps)} {Args.Join(psArgs)}";
            isOpen = StartShell(Shell.WindowsTerminal!, args);
        }
        else isOpen = ProcessRunner.StartInWindow(ps, Args.Join(psArgs), folder) is not null;

        if (!isOpen) return false;
        var limit = Environment.TickCount64 + 8000;
        while (Environment.TickCount64 < limit)
        {
            if (_s.Services.Registry.AdoptFromFile(key, pidFile)) return true;
            Thread.Sleep(150);
        }
        return true;   // se abrió, aunque no pudimos leer su PID
    }

    private static bool StartShell(string exe, string arguments)
    {
        try { Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = true }); return true; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    /// <summary>Una terminal nueva ya parada en la carpeta (Windows Terminal si existe).</summary>
    public void OpenTerminal(string dir)
    {
        var env = _s.EnvFor(dir);
        var lines = new List<string> { $"Set-Location -LiteralPath {Args.PsLiteral(dir)}" };
        lines.AddRange(Shell.EnvLines(env));
        var ps = Shell.PowerShellExe;
        var psArgs = Shell.ArgsScript(lines, noExit: true);
        if (UseWindowsTerminal())
            StartShell(Shell.WindowsTerminal!, $"-w 0 new-tab -d {Args.Quote(dir)} {Args.Quote(ps)} {Args.Join(psArgs)}");
        else ProcessRunner.StartInWindow(ps, Args.Join(psArgs), dir);
        _s.Ui.Write($"Terminal abierta en {dir}", Level.Ok);
    }

    public void OpenBrowser(string url)
    {
        try
        {
            var preferred = string.IsNullOrWhiteSpace(_s.Ctx.Config.Browser) ? _s.State.Browser : _s.Ctx.Config.Browser;
            var warning = Browsers.Open(url, preferred);
            if (warning is not null) _s.Ui.Write(warning, Level.Warning);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { _s.Ui.Write($"No pude abrir el navegador: {ex.Message}", Level.Error); }
    }
}
