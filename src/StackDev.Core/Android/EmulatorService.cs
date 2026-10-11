using System.Diagnostics;
using StackDev.Core.Config;
using StackDev.Core.Proc;
using StackDev.Core.Ui;
using StackDev.Core.Util;

namespace StackDev.Core.Android;

public enum CodigoEmu { Ninguno, Ok, NoResponde }

public sealed record EstadoEmu(CodigoEmu Codigo, string? Serial, string Texto);

public enum ResultadoArranque { Ok, Tiempo, Cancelado, Muerto }

/// <summary>Seguimiento del arranque de un emulador (para poder esperar varias cosas a la vez).</summary>
public sealed class EsperaEmu
{
    public string? Serial { get; set; }
    public bool Centrado { get; set; }
}

/// <summary>
/// El emulador de Android: arranque con Quick Boot, cierre guardando el estado, detección de un emulador colgado,
/// cierre forzado, centrado de la ventana y recuperación cuando no arranca.
/// </summary>
public sealed class EmulatorService
{
    private readonly Sesion _s;

    public EmulatorService(Sesion s) { _s = s; }

    private bool HayAndroid(bool avisar = true)
    {
        if (_s.EmulatorExe is not null && _s.Adb is not null) return true;
        if (avisar)
        {
            _s.Ui.Escribir("No encontré el Android SDK (emulator y platform-tools).", Nivel.Error);
            _s.Ui.Escribir("Pon la ruta en movil.androidSdk de dev-menu.json.", Nivel.Aviso);
        }
        return false;
    }

    // ------------------------------------------------------------ Estado

    public static Process[] Procesos() =>
        Process.GetProcesses().Where(p =>
        {
            try { return p.ProcessName.StartsWith("qemu-system-", StringComparison.OrdinalIgnoreCase) || p.ProcessName.Equals("emulator", StringComparison.OrdinalIgnoreCase); }
            catch (InvalidOperationException) { return false; }
        }).ToArray();

    public bool HayProceso() => Procesos().Length > 0;

    /// <summary>
    /// Ninguno / Ok / NoResponde. Se considera que no responde si la ventana de qemu está congelada, si
    /// <c>adb shell echo ok</c> no contesta en 5 s, si adb lo ve "offline", o si el proceso existe pero adb no lo ve.
    /// </summary>
    public EstadoEmu Estado()
    {
        var procs = Procesos();
        if (procs.Length == 0) return new(CodigoEmu.Ninguno, null, "DETENIDO");
        if (_s.Adb is null) return new(CodigoEmu.NoResponde, null, "SIN ADB");

        var emus = _s.Adb.Emuladores();
        var dev = emus.Where(e => e.Estado == "device").ToList();
        if (dev.Count > 0)
        {
            var s = dev[0].Serial;
            var congelada = procs.Any(p => SinRespuesta(p));
            var eco = _s.Adb.Eco(s);
            if (!congelada && eco is not null && eco.Contains("ok")) return new(CodigoEmu.Ok, s, $"CORRIENDO  {s}");
            var motivo = congelada ? "ventana congelada" : "adb no contesta";
            return new(CodigoEmu.NoResponde, s, $"NO RESPONDE ({s}, {motivo})");
        }
        if (emus.Count > 0) return new(CodigoEmu.NoResponde, emus[0].Serial, $"SIN RESPUESTA ({emus[0].Serial} {emus[0].Estado})");
        return new(CodigoEmu.NoResponde, null, "SIN CONEXIÓN ADB (arrancando o colgado)");
    }

    private static bool SinRespuesta(Process p)
    {
        try { return p.MainWindowHandle != IntPtr.Zero && !p.Responding; }
        catch (InvalidOperationException) { return false; }
    }

    public string NombreAvd(string serial) => _s.Adb?.NombreAvd(serial) ?? "?";

    // ------------------------------------------------------------ AVDs

    /// <summary>Carpeta donde viven los AVDs (~\.android\avd).</summary>
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

