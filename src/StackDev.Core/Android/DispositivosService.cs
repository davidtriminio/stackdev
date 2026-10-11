using System.Text.RegularExpressions;
using StackDev.Core.Ui;

namespace StackDev.Core.Android;

/// <summary>
/// Teléfonos y tablets Android por red (depuración inalámbrica): ver los dispositivos que adb conoce,
/// conectar, emparejar (Android 11+) y desconectar. Las direcciones usadas se recuerdan por proyecto.
/// </summary>
public sealed class DispositivosService
{
    private readonly Sesion _s;

    public DispositivosService(Sesion s) { _s = s; }

    public static string Etiqueta(DispositivoAdb d)
    {
        var tipo = d.EsEmulador ? "emulador" : d.EsRed ? "red" : "USB";
        var nombre = d.Descripcion.Length > 0 ? $"{d.Descripcion}  " : "";
        return $"{nombre}{d.Serial}  [{tipo}]" + (d.Listo ? "" : $"  ({Explicar(d.Estado)})");
    }

    private static string Explicar(string estado) => estado switch
    {
        "unauthorized" => "sin autorizar: acepta el aviso «¿Permitir depuración?» en el teléfono",
        "offline" => "sin conexión: reconéctalo",
        _ => estado,
    };

    private bool HayAdb()
    {
        if (_s.Adb is not null) return true;
        _s.Ui.Escribir("No encontré el Android SDK (platform-tools).", Nivel.Error);
        return false;
    }

    private static readonly Regex Direccion = new(@"^[\w\.\-]+:\d{1,5}$", RegexOptions.Compiled);

    /// <summary>«192.168.1.20» pasa a «192.168.1.20:5555». Null si no parece una dirección.</summary>
    public static string? Normalizar(string? texto)
    {
        var t = texto?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        if (!t.Contains(':')) t += ":5555";
        return Direccion.IsMatch(t) ? t : null;
    }

    // ------------------------------------------------------------ Ver

    public bool Mostrar()
    {
        if (!_s.AfirmarComp(Config.Componente.Movil) || !HayAdb()) return false;
        var lista = _s.Adb!.Dispositivos();
        if (lista.Count == 0) _s.Ui.Escribir("adb no ve ningún dispositivo ni emulador.", Nivel.Aviso);
        else
        {
            _s.Ui.Escribir("Dispositivos que ve adb:", Nivel.Info);
            foreach (var d in lista) _s.Ui.Escribir("  " + Etiqueta(d), d.Listo ? Nivel.Ok : Nivel.Aviso);
        }
        var guardados = _s.Estado.LeerDispositivosRed(_s.Clave);
        if (guardados.Count > 0) _s.Ui.Escribir("Guardados para este proyecto: " + string.Join(", ", guardados), Nivel.Tenue);
        return true;
    }

    // ------------------------------------------------------------ Conectar

    public bool Conectar()
    {
        if (!_s.AfirmarComp(Config.Componente.Movil) || !HayAdb()) return false;

        if (!string.IsNullOrWhiteSpace(_s.Op.Direccion))
        {
            var d = Normalizar(_s.Op.Direccion);
            if (d is null) { _s.Ui.Escribir($"'{_s.Op.Direccion}' no es una dirección válida (ip:puerto).", Nivel.Error); return false; }
            return ConectarA(d);
        }

        var guardados = _s.Estado.LeerDispositivosRed(_s.Clave);
        if (!_s.Ui.Interactiva)
        {
            if (guardados.Count == 0) { _s.Ui.Escribir("Indica la dirección con --direccion ip:puerto.", Nivel.Error); return false; }
            return guardados.Select(ConectarA).ToList().All(x => x);
        }

        var opciones = guardados.Select(g => $"Reconectar {g}").ToList();
        var iNueva = opciones.Count; opciones.Add("Escribir una dirección nueva (ip:puerto)");
        var iUsb = opciones.Count; opciones.Add("Pasar un teléfono conectado por USB a Wi-Fi");
        var i = _s.Ui.Elegir("Conectar dispositivo por red:", opciones, 0);
        if (i < 0) { _s.Ui.Escribir("Cancelado.", Nivel.Tenue); return false; }
        if (i < guardados.Count) return ConectarA(guardados[i]);
        if (i == iUsb) return DesdeUsb();

        _s.Ui.Escribir("En el teléfono: Opciones de desarrollador → Depuración inalámbrica; ahí aparece «Dirección IP y puerto».", Nivel.Tenue);
        var texto = _s.Ui.Preguntar("Dirección (ip:puerto, p. ej. 192.168.1.20:37215)");
        var dir = Normalizar(texto);
        if (dir is null) { _s.Ui.Escribir("Dirección no válida.", Nivel.Error); return false; }
        return ConectarA(dir);
    }

