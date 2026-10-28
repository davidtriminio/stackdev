using StackDev.Core.Android;
using StackDev.Core.Config;
using StackDev.Core.Ui;

namespace StackDev.Core.Flutter;

/// <summary>flutter run (debug, profile or release), stop the app, analyze, test, format, build_runner, pub get and clean.</summary>
public sealed class FlutterService
{
    private readonly Session _s;

    public FlutterService(Session s) { _s = s; }

    public bool AppRunning => _s.Services.LiveApps().Count > 0;

    public string ChangeMode()
    {
        _s.BuildMode = _s.BuildMode switch { "debug" => "profile", "profile" => "release", _ => "debug" };
        _s.Ui.Write($"Modo de build: {_s.BuildMode}", Level.Ok);
        if (_s.BuildMode != "debug")
            _s.Ui.Write($"En {_s.BuildMode} no hay hot reload; sirve para medir rendimiento o probar la versión final.", Level.Muted);
        return _s.BuildMode;
    }

    private static string AppKey(AdbDevice d) =>
        d.IsEmulator ? "app" : "app-" + System.Text.RegularExpressions.Regex.Replace(d.Serial, @"[^\w\-]", "_");

    /// <summary>Devices ready to run the app (the emulator only if it answers).</summary>
    private List<AdbDevice>? Destinations()
    {
        var all = _s.Adb!.Devices();
        var emu = _s.Emulator.State();
        var ready = all.Where(d => d.Ready && (!d.IsEmulator || emu.Code == EmuCode.Ok)).ToList();
        if (ready.Count > 0) return ready;

        foreach (var d in all.Where(d => !d.Ready && !d.IsEmulator))
            _s.Ui.Write(DeviceService.Label(d), Level.Warning);
        if (emu.Code == EmuCode.NotResponding)
        {
            _s.Ui.Write($"El emulador no responde: {emu.Text}", Level.Error);
            _s.Ui.Write("Cámbialo o fuerza su cierre.", Level.Warning);
        }
        else _s.Ui.Write("No hay emulador ni teléfono conectado. Inicia el emulador o conecta un dispositivo (USB o red).", Level.Warning);
        return null;
    }

    /// <summary>Decides which devices to run on: --dispositivo, the only one there is, or asks (with the "All" option).</summary>
    private List<AdbDevice>? Choose(List<AdbDevice> ready)
    {
        var request = _s.Op.Device?.Trim();
        if (!string.IsNullOrEmpty(request))
        {
            if (request.Equals("todos", StringComparison.OrdinalIgnoreCase)) return ready;
            var d = ready.FirstOrDefault(x => x.Serial.Equals(request, StringComparison.OrdinalIgnoreCase));
            if (d is null)
            {
                _s.Ui.Write($"El dispositivo '{request}' no está listo. Hay: {string.Join(", ", ready.Select(x => x.Serial))}", Level.Error);
                return null;
            }
            return new() { d };
        }
        if (ready.Count == 1) return ready;
        if (!_s.Ui.Interactive)
        {
            _s.Ui.Write($"Hay varios dispositivos ({string.Join(", ", ready.Select(x => x.Serial))}): usa --dispositivo <serial|todos>.", Level.Error);
            return null;
        }
        var texts = ready.Select(DeviceService.Label).ToList();
        texts.Add($"Todos los dispositivos ({ready.Count})");
        var i = _s.Ui.Choose("¿En qué dispositivo ejecutar la app?", texts, 0);
        if (i < 0) { _s.Ui.Write("Cancelado.", Level.Muted); return null; }
        return i == ready.Count ? ready : new() { ready[i] };
    }

