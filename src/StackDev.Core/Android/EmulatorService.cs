using System.Diagnostics;
using StackDev.Core.Config;
using StackDev.Core.Proc;
using StackDev.Core.Ui;
using StackDev.Core.Util;

namespace StackDev.Core.Android;

public enum EmuCode { None, Ok, NotResponding }

public sealed record EmuState(EmuCode Code, string? Serial, string Text);

public enum StartupResult { Ok, Timeout, Cancelled, Dead }

/// <summary>Tracking of an emulator boot (so several things can be awaited at once).</summary>
public sealed class EmuWait
{
    public string? Serial { get; set; }
    public bool Centered { get; set; }
}

/// <summary>
/// The Android emulator: Quick Boot start, shutdown saving state, detection of a hung emulator,
/// forced shutdown, window centring and recovery when it does not start.
/// </summary>
public sealed class EmulatorService
{
    private readonly Session _s;

    public EmulatorService(Session s) { _s = s; }

    private bool HasAndroid(bool warn = true)
    {
        if (_s.EmulatorExe is not null && _s.Adb is not null) return true;
        if (warn)
        {
            _s.Ui.Write("No encontré el Android SDK (emulator y platform-tools).", Level.Error);
            _s.Ui.Write("Pon la ruta en mobile.androidSdk de dev-menu.json.", Level.Warning);
        }
        return false;
    }

    // ------------------------------------------------------------ Estado

    public static Process[] Processes() =>
        Process.GetProcesses().Where(p =>
        {
            try { return p.ProcessName.StartsWith("qemu-system-", StringComparison.OrdinalIgnoreCase) || p.ProcessName.Equals("emulator", StringComparison.OrdinalIgnoreCase); }
            catch (InvalidOperationException) { return false; }
        }).ToArray();

    public bool HasProcess() => Processes().Length > 0;

    /// <summary>
    /// None / Ok / Unresponsive. It is considered unresponsive if the qemu window is frozen, if
    /// <c>adb shell echo ok</c> does not answer within 5 s, if adb sees it "offline", or if the process exists but adb does not see it.
    /// </summary>
    public EmuState State()
    {
        var procs = Processes();
        if (procs.Length == 0) return new(EmuCode.None, null, "DETENIDO");
        if (_s.Adb is null) return new(EmuCode.NotResponding, null, "SIN ADB");

        var emus = _s.Adb.Emulators();
        var dev = emus.Where(e => e.State == "device").ToList();
        if (dev.Count > 0)
        {
            var s = dev[0].Serial;
            var frozen = procs.Any(p => NoResponse(p));
            var echo = _s.Adb.Echo(s);
            if (!frozen && echo is not null && echo.Contains("ok")) return new(EmuCode.Ok, s, $"CORRIENDO  {s}");
            var reason = frozen ? "ventana congelada" : "adb no contesta";
            return new(EmuCode.NotResponding, s, $"NO RESPONDE ({s}, {reason})");
        }
        if (emus.Count > 0) return new(EmuCode.NotResponding, emus[0].Serial, $"SIN RESPUESTA ({emus[0].Serial} {emus[0].State})");
        return new(EmuCode.NotResponding, null, "SIN CONEXIÓN ADB (arrancando o colgado)");
    }

    private static bool NoResponse(Process p)
    {
        try { return p.MainWindowHandle != IntPtr.Zero && !p.Responding; }
        catch (InvalidOperationException) { return false; }
    }

    public string AvdName(string serial) => _s.Adb?.AvdName(serial) ?? "?";

    // ------------------------------------------------------------ AVDs