    private bool ConectarA(string direccion)
    {
        _s.Ui.Escribir($"Conectando a {direccion}...", Nivel.Info);
        var (ok, texto) = _s.Adb!.Conectar(direccion);
        if (!ok)
        {
            _s.Ui.Escribir(texto, Nivel.Error);
            _s.Ui.Escribir("Revisa que el teléfono y este equipo estén en la misma red Wi-Fi.", Nivel.Aviso);
            _s.Ui.Escribir("En Android 11+ el puerto cambia cada vez que se activa la depuración inalámbrica; y la primera vez hay que emparejar (acción «Emparejar»).", Nivel.Tenue);
            return false;
        }
        _s.Estado.AgregarDispositivoRed(_s.Clave, direccion);
        var d = _s.Adb.Dispositivos().FirstOrDefault(x => x.Serial.Equals(direccion, StringComparison.OrdinalIgnoreCase));
        if (d is { Listo: false })
        {
            _s.Ui.Escribir(Etiqueta(d), Nivel.Aviso);
            return true;
        }
        _s.Ui.Escribir(d is null ? $"Conectado a {direccion}." : $"Conectado: {Etiqueta(d)}", Nivel.Ok);
        return true;
    }

    /// <summary>Método clásico: con el cable puesto, <c>adb tcpip 5555</c> y luego conectar por la IP del teléfono.</summary>
    private bool DesdeUsb()
    {
        var usb = _s.Adb!.Dispositivos().Where(d => d.Listo && !d.EsEmulador && !d.EsRed).ToList();
        if (usb.Count == 0)
        {
            _s.Ui.Escribir("No hay ningún teléfono conectado por USB (con la depuración USB activada).", Nivel.Error);
            return false;
        }
        var d = usb[0];
        if (usb.Count > 1)
        {
            var i = _s.Ui.Elegir("¿Cuál teléfono?", usb.Select(Etiqueta).ToList(), 0);
            if (i < 0) { _s.Ui.Escribir("Cancelado.", Nivel.Tenue); return false; }
            d = usb[i];
        }
        var ip = _s.Adb.ActivarTcpip(d.Serial);
        if (ip is null)
        {
            var texto = _s.Ui.Preguntar("No pude leer su IP. Escríbela (Ajustes → Wi-Fi → tu red)");
            ip = texto?.Trim();
            if (string.IsNullOrEmpty(ip)) { _s.Ui.Escribir("Cancelado.", Nivel.Tenue); return false; }
        }
        Thread.Sleep(1500);
        var dir = Normalizar(ip);
        if (dir is null) { _s.Ui.Escribir("IP no válida.", Nivel.Error); return false; }
        var ok = ConectarA(dir);
        if (ok) _s.Ui.Escribir("Ya puedes desenchufar el cable. (El modo red dura hasta que reinicies el teléfono.)", Nivel.Tenue);
        return ok;
    }

    // ------------------------------------------------------------ Emparejar

    public bool Emparejar()
    {
        if (!_s.AfirmarComp(Config.Componente.Movil) || !HayAdb()) return false;

        string? dirPar = _s.Op.Direccion, codigo = _s.Op.CodigoEmparejar;
        if (string.IsNullOrWhiteSpace(dirPar) || string.IsNullOrWhiteSpace(codigo))
        {
            if (!_s.Ui.Interactiva) { _s.Ui.Escribir("Indica --direccion ip:puerto y --codigo-emparejar.", Nivel.Error); return false; }
            _s.Ui.Escribir("En el teléfono: Depuración inalámbrica → «Vincular dispositivo con código de sincronización».", Nivel.Tenue);
            _s.Ui.Escribir("Te muestra una IP:puerto (solo para vincular) y un código de 6 dígitos.", Nivel.Tenue);
            dirPar = _s.Ui.Preguntar("IP:puerto de la ventana de vinculación");
            codigo = _s.Ui.Preguntar("Código de 6 dígitos");
        }
        var dir = Normalizar(dirPar);
        if (dir is null || string.IsNullOrWhiteSpace(codigo)) { _s.Ui.Escribir("Faltan datos o la dirección no es válida.", Nivel.Error); return false; }

        _s.Ui.Escribir($"Emparejando con {dir}...", Nivel.Info);
        var (ok, texto) = _s.Adb!.Emparejar(dir, codigo.Trim());
        _s.Ui.Escribir(texto, ok ? Nivel.Ok : Nivel.Error);
        if (!ok) return false;

        if (!_s.Ui.Interactiva) return true;
        _s.Ui.Escribir("Ahora conéctalo con la IP:puerto de la pantalla principal de Depuración inalámbrica (es otro puerto).", Nivel.Tenue);
        var conectar = Normalizar(_s.Ui.Preguntar("IP:puerto para conectar (vacío = después)"));
        return conectar is null || ConectarA(conectar);
    }

