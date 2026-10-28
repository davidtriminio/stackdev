using System.Text.RegularExpressions;
using StackDev.Core.Ui;

namespace StackDev.Core.Android;

/// <summary>
/// Android phones and tablets over the network (wireless debugging): see the devices adb knows,
/// connect, pair (Android 11+) and disconnect. The addresses used are remembered per project.
/// </summary>
public sealed class DeviceService
{
    private readonly Session _s;

    public DeviceService(Session s) { _s = s; }

    public static string Label(AdbDevice d)
    {
        var type = d.IsEmulator ? "emulador" : d.IsNetwork ? "red" : "USB";
        var name = d.Description.Length > 0 ? $"{d.Description}  " : "";
        return $"{name}{d.Serial}  [{type}]" + (d.Ready ? "" : $"  ({Explain(d.State)})");
    }

    private static string Explain(string state) => state switch
    {
        "unauthorized" => "sin autorizar: acepta el aviso «¿Permitir depuración?» en el teléfono",
        "offline" => "sin conexión: reconéctalo",
        _ => state,
    };

    private bool HasAdb()
    {
        if (_s.Adb is not null) return true;
        _s.Ui.Write("No encontré el Android SDK (platform-tools).", Level.Error);
        return false;
    }

    private static readonly Regex Address = new(@"^[\w\.\-]+:\d{1,5}$", RegexOptions.Compiled);

    /// <summary>"192.168.1.20" becomes "192.168.1.20:5555". Null if it does not look like an address.</summary>
    public static string? Normalize(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        if (!t.Contains(':')) t += ":5555";
        return Address.IsMatch(t) ? t : null;
    }

    // ------------------------------------------------------------ View

    public bool Show()
    {
        if (!_s.AssertComp(Config.ProjectComponent.Mobile) || !HasAdb()) return false;
        var list = _s.Adb!.Devices();
        if (list.Count == 0) _s.Ui.Write("adb no ve ningún dispositivo ni emulador.", Level.Warning);
        else
        {
            _s.Ui.Write("Dispositivos que ve adb:", Level.Info);
            foreach (var d in list) _s.Ui.Write("  " + Label(d), d.Ready ? Level.Ok : Level.Warning);
        }
        var saved = _s.State.ReadNetworkDevices(_s.Key);
        if (saved.Count > 0) _s.Ui.Write("Guardados para este proyecto: " + string.Join(", ", saved), Level.Muted);
        return true;
    }

    // ------------------------------------------------------------ Conectar

    public bool Connect()
    {
        if (!_s.AssertComp(Config.ProjectComponent.Mobile) || !HasAdb()) return false;

        if (!string.IsNullOrWhiteSpace(_s.Op.Address))
        {
            var d = Normalize(_s.Op.Address);
            if (d is null) { _s.Ui.Write($"'{_s.Op.Address}' no es una dirección válida (ip:puerto).", Level.Error); return false; }
            return ConnectTo(d);
        }

        var saved = _s.State.ReadNetworkDevices(_s.Key);
        if (!_s.Ui.Interactive)
        {
            if (saved.Count == 0) { _s.Ui.Write("Indica la dirección con --direccion ip:puerto.", Level.Error); return false; }
            return saved.Select(ConnectTo).ToList().All(x => x);
        }

        var options = saved.Select(g => $"Reconectar {g}").ToList();
        var iNew = options.Count; options.Add("Escribir una dirección nueva (ip:puerto)");
        var iUsb = options.Count; options.Add("Pasar un teléfono conectado por USB a Wi-Fi");
        var i = _s.Ui.Choose("Conectar dispositivo por red:", options, 0);
        if (i < 0) { _s.Ui.Write("Cancelado.", Level.Muted); return false; }
        if (i < saved.Count) return ConnectTo(saved[i]);
        if (i == iUsb) return FromUsb();

        _s.Ui.Write("En el teléfono: Opciones de desarrollador → Depuración inalámbrica; ahí aparece «Dirección IP y puerto».", Level.Muted);
        var text = _s.Ui.Ask("Dirección (ip:puerto, p. ej. 192.168.1.20:37215)");
        var dir = Normalize(text);
        if (dir is null) { _s.Ui.Write("Dirección no válida.", Level.Error); return false; }
        return ConnectTo(dir);
    }

