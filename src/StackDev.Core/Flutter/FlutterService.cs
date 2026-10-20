using StackDev.Core.Android;
using StackDev.Core.Config;
using StackDev.Core.Ui;

namespace StackDev.Core.Flutter;

/// <summary>flutter run (debug, profile o release), detener la app, analyze, test, format, build_runner, pub get y clean.</summary>
public sealed class FlutterService
{
    private readonly Sesion _s;

    public FlutterService(Sesion s) { _s = s; }

    public bool AppCorriendo => _s.Servicios.AppsVivas().Count > 0;

    public string CambiarModo()
    {
        _s.ModoBuild = _s.ModoBuild switch { "debug" => "profile", "profile" => "release", _ => "debug" };
        _s.Ui.Escribir($"Modo de build: {_s.ModoBuild}", Nivel.Ok);
        if (_s.ModoBuild != "debug")
            _s.Ui.Escribir($"En {_s.ModoBuild} no hay hot reload; sirve para medir rendimiento o probar la versión final.", Nivel.Tenue);
        return _s.ModoBuild;
    }

    private static string ClaveApp(DispositivoAdb d) =>
        d.EsEmulador ? "app" : "app-" + System.Text.RegularExpressions.Regex.Replace(d.Serial, @"[^\w\-]", "_");

    /// <summary>Dispositivos listos para correr la app (el emulador solo si responde).</summary>
    private List<DispositivoAdb>? Destinos()
    {
        var todos = _s.Adb!.Dispositivos();
        var emu = _s.Emulador.Estado();
        var listos = todos.Where(d => d.Listo && (!d.EsEmulador || emu.Codigo == CodigoEmu.Ok)).ToList();
        if (listos.Count > 0) return listos;

        foreach (var d in todos.Where(d => !d.Listo && !d.EsEmulador))
            _s.Ui.Escribir(DispositivosService.Etiqueta(d), Nivel.Aviso);
        if (emu.Codigo == CodigoEmu.NoResponde)
        {
            _s.Ui.Escribir($"El emulador no responde: {emu.Texto}", Nivel.Error);
            _s.Ui.Escribir("Cámbialo o fuerza su cierre.", Nivel.Aviso);
        }
        else _s.Ui.Escribir("No hay emulador ni teléfono conectado. Inicia el emulador o conecta un dispositivo (USB o red).", Nivel.Aviso);
        return null;
    }

    /// <summary>Decide en qué dispositivos correr: --dispositivo, el único que hay, o pregunta (con la opción «Todos»).</summary>
    private List<DispositivoAdb>? Elegir(List<DispositivoAdb> listos)
    {
        var pedido = _s.Op.Dispositivo?.Trim();
        if (!string.IsNullOrEmpty(pedido))
        {
            if (pedido.Equals("todos", StringComparison.OrdinalIgnoreCase)) return listos;
            var d = listos.FirstOrDefault(x => x.Serial.Equals(pedido, StringComparison.OrdinalIgnoreCase));
            if (d is null)
            {
                _s.Ui.Escribir($"El dispositivo '{pedido}' no está listo. Hay: {string.Join(", ", listos.Select(x => x.Serial))}", Nivel.Error);
                return null;
            }
            return new() { d };
        }
        if (listos.Count == 1) return listos;
        if (!_s.Ui.Interactiva)
        {
            _s.Ui.Escribir($"Hay varios dispositivos ({string.Join(", ", listos.Select(x => x.Serial))}): usa --dispositivo <serial|todos>.", Nivel.Error);
            return null;
        }
        var textos = listos.Select(DispositivosService.Etiqueta).ToList();
        textos.Add($"Todos los dispositivos ({listos.Count})");
        var i = _s.Ui.Elegir("¿En qué dispositivo ejecutar la app?", textos, 0);
        if (i < 0) { _s.Ui.Escribir("Cancelado.", Nivel.Tenue); return null; }
        return i == listos.Count ? listos : new() { listos[i] };
    }