    public static string? AvdDir(string? nombre)
    {
        var home = AvdHome();
        if (home is null || string.IsNullOrEmpty(nombre)) return null;
        var ini = Path.Combine(home, nombre + ".ini");
        if (File.Exists(ini))
        {
            var linea = File.ReadAllLines(ini).FirstOrDefault(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\s*path\s*="));
            if (linea is not null)
            {
                var p = linea[(linea.IndexOf('=') + 1)..].Trim();
                if (Directory.Exists(p)) return p;
            }
        }
        var d = Path.Combine(home, nombre + ".avd");
        return Directory.Exists(d) ? d : null;
    }

    public List<string> ListaAvds()
    {
        if (_s.EmulatorExe is null) return new();
        var r = ProcessRunner.Run(_s.EmulatorExe, new[] { "-list-avds" }, 20, env: _s.EnvAndroid, ct: _s.Ct);
        return r.Salida.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("INFO", StringComparison.Ordinal)).ToList();
    }

    /// <summary>Muestra la lista y deja elegir. Devuelve null si se cancela.</summary>
    public string? ElegirAvd()
    {
        var avds = ListaAvds();
        if (avds.Count == 0)
        {
            _s.Ui.Escribir("No hay AVDs creados. Crea uno en Android Studio (Device Manager).", Nivel.Error);
            return null;
        }
        var porDefecto = _s.Avd is null ? -1 : avds.IndexOf(_s.Avd);
        var textos = avds.Select(a => a == _s.Avd ? $"{a}  (último usado)" : a).ToList();
        var i = _s.Ui.Elegir("Emuladores disponibles:", textos, porDefecto);
        if (i < 0) return null;
        _s.Avd = avds[i];
        return _s.Avd;
    }