    private bool ConnectTo(string address)
    {
        _s.Ui.Write($"Conectando a {address}...", Level.Info);
        var (ok, text) = _s.Adb!.Connect(address);
        if (!ok)
        {
            _s.Ui.Write(text, Level.Error);
            _s.Ui.Write("Revisa que el teléfono y este equipo estén en la misma red Wi-Fi.", Level.Warning);
            _s.Ui.Write("En Android 11+ el puerto cambia cada vez que se activa la depuración inalámbrica; y la primera vez hay que emparejar (acción «Emparejar»).", Level.Muted);
            return false;
        }
        _s.State.AddNetworkDevice(_s.Key, address);
        var d = _s.Adb.Devices().FirstOrDefault(x => x.Serial.Equals(address, StringComparison.OrdinalIgnoreCase));
        if (d is { Ready: false })
        {
            _s.Ui.Write(Label(d), Level.Warning);
            return true;
        }
        _s.Ui.Write(d is null ? $"Conectado a {address}." : $"Conectado: {Label(d)}", Level.Ok);
        return true;
    }

    /// <summary>Classic method: with the cable plugged in, <c>adb tcpip 5555</c> and then connect through the phone's IP.</summary>
    private bool FromUsb()
    {
        var usb = _s.Adb!.Devices().Where(d => d.Ready && !d.IsEmulator && !d.IsNetwork).ToList();
        if (usb.Count == 0)
        {
            _s.Ui.Write("No hay ningún teléfono conectado por USB (con la depuración USB activada).", Level.Error);
            return false;
        }
        var d = usb[0];
        if (usb.Count > 1)
        {
            var i = _s.Ui.Choose("¿Cuál teléfono?", usb.Select(Label).ToList(), 0);
            if (i < 0) { _s.Ui.Write("Cancelado.", Level.Muted); return false; }
            d = usb[i];
        }
        var ip = _s.Adb.EnableTcpip(d.Serial);
        if (ip is null)
        {
            var text = _s.Ui.Ask("No pude leer su IP. Escríbela (Ajustes → Wi-Fi → tu red)");
            ip = text?.Trim();
            if (string.IsNullOrEmpty(ip)) { _s.Ui.Write("Cancelado.", Level.Muted); return false; }
        }
        Thread.Sleep(1500);
        var dir = Normalize(ip);
        if (dir is null) { _s.Ui.Write("IP no válida.", Level.Error); return false; }
        var ok = ConnectTo(dir);
        if (ok) _s.Ui.Write("Ya puedes desenchufar el cable. (El modo red dura hasta que reinicies el teléfono.)", Level.Muted);
        return ok;
    }

    // ------------------------------------------------------------ Emparejar

    public bool Pair()
    {
        if (!_s.AssertComp(Config.ProjectComponent.Mobile) || !HasAdb()) return false;

        string? dirPair = _s.Op.Address, code = _s.Op.PairingCode;
        if (string.IsNullOrWhiteSpace(dirPair) || string.IsNullOrWhiteSpace(code))
        {
            if (!_s.Ui.Interactive) { _s.Ui.Write("Indica --direccion ip:puerto y --codigo-emparejar.", Level.Error); return false; }
            _s.Ui.Write("En el teléfono: Depuración inalámbrica → «Vincular dispositivo con código de sincronización».", Level.Muted);
            _s.Ui.Write("Te muestra una IP:puerto (solo para vincular) y un código de 6 dígitos.", Level.Muted);
            dirPair = _s.Ui.Ask("IP:puerto de la ventana de vinculación");
            code = _s.Ui.Ask("Código de 6 dígitos");
        }
        var dir = Normalize(dirPair);
        if (dir is null || string.IsNullOrWhiteSpace(code)) { _s.Ui.Write("Faltan datos o la dirección no es válida.", Level.Error); return false; }

        _s.Ui.Write($"Emparejando con {dir}...", Level.Info);
        var (ok, text) = _s.Adb!.Pair(dir, code.Trim());
        _s.Ui.Write(text, ok ? Level.Ok : Level.Error);
        if (!ok) return false;

        if (!_s.Ui.Interactive) return true;
        _s.Ui.Write("Ahora conéctalo con la IP:puerto de la pantalla principal de Depuración inalámbrica (es otro puerto).", Level.Muted);
        var connect = Normalize(_s.Ui.Ask("IP:puerto para conectar (vacío = después)"));
        return connect is null || ConnectTo(connect);
    }