    /// <summary>Folder where the AVDs live (~\.android\avd).</summary>
    public static string? AvdHome()
    {
        var h = Environment.GetEnvironmentVariable("ANDROID_AVD_HOME");
        if (!string.IsNullOrEmpty(h) && Directory.Exists(h)) return h;
        var bases = new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_USER_HOME"),
            Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android"),
        };
        foreach (var b in bases)
        {
            if (string.IsNullOrEmpty(b)) continue;
            var d = Path.Combine(b, "avd");
            if (Directory.Exists(d)) return d;
        }
        return null;
    }

    public static string? AvdDir(string? name)
    {
        var home = AvdHome();
        if (home is null || string.IsNullOrEmpty(name)) return null;
        var ini = Path.Combine(home, name + ".ini");
        if (File.Exists(ini))
        {
            var line = File.ReadAllLines(ini).FirstOrDefault(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\s*path\s*="));
            if (line is not null)
            {
                var p = line[(line.IndexOf('=') + 1)..].Trim();
                if (Directory.Exists(p)) return p;
            }
        }
        var d = Path.Combine(home, name + ".avd");
        return Directory.Exists(d) ? d : null;
    }

    public List<string> AvdList()
    {
        if (_s.EmulatorExe is null) return new();
        var r = ProcessRunner.Run(_s.EmulatorExe, new[] { "-list-avds" }, 20, env: _s.EnvAndroid, ct: _s.Ct);
        return r.Output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("INFO", StringComparison.Ordinal)).ToList();
    }

    /// <summary>Shows the list and lets the user choose. Returns null if cancelled.</summary>
    public string? ChooseAvd()
    {
        var avds = AvdList();
        if (avds.Count == 0)
        {
            _s.Ui.Write("No hay AVDs creados. Crea uno en Android Studio (Device Manager).", Level.Error);
            return null;
        }
        var byDefault = _s.Avd is null ? -1 : avds.IndexOf(_s.Avd);
        var texts = avds.Select(a => a == _s.Avd ? $"{a}  (último usado)" : a).ToList();
        var i = _s.Ui.Choose("Emuladores disponibles:", texts, byDefault);
        if (i < 0) return null;
        _s.Avd = avds[i];
        return _s.Avd;
    }

    /// <summary>The .lock files left by an emulator closed by force make the next start fail.</summary>
    public void CleanLocks()
    {
        if (HasProcess()) return;
        var dirs = new List<string>();
        var home = AvdHome();
        if (home is not null)
        {
            try { dirs.AddRange(Directory.GetDirectories(home, "*.avd")); } catch (IOException) { }
        }
        var own = AvdDir(_s.Avd);
        if (own is not null) dirs.Add(own);
        foreach (var d in dirs.Distinct())
        {
            try
            {
                foreach (var l in Directory.GetFileSystemEntries(d, "*.lock"))
                {
                    if (Directory.Exists(l)) Directory.Delete(l, true); else File.Delete(l);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>If the AVD is set to always cold boot, offers to switch it to Quick Boot.</summary>
    public void ConfirmQuickBoot(string name)
    {
        var dir = AvdDir(name);
        if (dir is null) return;
        var cfg = Path.Combine(dir, "config.ini");
        if (File.Exists(cfg))
        {
            var lines = File.ReadAllLines(cfg);
            if (lines.Any(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\s*fastboot\.forceColdBoot\s*=\s*yes")))
            {
                _s.Ui.Write("Este AVD está configurado para arrancar SIEMPRE desde cero (Cold boot).", Level.Warning);
                if (_s.Ui.Confirm("¿Cambiarlo a Quick Boot (restaurar estado)?", true))
                {
                    var fresh = lines.Select(l =>
                        System.Text.RegularExpressions.Regex.IsMatch(l, @"^\s*fastboot\.forceColdBoot\s*=") ? "fastboot.forceColdBoot=no"
                        : System.Text.RegularExpressions.Regex.IsMatch(l, @"^\s*fastboot\.forceFastBoot\s*=") ? "fastboot.forceFastBoot=yes"
                        : l).ToArray();
                    File.WriteAllLines(cfg, fresh, new System.Text.UTF8Encoding(false));
                    _s.Ui.Write("Quick Boot activado.", Level.Ok);
                }
            }
        }
        if (Directory.Exists(Path.Combine(dir, "snapshots", "default_boot")))
            _s.Ui.Write("Se restaurará el estado guardado (Quick Boot).", Level.Muted);
        else
        {
            _s.Ui.Write("No hay estado guardado: este arranque será completo.", Level.Muted);
            _s.Ui.Write("Al detenerlo desde la herramienta se guarda y el próximo será rápido.", Level.Muted);
        }
    }

    // ------------------------------------------------------------ Centrar

    public bool Center()
    {
        if (_s.Op.NoCenter) return true;
        var p = Processes()
            .Where(x => x.ProcessName.StartsWith("qemu-system-", StringComparison.OrdinalIgnoreCase))
            .Where(x => { try { return x.MainWindowHandle != IntPtr.Zero; } catch (InvalidOperationException) { return false; } })
            .OrderBy(x => x.MainWindowTitle.StartsWith("Android Emulator", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
        return p is not null && Win32Window.Center(p.Id, p.MainWindowHandle);
    }

    // ------------------------------------------------------------ Arranque

    /// <summary>
    /// "" while it is still starting, "OK" when <c>sys.boot_completed</c> is 1, or the reason for the failure.
    /// The window is centred once when it appears and again when it finishes loading (it can change size).
    /// </summary>
    public string TestReady(EmuWait wait)
    {
        if (!wait.Centered) wait.Centered = Center();
        if (!HasProcess()) return "se cerró inesperadamente";
        wait.Serial ??= _s.Adb?.Emulators().FirstOrDefault(e => e.State == "device")?.Serial;
        if (wait.Serial is not null && _s.Adb!.FullStartup(wait.Serial))
        {
            Center();
            return "OK";
        }
        return "";
    }

    public StartupResult WaitForStartup(int seconds = 180)
    {
        _s.Ui.WriteInline("Esperando el emulador (C o Ctrl+C para dejar de esperar)");
        var limit = Environment.TickCount64 + seconds * 1000L;
        var wait = new EmuWait();
        while (true)
        {
            var r = TestReady(wait);
            if (r == "OK")
            {
                _s.Ui.Write("");
                _s.Ui.Write($"Emulador listo: {_s.Avd} ({wait.Serial})", Level.Ok);
                return StartupResult.Ok;
            }
            if (r.Length > 0) { _s.Ui.Write(""); return StartupResult.Dead; }
            if (Environment.TickCount64 > limit) { _s.Ui.Write(""); return StartupResult.Timeout; }
            if (_s.Ct.IsCancellationRequested || ConsoleKeys.PressCancel()) { _s.Ui.Write(""); return StartupResult.Cancelled; }
            _s.Ui.WriteInline(".");
            _s.Ct.WaitHandle.WaitOne(2000);
        }
    }

    /// <summary>Launches the emulator process (without waiting). Always with <c>-netdelay none -netspeed full -no-boot-anim</c>.</summary>
    public bool StartProcess(string name, bool cold)
    {
        if (!HasAndroid()) return false;
        var psi = new ProcessStartInfo(_s.EmulatorExe!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-avd", name, "-netdelay", "none", "-netspeed", "full", "-no-boot-anim" }) psi.ArgumentList.Add(a);
        if (cold)
        {
            psi.ArgumentList.Add("-no-snapshot-load");
            _s.Ui.Write("Arranque desde cero: tardará un poco más.", Level.Muted);
        }
        else ConfirmQuickBoot(name);
        if (_s.EnvAndroid is not null) foreach (var (k, v) in _s.EnvAndroid) psi.Environment[k] = v;

        _s.Avd = name;
        _s.State.SaveLastAvd(_s.Key, name);
        _s.Ui.Write($"Iniciando emulador '{name}' en segundo plano...", Level.Info);
        try { Process.Start(psi); return true; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _s.Ui.Write($"No se pudo iniciar el emulador: {ex.Message}", Level.Error);
            return false;
        }
    }

    /// <summary>
    /// With no parameters it starts the last emulator used (fast, no questions); <paramref name="choose"/> shows the list.
    /// If it does not start it offers: keep waiting, retry from scratch, pick another or close it.
    /// </summary>
    public bool Start(string? name = null, bool cold = false, bool choose = false)
    {
        if (!_s.AssertComp(ProjectComponent.Mobile) || !HasAndroid()) return false;
        var state = State();
        if (state.Code == EmuCode.Ok)
        {
            _s.Ui.Write($"Ya hay un emulador corriendo: {AvdName(state.Serial!)} ({state.Serial}).", Level.Warning);
            _s.Ui.Write("Usa «Iniciar otro / cambiar» para cambiarlo.", Level.Warning);
            return true;
        }
        if (state.Code == EmuCode.NotResponding)
        {
            _s.Ui.Write($"Hay un emulador abierto que no responde: {state.Text}", Level.Warning);
            if (!_s.Ui.Confirm("¿Cerrarlo a la fuerza y continuar?", true)) return false;
            Stop(force: true);
        }

        if (name is null && !choose && _s.Avd is not null)
        {
            if (AvdList().Contains(_s.Avd)) name = _s.Avd;
            else _s.Ui.Write($"El último emulador usado ({_s.Avd}) ya no existe; elige otro.", Level.Warning);
        }
        name ??= ChooseAvd();
        if (name is null) { _s.Ui.Write("Cancelado.", Level.Muted); return false; }

        if (!StartProcess(name, cold)) return false;

        while (true)
        {
            var r = WaitForStartup(180);
            if (r == StartupResult.Ok) { _s.Conditions.AfterStartup(); return true; }

            _s.Ui.Write(r switch
            {
                StartupResult.Dead => "El emulador se cerró inesperadamente.",
                StartupResult.Timeout => "El emulador no terminó de arrancar en 3 minutos.",
                _ => "Espera cancelada.",
            }, r == StartupResult.Cancelled ? Level.Warning : Level.Error);

            var options = new List<(string Text, string Key)>();
            if (r != StartupResult.Dead) options.Add(("Seguir esperando", "esperar"));
            options.Add(("Reintentar este emulador desde cero", "frio"));
            options.Add(("Elegir otro emulador", "otro"));
            options.Add(("Cerrarlo y salir", "cerrar"));
            if (r != StartupResult.Dead) options.Add(("Dejarlo abierto y salir", "dejar"));

            // With nobody in front (headless command) it does not insist: it is closed so as not to leave a half-started emulator
            var i = _s.Ui.Interactive ? _s.Ui.Choose("¿Qué hacemos?", options.Select(o => o.Text).ToList(), -1) : -1;
            var choice = i < 0 ? (r == StartupResult.Cancelled ? "dejar" : "cerrar") : options[i].Key;
            switch (choice)
            {
                case "esperar": continue;
                case "frio": Stop(force: true); return Start(name, cold: true);
                case "otro": Stop(force: true); return Start(choose: true);
                case "dejar": return false;
                default: Stop(force: true); return false;
            }
        }
    }

    // ------------------------------------------------------------ Cierre

    /// <summary>
    /// Normal shutdown: resets the conditions, saves the state (<c>snapshot save default_boot</c>), <c>emu kill</c> and
    /// waits up to 60 s for the process to end by itself (killing it while it writes to disk forced a cold start every time).
    /// Only if it does not end, or with <paramref name="force"/>, is it killed, the .lock files removed and adb restarted if a ghost remains.
    /// </summary>
    public void Stop(bool force = false)
    {
        var state = State();
        if (state.Code == EmuCode.None && !force)
        {
            _s.Ui.Write("No hay emulador corriendo.", Level.Muted);
            return;
        }

        if (!force && state.Code == EmuCode.Ok && _s.Adb is not null)
        {
            var serial = state.Serial!;
            if (_s.Conditions.Active().Count > 0)
            {
                // so that the saved state does not stay in airplane mode, low battery, etc.
                _s.Ui.Write("Restableciendo condiciones del dispositivo...", Level.Muted);
                _s.Conditions.Reset(serial);
            }
            _s.Ui.Write("Guardando el estado del emulador para el próximo arranque...", Level.Info);
            var r = _s.Adb.Run(new[] { "-s", serial, "emu", "avd", "snapshot", "save", "default_boot" }, 120, _s.Ct);
            if (r is not null && r.Contains("OK")) _s.Ui.Write("Estado guardado.", Level.Ok);
            else _s.Ui.Write("No se pudo guardar el estado; el próximo arranque usará el último guardado.", Level.Warning);

            _s.Ui.WriteInline($"Cerrando emulador {serial}");
            _s.Adb.Run(new[] { "-s", serial, "emu", "kill" }, 10, _s.Ct);
            var limit = Environment.TickCount64 + 60_000;
            while (HasProcess() && Environment.TickCount64 < limit)
            {
                _s.Ui.WriteInline(".");
                Thread.Sleep(1000);
            }
            _s.Ui.Write("");
        }
        else if (state.Code != EmuCode.None)
        {
            _s.Ui.Write("Forzando el cierre del emulador (no se guarda el estado)...", Level.Warning);
        }

        var remain = Processes();
        foreach (var p in remain) ProcessRunner.KillTree(p);
        if (remain.Length > 0) Thread.Sleep(2000);

        if (remain.Length > 0 || force)
        {
            CleanLocks();
            // if adb still shows a ghost emulator, restart the server
            if (_s.Adb is not null && _s.Adb.Emulators().Count > 0) _s.Adb.RestartServer();
        }
        if (state.Code != EmuCode.None) _s.Ui.Write("Emulador detenido.", Level.Ok);
    }

    /// <summary>Chooses first; if you cancel, the current one stays open.</summary>
    public bool Change()
    {
        if (!_s.AssertComp(ProjectComponent.Mobile) || !HasAndroid()) return false;
        var state = State();
        if (state.Code == EmuCode.Ok) _s.Ui.Write($"Emulador actual: {AvdName(state.Serial!)} ({state.Serial})", Level.Muted);
        else if (state.Code == EmuCode.NotResponding) _s.Ui.Write($"Emulador actual: {state.Text}", Level.Error);

        var fresh = ChooseAvd();
        if (fresh is null) { _s.Ui.Write("Cancelado.", Level.Muted); return false; }
        if (state.Code != EmuCode.None) Stop();   // if it does not respond, it is forced only then
        return Start(fresh);
    }

    /// <summary>Cold start (when the saved state causes problems).</summary>
    public bool StartCold(string? name = null)
    {
        if (!_s.AssertComp(ProjectComponent.Mobile) || !HasAndroid()) return false;
        var state = State();
        if (state.Code == EmuCode.Ok)
        {
            if (!_s.Ui.Confirm("Hay un emulador corriendo. ¿Cerrarlo y arrancar desde cero?", true)) return false;
            Stop();
        }
        else if (state.Code == EmuCode.NotResponding) Stop(force: true);
        name ??= ChooseAvd();
        if (name is null) { _s.Ui.Write("Cancelado.", Level.Muted); return false; }
        return Start(name, cold: true);
    }
}