    /// <summary>
    /// <c>flutter run -d serial</c> más <c>movil.argsRun</c> y los argumentos extra, en el emulador, en un teléfono (USB o red) o en
    /// varios a la vez. Cada dispositivo corre en su propia ventana o pestaña (necesita su terminal para las teclas r, R y q) o,
    /// con <c>--misma-ventana</c> y un solo dispositivo, en esta consola.
    /// </summary>
    public bool Ejecutar()
    {
        if (!_s.AfirmarComp(Componente.Movil)) return false;
        if (_s.Adb is null)
        {
            _s.Ui.Escribir("No encontré el Android SDK (emulator y platform-tools).", Nivel.Error);
            return false;
        }
        var listos = Destinos();
        if (listos is null) return false;
        var destinos = Elegir(listos);
        if (destinos is null) return false;
        if (destinos.Count > 1 && _s.Op.MismaVentana)
        {
            _s.Ui.Escribir("Con --misma-ventana solo se puede un dispositivo a la vez.", Nivel.Error);
            return false;
        }

        _s.Dispositivos.Reenviar(destinos, silencioso: true);

        var modo = _s.ModoBuild switch { "profile" => "--profile", "release" => "--release", _ => "" };
        var cond = _s.Condiciones.Texto();
        var teclas = _s.ModoBuild == "debug" ? "r = hot reload | R = hot restart | q = terminar" : $"modo {_s.ModoBuild}: sin hot reload | q = terminar";
        var carpeta = _s.Ctx.Carpeta(Componente.Movil);
        var ok = true;

        foreach (var d in destinos)
        {
            var clave = ClaveApp(d);
            if (_s.Servicios.Registro.Viva(clave) is not null && !_s.Op.MismaVentana)
            {
                if (!_s.Ui.Confirmar($"La app ya está corriendo en {d.Serial}. ¿Cerrarla y volver a lanzarla?", true)) continue;
                _s.Servicios.DetenerProceso(clave);
                Thread.Sleep(800);
            }

            // El emulador ve el PC como 10.0.2.2; un teléfono no. Con el reenvío de puertos le sirve localhost.
            var define = "";
            var yaDefinida = $"{_s.Ctx.Config.Movil?.ArgsRun} {_s.Op.ArgsExtra}".Contains("API_BASE_URL", StringComparison.Ordinal);
            if (!d.EsEmulador && !yaDefinida && _s.Ctx.Tiene(Componente.Api))
            {
                define = $"--dart-define=API_BASE_URL=http://localhost:{_s.Ctx.Puerto(Componente.Api)}";
                _s.Ui.Escribir($"Teléfono: la app llamará a http://localhost:{_s.Ctx.Puerto(Componente.Api)} (puerto reenviado a tu PC).", Nivel.Tenue);
            }
            var args = System.Text.RegularExpressions.Regex.Replace(
                $"run -d {d.Serial} {modo} {define} {_s.Ctx.Config.Movil?.ArgsRun} {_s.Op.ArgsExtra}", @"\s+", " ").Trim();
            _s.Ui.Escribir($"\nflutter {args}   ({DispositivosService.Etiqueta(d)})", Nivel.Info);
            if (cond != "Normal" && d.EsEmulador) _s.Ui.Escribir($"Condiciones activas: {cond}", Nivel.Aviso);

            if (_s.Op.MismaVentana)
            {
                _s.Ui.Escribir($"Teclas: {teclas}", Nivel.Tenue);
                _s.Ejecutor.Aqui($"flutter {args}", carpeta);
                _s.Ui.Escribir("\nSesión de flutter terminada.", Nivel.Ok);
                return true;
            }
            var etiqueta = d.Descripcion.Length > 0 ? d.Descripcion : d.Serial;
            if (!_s.Ejecutor.EnVentana(clave, $"flutter run - {_s.Ctx.Nombre} [{etiqueta}]", carpeta, $"flutter {args}"))
            {
                _s.Ui.Escribir($"No se pudo abrir la ventana de la app para {d.Serial}.", Nivel.Error);
                ok = false;
                continue;
            }
            _s.Ui.Escribir($"La app corre en {etiqueta} en su propia ventana ({teclas}).", Nivel.Ok);
        }
        if (ok) _s.Ui.Escribir("Puedes seguir usando la herramienta.", Nivel.Tenue);
        return ok;
    }

    public bool Detener()
    {
        var n = _s.Servicios.DetenerApps();
        if (n > 0) { _s.Ui.Escribir(n == 1 ? "App detenida." : $"{n} sesiones de la app detenidas.", Nivel.Ok); return true; }
        _s.Ui.Escribir("La app no estaba corriendo desde la herramienta.", Nivel.Tenue);
        return true;
    }

    public int PubGet() =>
        _s.AfirmarComp(Componente.Movil) ? _s.Ejecutor.Aqui("flutter pub get", _s.Ctx.Carpeta(Componente.Movil)) : 1;

    /// <summary><c>flutter clean</c> y, si salió bien, <c>flutter pub get</c>.</summary>
    public int Limpiar() =>
        _s.AfirmarComp(Componente.Movil)
            ? _s.Ejecutor.Aqui("flutter clean; if ($LASTEXITCODE -eq 0) { flutter pub get }", _s.Ctx.Carpeta(Componente.Movil))
            : 1;
}