    // ------------------------------------------------------------ Reenviar puertos

    /// <summary>
    /// <c>adb reverse</c> of the API and web ports to the ready devices (all by default): on the phone,
    /// <c>http://localhost:PORT</c> reaches your PC without opening anything to the network. It is lost on reconnect, so it is repeated when the app runs.
    /// </summary>
    public bool Forward(IEnumerable<AdbDevice>? destinations = null, bool silent = false)
    {
        if (!_s.AssertComp(Config.ProjectComponent.Mobile) || !HasAdb()) return false;
        var ports = new List<(string Name, int Port)>();
        if (_s.Ctx.Has(Config.ProjectComponent.Api)) ports.Add(("API", _s.Ctx.Port(Config.ProjectComponent.Api)));
        if (_s.Ctx.Has(Config.ProjectComponent.Web)) ports.Add(("web", _s.Ctx.Port(Config.ProjectComponent.Web)));
        if (ports.Count == 0) { if (!silent) _s.Ui.Write("Este proyecto no tiene API ni web que reenviar.", Level.Warning); return true; }

        var list = (destinations ?? _s.Adb!.Devices()).Where(d => d.Ready).ToList();
        if (list.Count == 0) { if (!silent) _s.Ui.Write("No hay dispositivos listos.", Level.Warning); return false; }

        var allOk = true;
        foreach (var d in list)
            foreach (var (name, port) in ports)
            {
                var (ok, text) = _s.Adb!.Forward(d.Serial, port);
                if (ok) { if (!silent) _s.Ui.Write($"{d.Serial}: localhost:{port} ({name}) → tu PC", Level.Ok); }
                else { allOk = false; _s.Ui.Write($"{d.Serial}: no pude reenviar {port}: {text}", Level.Error); }
            }
        if (silent && allOk)
            _s.Ui.Write($"Puertos reenviados al dispositivo: {string.Join(", ", ports.Select(p => "localhost:" + p.Port))}", Level.Muted);
        else if (!silent && allOk)
            _s.Ui.Write("En la app usa http://localhost:PUERTO. Android bloquea HTTP sin cifrar: en debug activa usesCleartextTraffic.", Level.Muted);
        return allOk;
    }

    // ------------------------------------------------------------ Desconectar

    public bool Disconnect()
    {
        if (!_s.AssertComp(Config.ProjectComponent.Mobile) || !HasAdb()) return false;
        var networks = _s.Adb!.Devices().Where(d => d.IsNetwork).ToList();
        if (networks.Count == 0) { _s.Ui.Write("No hay dispositivos conectados por red.", Level.Muted); return true; }

        string? dir = null;
        if (!string.IsNullOrWhiteSpace(_s.Op.Address)) dir = Normalize(_s.Op.Address);
        else if (_s.Ui.Interactive && networks.Count > 1)
        {
            var options = networks.Select(Label).ToList();
            options.Add("Todos");
            var i = _s.Ui.Choose("¿Cuál desconectar?", options, 0);
            if (i < 0) { _s.Ui.Write("Cancelado.", Level.Muted); return false; }
            if (i < networks.Count) dir = networks[i].Serial;
        }
        else if (networks.Count == 1) dir = networks[0].Serial;

        // the app that was running on that device is left without a destination
        var text = _s.Adb.Disconnect(dir);
        _s.Ui.Write(text.Length > 0 ? text : "Desconectado.", Level.Ok);
        return true;
    }
}