    // ------------------------------------------------------------ Reenviar puertos

    /// <summary>
    /// <c>adb reverse</c> de los puertos de la API y la web a los dispositivos listos (por defecto todos): en el teléfono,
    /// <c>http://localhost:PUERTO</c> llega a tu PC sin abrir nada a la red. Se pierde al reconectar, por eso se repite al ejecutar la app.
    /// </summary>
    public bool Reenviar(IEnumerable<DispositivoAdb>? destinos = null, bool silencioso = false)
    {
        if (!_s.AfirmarComp(Config.Componente.Movil) || !HayAdb()) return false;
        var puertos = new List<(string Nombre, int Puerto)>();
        if (_s.Ctx.Tiene(Config.Componente.Api)) puertos.Add(("API", _s.Ctx.Puerto(Config.Componente.Api)));
        if (_s.Ctx.Tiene(Config.Componente.Web)) puertos.Add(("web", _s.Ctx.Puerto(Config.Componente.Web)));
        if (puertos.Count == 0) { if (!silencioso) _s.Ui.Escribir("Este proyecto no tiene API ni web que reenviar.", Nivel.Aviso); return true; }

        var lista = (destinos ?? _s.Adb!.Dispositivos()).Where(d => d.Listo).ToList();
        if (lista.Count == 0) { if (!silencioso) _s.Ui.Escribir("No hay dispositivos listos.", Nivel.Aviso); return false; }

        var todoOk = true;
        foreach (var d in lista)
            foreach (var (nombre, puerto) in puertos)
            {
                var (ok, texto) = _s.Adb!.Reenviar(d.Serial, puerto);
                if (ok) { if (!silencioso) _s.Ui.Escribir($"{d.Serial}: localhost:{puerto} ({nombre}) → tu PC", Nivel.Ok); }
                else { todoOk = false; _s.Ui.Escribir($"{d.Serial}: no pude reenviar {puerto}: {texto}", Nivel.Error); }
            }
        if (silencioso && todoOk)
            _s.Ui.Escribir($"Puertos reenviados al dispositivo: {string.Join(", ", puertos.Select(p => "localhost:" + p.Puerto))}", Nivel.Tenue);
        else if (!silencioso && todoOk)
            _s.Ui.Escribir("En la app usa http://localhost:PUERTO. Android bloquea HTTP sin cifrar: en debug activa usesCleartextTraffic.", Nivel.Tenue);
        return todoOk;
    }

    // ------------------------------------------------------------ Desconectar

    public bool Desconectar()
    {
        if (!_s.AfirmarComp(Config.Componente.Movil) || !HayAdb()) return false;
        var redes = _s.Adb!.Dispositivos().Where(d => d.EsRed).ToList();
        if (redes.Count == 0) { _s.Ui.Escribir("No hay dispositivos conectados por red.", Nivel.Tenue); return true; }

        string? dir = null;
        if (!string.IsNullOrWhiteSpace(_s.Op.Direccion)) dir = Normalizar(_s.Op.Direccion);
        else if (_s.Ui.Interactiva && redes.Count > 1)
        {
            var opciones = redes.Select(Etiqueta).ToList();
            opciones.Add("Todos");
            var i = _s.Ui.Elegir("¿Cuál desconectar?", opciones, 0);
            if (i < 0) { _s.Ui.Escribir("Cancelado.", Nivel.Tenue); return false; }
            if (i < redes.Count) dir = redes[i].Serial;
        }
        else if (redes.Count == 1) dir = redes[0].Serial;

        // la app que corría en ese dispositivo se queda sin destino
        var texto = _s.Adb.Desconectar(dir);
        _s.Ui.Escribir(texto.Length > 0 ? texto : "Desconectado.", Nivel.Ok);
        return true;
    }
}