    /// <summary>Los .lock que deja un emulador cerrado a la fuerza hacen fallar el siguiente arranque.</summary>
    public void LimpiarLocks()
    {
        if (HayProceso()) return;
        var dirs = new List<string>();
        var home = AvdHome();
        if (home is not null)
        {
            try { dirs.AddRange(Directory.GetDirectories(home, "*.avd")); } catch (IOException) { }
        }
        var propio = AvdDir(_s.Avd);
        if (propio is not null) dirs.Add(propio);
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

    /// <summary>Si el AVD está configurado para arrancar siempre en frío, ofrece cambiarlo a Quick Boot.</summary>
    public void ConfirmarQuickBoot(string nombre)
    {
        var dir = AvdDir(nombre);
        if (dir is null) return;
        var cfg = Path.Combine(dir, "config.ini");
        if (File.Exists(cfg))
        {
            var lineas = File.ReadAllLines(cfg);
            if (lineas.Any(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\s*fastboot\.forceColdBoot\s*=\s*yes")))
            {
                _s.Ui.Escribir("Este AVD está configurado para arrancar SIEMPRE desde cero (Cold boot).", Nivel.Aviso);
                if (_s.Ui.Confirmar("¿Cambiarlo a Quick Boot (restaurar estado)?", true))
                {
                    var nuevas = lineas.Select(l =>
                        System.Text.RegularExpressions.Regex.IsMatch(l, @"^\s*fastboot\.forceColdBoot\s*=") ? "fastboot.forceColdBoot=no"
                        : System.Text.RegularExpressions.Regex.IsMatch(l, @"^\s*fastboot\.forceFastBoot\s*=") ? "fastboot.forceFastBoot=yes"
                        : l).ToArray();
                    File.WriteAllLines(cfg, nuevas, new System.Text.UTF8Encoding(false));
                    _s.Ui.Escribir("Quick Boot activado.", Nivel.Ok);
                }
            }
        }
        if (Directory.Exists(Path.Combine(dir, "snapshots", "default_boot")))
            _s.Ui.Escribir("Se restaurará el estado guardado (Quick Boot).", Nivel.Tenue);
        else
        {
            _s.Ui.Escribir("No hay estado guardado: este arranque será completo.", Nivel.Tenue);
            _s.Ui.Escribir("Al detenerlo desde la herramienta se guarda y el próximo será rápido.", Nivel.Tenue);
        }
    }

    // ------------------------------------------------------------ Centrar

    public bool Centrar()
    {
        if (_s.Op.NoCentrar) return true;
        var p = Procesos()
            .Where(x => x.ProcessName.StartsWith("qemu-system-", StringComparison.OrdinalIgnoreCase))
            .Where(x => { try { return x.MainWindowHandle != IntPtr.Zero; } catch (InvalidOperationException) { return false; } })
            .OrderBy(x => x.MainWindowTitle.StartsWith("Android Emulator", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
        return p is not null && Win32Window.Centrar(p.Id, p.MainWindowHandle);
    }

    // ------------------------------------------------------------ Arranque

    /// <summary>
    /// "" mientras sigue arrancando, "OK" cuando <c>sys.boot_completed</c> es 1, o el motivo del fallo.
    /// La ventana se centra una vez al aparecer y otra al terminar de cargar (puede cambiar de tamaño).
    /// </summary>
    public string TestListo(EsperaEmu espera)
    {
        if (!espera.Centrado) espera.Centrado = Centrar();
        if (!HayProceso()) return "se cerró inesperadamente";
        espera.Serial ??= _s.Adb?.Emuladores().FirstOrDefault(e => e.Estado == "device")?.Serial;
        if (espera.Serial is not null && _s.Adb!.ArranqueCompleto(espera.Serial))
        {
            Centrar();
            return "OK";
        }
        return "";
    }

    public ResultadoArranque EsperarArranque(int segundos = 180)
    {
        _s.Ui.EscribirInline("Esperando el emulador (C o Ctrl+C para dejar de esperar)");
        var limite = Environment.TickCount64 + segundos * 1000L;
        var espera = new EsperaEmu();
        while (true)
        {
            var r = TestListo(espera);
            if (r == "OK")
            {
                _s.Ui.Escribir("");
                _s.Ui.Escribir($"Emulador listo: {_s.Avd} ({espera.Serial})", Nivel.Ok);
                return ResultadoArranque.Ok;
            }
            if (r.Length > 0) { _s.Ui.Escribir(""); return ResultadoArranque.Muerto; }
            if (Environment.TickCount64 > limite) { _s.Ui.Escribir(""); return ResultadoArranque.Tiempo; }
            if (_s.Ct.IsCancellationRequested || Teclado.PulsoCancelar()) { _s.Ui.Escribir(""); return ResultadoArranque.Cancelado; }
            _s.Ui.EscribirInline(".");
            _s.Ct.WaitHandle.WaitOne(2000);
        }
    }

    /// <summary>Lanza el proceso del emulador (sin esperar). Siempre con <c>-netdelay none -netspeed full -no-boot-anim</c>.</summary>
    public bool IniciarProceso(string nombre, bool frio)
    {
        if (!HayAndroid()) return false;
        var psi = new ProcessStartInfo(_s.EmulatorExe!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-avd", nombre, "-netdelay", "none", "-netspeed", "full", "-no-boot-anim" }) psi.ArgumentList.Add(a);
        if (frio)
        {
            psi.ArgumentList.Add("-no-snapshot-load");
            _s.Ui.Escribir("Arranque desde cero: tardará un poco más.", Nivel.Tenue);
        }
        else ConfirmarQuickBoot(nombre);
        if (_s.EnvAndroid is not null) foreach (var (k, v) in _s.EnvAndroid) psi.Environment[k] = v;

        _s.Avd = nombre;
        _s.Estado.GuardarUltimoAvd(_s.Clave, nombre);
        _s.Ui.Escribir($"Iniciando emulador '{nombre}' en segundo plano...", Nivel.Info);
        try { Process.Start(psi); return true; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _s.Ui.Escribir($"No se pudo iniciar el emulador: {ex.Message}", Nivel.Error);
            return false;
        }
    }

    /// <summary>
    /// Sin parámetros arranca el último emulador usado (rápido, sin preguntas); <paramref name="elegir"/> muestra la lista.
    /// Si no arranca ofrece: seguir esperando, reintentar desde cero, elegir otro o cerrarlo.
    /// </summary>
    public bool Iniciar(string? nombre = null, bool frio = false, bool elegir = false)
    {
        if (!_s.AfirmarComp(Componente.Movil) || !HayAndroid()) return false;
        var estado = Estado();
        if (estado.Codigo == CodigoEmu.Ok)
        {
            _s.Ui.Escribir($"Ya hay un emulador corriendo: {NombreAvd(estado.Serial!)} ({estado.Serial}).", Nivel.Aviso);
            _s.Ui.Escribir("Usa «Iniciar otro / cambiar» para cambiarlo.", Nivel.Aviso);
            return true;
        }
        if (estado.Codigo == CodigoEmu.NoResponde)
        {
            _s.Ui.Escribir($"Hay un emulador abierto que no responde: {estado.Texto}", Nivel.Aviso);
            if (!_s.Ui.Confirmar("¿Cerrarlo a la fuerza y continuar?", true)) return false;
            Detener(forzar: true);
        }

        if (nombre is null && !elegir && _s.Avd is not null)
        {
            if (ListaAvds().Contains(_s.Avd)) nombre = _s.Avd;
            else _s.Ui.Escribir($"El último emulador usado ({_s.Avd}) ya no existe; elige otro.", Nivel.Aviso);
        }
        nombre ??= ElegirAvd();
        if (nombre is null) { _s.Ui.Escribir("Cancelado.", Nivel.Tenue); return false; }

        if (!IniciarProceso(nombre, frio)) return false;

        while (true)
        {
            var r = EsperarArranque(180);
            if (r == ResultadoArranque.Ok) { _s.Condiciones.TrasArranque(); return true; }

            _s.Ui.Escribir(r switch
            {
                ResultadoArranque.Muerto => "El emulador se cerró inesperadamente.",
                ResultadoArranque.Tiempo => "El emulador no terminó de arrancar en 3 minutos.",
                _ => "Espera cancelada.",
            }, r == ResultadoArranque.Cancelado ? Nivel.Aviso : Nivel.Error);

            var opciones = new List<(string Texto, string Clave)>();
            if (r != ResultadoArranque.Muerto) opciones.Add(("Seguir esperando", "esperar"));
            opciones.Add(("Reintentar este emulador desde cero", "frio"));
            opciones.Add(("Elegir otro emulador", "otro"));
            opciones.Add(("Cerrarlo y salir", "cerrar"));
            if (r != ResultadoArranque.Muerto) opciones.Add(("Dejarlo abierto y salir", "dejar"));

            // Sin una persona delante (comando sin interfaz) no se insiste: se cierra para no dejar un emulador a medias
            var i = _s.Ui.Interactiva ? _s.Ui.Elegir("¿Qué hacemos?", opciones.Select(o => o.Texto).ToList(), -1) : -1;
            var eleccion = i < 0 ? (r == ResultadoArranque.Cancelado ? "dejar" : "cerrar") : opciones[i].Clave;
            switch (eleccion)
            {
                case "esperar": continue;
                case "frio": Detener(forzar: true); return Iniciar(nombre, frio: true);
                case "otro": Detener(forzar: true); return Iniciar(elegir: true);
                case "dejar": return false;
                default: Detener(forzar: true); return false;
            }
        }
    }

    // ------------------------------------------------------------ Cierre

    /// <summary>
    /// Cierre normal: restablece las condiciones, guarda el estado (<c>snapshot save default_boot</c>), <c>emu kill</c> y
    /// espera hasta 60 s a que el proceso termine solo (matarlo mientras escribe a disco obligaba a arrancar siempre desde cero).
    /// Solo si no termina, o con <paramref name="forzar"/>, se mata a la fuerza, se borran los .lock y se reinicia adb si hay un fantasma.
    /// </summary>
    public void Detener(bool forzar = false)
    {
        var estado = Estado();
        if (estado.Codigo == CodigoEmu.Ninguno && !forzar)
        {
            _s.Ui.Escribir("No hay emulador corriendo.", Nivel.Tenue);
            return;
        }

        if (!forzar && estado.Codigo == CodigoEmu.Ok && _s.Adb is not null)
        {
            var serial = estado.Serial!;
            if (_s.Condiciones.Activas().Count > 0)
            {
                // para que el estado guardado no quede en modo avión, batería baja, etc.
                _s.Ui.Escribir("Restableciendo condiciones del dispositivo...", Nivel.Tenue);
                _s.Condiciones.Restablecer(serial);
            }
            _s.Ui.Escribir("Guardando el estado del emulador para el próximo arranque...", Nivel.Info);
            var r = _s.Adb.Run(new[] { "-s", serial, "emu", "avd", "snapshot", "save", "default_boot" }, 120, _s.Ct);
            if (r is not null && r.Contains("OK")) _s.Ui.Escribir("Estado guardado.", Nivel.Ok);
            else _s.Ui.Escribir("No se pudo guardar el estado; el próximo arranque usará el último guardado.", Nivel.Aviso);

            _s.Ui.EscribirInline($"Cerrando emulador {serial}");
            _s.Adb.Run(new[] { "-s", serial, "emu", "kill" }, 10, _s.Ct);
            var limite = Environment.TickCount64 + 60_000;
            while (HayProceso() && Environment.TickCount64 < limite)
            {
                _s.Ui.EscribirInline(".");
                Thread.Sleep(1000);
            }
            _s.Ui.Escribir("");
        }
        else if (estado.Codigo != CodigoEmu.Ninguno)
        {
            _s.Ui.Escribir("Forzando el cierre del emulador (no se guarda el estado)...", Nivel.Aviso);
        }

        var quedan = Procesos();
        foreach (var p in quedan) ProcessRunner.KillTree(p);
        if (quedan.Length > 0) Thread.Sleep(2000);

        if (quedan.Length > 0 || forzar)
        {
            LimpiarLocks();
            // si adb sigue mostrando un emulador fantasma, reiniciar el servidor
            if (_s.Adb is not null && _s.Adb.Emuladores().Count > 0) _s.Adb.ReiniciarServidor();
        }
        if (estado.Codigo != CodigoEmu.Ninguno) _s.Ui.Escribir("Emulador detenido.", Nivel.Ok);
    }

    /// <summary>Elige primero; si cancelas, el actual sigue abierto.</summary>
    public bool Cambiar()
    {
        if (!_s.AfirmarComp(Componente.Movil) || !HayAndroid()) return false;
        var estado = Estado();
        if (estado.Codigo == CodigoEmu.Ok) _s.Ui.Escribir($"Emulador actual: {NombreAvd(estado.Serial!)} ({estado.Serial})", Nivel.Tenue);
        else if (estado.Codigo == CodigoEmu.NoResponde) _s.Ui.Escribir($"Emulador actual: {estado.Texto}", Nivel.Error);

        var nuevo = ElegirAvd();
        if (nuevo is null) { _s.Ui.Escribir("Cancelado.", Nivel.Tenue); return false; }
        if (estado.Codigo != CodigoEmu.Ninguno) Detener();   // si no responde, se fuerza solo
        return Iniciar(nuevo);
    }

    /// <summary>Arranque desde cero (cuando el estado guardado da problemas).</summary>
    public bool IniciarFrio(string? nombre = null)
    {
        if (!_s.AfirmarComp(Componente.Movil) || !HayAndroid()) return false;
        var estado = Estado();
        if (estado.Codigo == CodigoEmu.Ok)
        {
            if (!_s.Ui.Confirmar("Hay un emulador corriendo. ¿Cerrarlo y arrancar desde cero?", true)) return false;
            Detener();
        }
        else if (estado.Codigo == CodigoEmu.NoResponde) Detener(forzar: true);
        nombre ??= ElegirAvd();
        if (nombre is null) { _s.Ui.Escribir("Cancelado.", Nivel.Tenue); return false; }
        return Iniciar(nombre, frio: true);
    }
}