    /// <summary>
    /// <c>flutter run -d serial</c> plus <c>mobile.argsRun</c> and the extra arguments, on the emulator, on a phone (USB or network) or on
    /// several at once. Each device runs in its own window or tab (it needs its terminal for the r, R and q keys) or,
    /// with <c>--misma-ventana</c> and a single device, in this console.
    /// </summary>
    public bool Run()
    {
        if (!_s.AssertComp(ProjectComponent.Mobile)) return false;
        if (_s.Adb is null)
        {
            _s.Ui.Write("No encontré el Android SDK (emulator y platform-tools).", Level.Error);
            return false;
        }
        var ready = Destinations();
        if (ready is null) return false;
        var destinations = Choose(ready);
        if (destinations is null) return false;
        if (destinations.Count > 1 && _s.Op.SameWindow)
        {
            _s.Ui.Write("Con --misma-ventana solo se puede un dispositivo a la vez.", Level.Error);
            return false;
        }

        _s.Devices.Forward(destinations, silent: true);

        var mode = _s.BuildMode switch { "profile" => "--profile", "release" => "--release", _ => "" };
        var cond = _s.Conditions.Text();
        var keys = _s.BuildMode == "debug" ? "r = hot reload | R = hot restart | q = terminar" : $"modo {_s.BuildMode}: sin hot reload | q = terminar";
        var folder = _s.Ctx.Folder(ProjectComponent.Mobile);
        var ok = true;

        foreach (var d in destinations)
        {
            var key = AppKey(d);
            if (_s.Services.Registry.Live(key) is not null && !_s.Op.SameWindow)
            {
                if (!_s.Ui.Confirm($"La app ya está corriendo en {d.Serial}. ¿Cerrarla y volver a lanzarla?", true)) continue;
                _s.Services.StopProcess(key);
                Thread.Sleep(800);
            }

            // The emulator sees the PC as 10.0.2.2; a phone does not. With port forwarding, localhost works for it.
            var defines = "";
            var alreadyDefined = $"{_s.Ctx.Config.Mobile?.ArgsRun} {_s.Op.ArgsExtra}".Contains("API_BASE_URL", StringComparison.Ordinal);
            if (!d.IsEmulator && !alreadyDefined && _s.Ctx.Has(ProjectComponent.Api))
            {
                defines = $"--dart-define=API_BASE_URL=http://localhost:{_s.Ctx.Port(ProjectComponent.Api)}";
                _s.Ui.Write($"Teléfono: la app llamará a http://localhost:{_s.Ctx.Port(ProjectComponent.Api)} (puerto reenviado a tu PC).", Level.Muted);
            }
            var args = System.Text.RegularExpressions.Regex.Replace(
                $"run -d {d.Serial} {mode} {defines} {_s.Ctx.Config.Mobile?.ArgsRun} {_s.Op.ArgsExtra}", @"\s+", " ").Trim();
            _s.Ui.Write($"\nflutter {args}   ({DeviceService.Label(d)})", Level.Info);
            if (cond != "Normal" && d.IsEmulator) _s.Ui.Write($"Condiciones activas: {cond}", Level.Warning);

            if (_s.Op.SameWindow)
            {
                _s.Ui.Write($"Teclas: {keys}", Level.Muted);
                _s.Executor.Here($"flutter {args}", folder);
                _s.Ui.Write("\nSesión de flutter terminada.", Level.Ok);
                return true;
            }
            var label = d.Description.Length > 0 ? d.Description : d.Serial;
            if (!_s.Executor.InWindow(key, $"flutter run - {_s.Ctx.Name} [{label}]", folder, $"flutter {args}"))
            {
                _s.Ui.Write($"No se pudo abrir la ventana de la app para {d.Serial}.", Level.Error);
                ok = false;
                continue;
            }
            _s.Ui.Write($"La app corre en {label} en su propia ventana ({keys}).", Level.Ok);
        }
        if (ok) _s.Ui.Write("Puedes seguir usando la herramienta.", Level.Muted);
        return ok;
    }

    public bool Stop()
    {
        var n = _s.Services.StopApps();
        if (n > 0) { _s.Ui.Write(n == 1 ? "App detenida." : $"{n} sesiones de la app detenidas.", Level.Ok); return true; }
        _s.Ui.Write("La app no estaba corriendo desde la herramienta.", Level.Muted);
        return true;
    }

    public int PubGet() =>
        _s.AssertComp(ProjectComponent.Mobile) ? _s.Executor.Here("flutter pub get", _s.Ctx.Folder(ProjectComponent.Mobile)) : 1;

    /// <summary><c>flutter clean</c> and, if it went well, <c>flutter pub get</c>.</summary>
    public int Clean() =>
        _s.AssertComp(ProjectComponent.Mobile)
            ? _s.Executor.Here("flutter clean; if ($LASTEXITCODE -eq 0) { flutter pub get }", _s.Ctx.Folder(ProjectComponent.Mobile))
            : 1;
}
