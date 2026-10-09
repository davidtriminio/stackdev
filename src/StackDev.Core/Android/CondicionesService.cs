using System.Globalization;
using StackDev.Core.State;
using StackDev.Core.Ui;

namespace StackDev.Core.Android;

public sealed class CondCtx
{
    public CondCtx(Sesion s, string serial) { Sesion = s; Serial = serial; }
    public Sesion Sesion { get; }
    public string Serial { get; }
    public AdbClient Adb => Sesion.Adb!;
    public void Sh(string c) => Adb.Sh(Serial, c, 15);
    public void Emu(string c) => Adb.Emu(Serial, c, 10);
}

public sealed record Preset(int N, string Cat, string Txt, bool Normal, Func<CondCtx, string?> Cmd);

/// <summary>
/// Condiciones del dispositivo para probar la app (red, velocidad, batería, tema, texto, pantalla, GPS).
/// Se aplican en vivo con adb, varias a la vez, y se recuerdan por AVD para poder restablecerlas.
/// </summary>
public sealed class CondicionesService
{
    private readonly Sesion _s;

    public CondicionesService(Sesion s) { _s = s; }

    private static void ModoAvion(CondCtx c, bool activo)
    {
        var r = c.Adb.Sh(c.Serial, $"cmd connectivity airplane-mode {(activo ? "enable" : "disable")}", 15) ?? "";
        if (System.Text.RegularExpressions.Regex.IsMatch(r, "Unknown|Error|Exception|not found", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            // Android 10 o anterior
            c.Sh($"settings put global airplane_mode_on {(activo ? 1 : 0)}");
            c.Sh($"am broadcast -a android.intent.action.AIRPLANE_MODE --ez state {(activo ? "true" : "false")}");
        }
    }

    private static string? Red(CondCtx c, bool wifi, bool datos)
    {
        ModoAvion(c, false);
        c.Sh($"svc wifi {(wifi ? "enable" : "disable")}");
        c.Sh($"svc data {(datos ? "enable" : "disable")}");
        return null;
    }

    private static string? Velocidad(CondCtx c, string velocidad, string retardo)
    {
        c.Emu($"network speed {velocidad}");
        c.Emu($"network delay {retardo}");
        return null;
    }

    private static string? Bateria(CondCtx c, int nivel, bool cargando, bool ahorro = false)
    {
        if (cargando) { c.Emu("power ac on"); c.Emu("power status charging"); }
        else { c.Emu("power ac off"); c.Emu("power status discharging"); }
        c.Emu($"power capacity {nivel}");
        var m = ahorro ? 1 : 0;
        c.Sh($"settings put global low_power {m}");
        c.Sh($"cmd power set-mode {m}");
        return null;
    }

    private static string? Pantalla(CondCtx c, int rotacion)
    {
        c.Sh("settings put system accelerometer_rotation 0");
        c.Sh($"settings put system user_rotation {rotacion}");
        return null;
    }

    /// <summary>Pide "latitud, longitud" y fija el GPS. <c>geo fix</c> pide la longitud primero, con cultura invariante.</summary>
    private static string? Gps(CondCtx c)
    {
        var t = c.Sesion.Ui.Preguntar("Latitud, longitud (ej: 14.0723, -87.1921; vacío = cancelar)")?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        var partes = t.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var inv = CultureInfo.InvariantCulture;
        if (partes.Length != 2 ||
            !double.TryParse(partes[0], NumberStyles.Float, inv, out var lat) ||
            !double.TryParse(partes[1], NumberStyles.Float, inv, out var lon))
        {
            c.Sesion.Ui.Escribir("Formato no válido.", Nivel.Error);
            return null;
        }
        c.Emu($"geo fix {lon.ToString(inv)} {lat.ToString(inv)}");
        return $"{lat.ToString(inv)}, {lon.ToString(inv)}";
    }

    public static readonly IReadOnlyList<Preset> Presets = new List<Preset>
    {
        new(1,  "Red",       "Normal",            true,  c => Red(c, true, true)),
        new(2,  "Red",       "Modo avión",        false, c => { ModoAvion(c, true); return null; }),
        new(3,  "Red",       "Sin internet",      false, c => Red(c, false, false)),
        new(4,  "Red",       "Solo datos",        false, c => Red(c, false, true)),
        new(5,  "Red",       "Solo Wi-Fi",        false, c => Red(c, true, false)),

        new(6,  "Velocidad", "Normal",            true,  c => Velocidad(c, "full", "none")),
        new(7,  "Velocidad", "4G",                false, c => Velocidad(c, "lte", "none")),
        new(8,  "Velocidad", "3G",                false, c => Velocidad(c, "umts", "umts")),
        new(9,  "Velocidad", "EDGE (lenta)",      false, c => Velocidad(c, "edge", "edge")),
        new(10, "Velocidad", "GPRS (muy lenta)",  false, c => Velocidad(c, "gprs", "gprs")),

        new(11, "Bateria",   "Normal",            true,  c => Bateria(c, 100, true)),
        new(12, "Bateria",   "50% sin cargar",    false, c => Bateria(c, 50, false)),
        new(13, "Bateria",   "Baja 15%",          false, c => Bateria(c, 15, false)),
        new(14, "Bateria",   "Crítica 4%",        false, c => Bateria(c, 4, false)),
        new(15, "Bateria",   "Ahorro de batería", false, c => Bateria(c, 40, false, true)),

        new(16, "Tema",      "Claro",             true,  c => { c.Sh("cmd uimode night no"); return null; }),
        new(17, "Tema",      "Oscuro",            false, c => { c.Sh("cmd uimode night yes"); return null; }),

        new(18, "Texto",     "Normal",            true,  c => { c.Sh("settings put system font_scale 1.0"); return null; }),
        new(19, "Texto",     "Grande (130%)",     false, c => { c.Sh("settings put system font_scale 1.3"); return null; }),
        new(20, "Texto",     "Enorme (200%)",     false, c => { c.Sh("settings put system font_scale 2.0"); return null; }),

        new(21, "Pantalla",  "Vertical",          true,  c => Pantalla(c, 0)),
        new(22, "Pantalla",  "Horizontal",        false, c => Pantalla(c, 1)),

        new(23, "Ubicacion", "Fijar GPS...",      false, Gps),
    };

    public static string Titulo(Preset p) => $"{p.Cat}: {p.Txt}";

    // ------------------------------------------------------------ Estado guardado (por AVD)

    public Dictionary<string, string> Activas() => _s.Estado.LeerCondiciones(_s.Avd);

    /// <summary>"Normal" si no hay nada activo; si no, "Red: Modo avión | Tema: Oscuro".</summary>
    public string Texto()
    {
        var h = Activas();
        if (h.Count == 0) return "Normal";
        return string.Join(" | ", StateStore.CategoriasCondiciones.Where(h.ContainsKey).Select(c => $"{c}: {h[c]}"));
    }

    // ------------------------------------------------------------ Aplicar

    private void Aplicar(Preset p, CondCtx ctx, Dictionary<string, string> h)
    {
        _s.Ui.Escribir($"  {p.Cat}: {p.Txt}", Nivel.Tenue);
        var res = p.Cmd(ctx);
        if (p.Cat == "Ubicacion") { if (res is not null) h[p.Cat] = res; return; }
        if (p.Normal) h.Remove(p.Cat); else h[p.Cat] = p.Txt;
    }

    /// <summary>Aplica varias condiciones en el emulador que está corriendo y las recuerda.</summary>
    public bool Aplicar(IEnumerable<Preset> presets)
    {
        var serial = SerialListo();
        if (serial is null) return false;
        var ctx = new CondCtx(_s, serial);
        var h = Activas();
        foreach (var p in presets) Aplicar(p, ctx, h);
        _s.Estado.GuardarCondiciones(_s.Avd, h);
        _s.Ui.Escribir("Listo.", Nivel.Ok);
        return true;
    }

    /// <summary>Acepta números (3 13 17) y "0" para restablecer todo.</summary>
    public bool AplicarNumeros(IEnumerable<string> tokens)
    {
        var lista = new List<Preset>();
        var restablecer = false;
        foreach (var t in tokens)
        {
            if (t == "0") { restablecer = true; continue; }
            var p = Presets.FirstOrDefault(x => x.N.ToString() == t);
            if (p is null) _s.Ui.Escribir($"  Opción no válida: {t}", Nivel.Error);
            else lista.Add(p);
        }
        var ok = true;
        if (restablecer) ok = Restablecer();
        return lista.Count == 0 ? ok : Aplicar(lista) && ok;
    }

    private string? SerialListo()
    {
        if (!_s.AfirmarComp(Config.Componente.Movil) || _s.Adb is null)
        {
            _s.Ui.Escribir("No encontré el Android SDK (emulator y platform-tools).", Nivel.Error);
            return null;
        }
        var e = _s.Emulador.Estado();
        if (e.Codigo != CodigoEmu.Ok)
        {
            _s.Ui.Escribir("El emulador tiene que estar corriendo y respondiendo.", Nivel.Aviso);
            return null;
        }
        return e.Serial;
    }

    /// <summary>Vuelve a "Normal" lo que esté activo (o red/velocidad/batería si no hay nada registrado).</summary>
    public bool Restablecer(string? serial = null)
    {
        serial ??= SerialListo();
        if (serial is null) return false;
        var ctx = new CondCtx(_s, serial);
        var h = Activas();
        var cats = h.Keys.ToList();
        if (cats.Count == 0) cats = new List<string> { "Red", "Velocidad", "Bateria" };
        foreach (var cat in cats)
        {
            var p = Presets.FirstOrDefault(x => x.Cat == cat && x.Normal);
            if (p is not null) Aplicar(p, ctx, h); else h.Remove(cat);
        }
        _s.Estado.GuardarCondiciones(_s.Avd, h);
        return true;
    }

    /// <summary>Tras arrancar, la velocidad siempre empieza normal: se quita de lo guardado y se avisa si quedan otras activas.</summary>
    public void TrasArranque()
    {
        var h = Activas();
        if (h.Count == 0) return;
        h.Remove("Velocidad");
        _s.Estado.GuardarCondiciones(_s.Avd, h);
        if (h.Count > 0)
        {
            _s.Ui.Escribir($"Ojo, quedan condiciones activas de la sesión anterior: {Texto()}", Nivel.Aviso);
            _s.Ui.Escribir("Restablécelas desde el menú de condiciones.", Nivel.Aviso);
        }
    }
}
