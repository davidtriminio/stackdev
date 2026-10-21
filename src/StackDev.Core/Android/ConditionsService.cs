using System.Globalization;
using StackDev.Core.State;
using StackDev.Core.Ui;

namespace StackDev.Core.Android;

public sealed class CondCtx
{
    public CondCtx(Session s, string serial) { Session = s; Serial = serial; }
    public Session Session { get; }
    public string Serial { get; }
    public AdbClient Adb => Session.Adb!;
    public void Sh(string c) => Adb.Sh(Serial, c, 15);
    public void Emu(string c) => Adb.Emu(Serial, c, 10);
}

public sealed record Preset(int N, string Cat, string Txt, bool Normal, Func<CondCtx, string?> Cmd);

/// <summary>
/// Condiciones del dispositivo para probar la app (red, velocidad, batería, tema, texto, pantalla, GPS).
/// Se aplican en vivo con adb, varias a la vez, y se recuerdan por AVD para poder restablecerlas.
/// </summary>
public sealed class ConditionsService
{
    private readonly Session _s;

    public ConditionsService(Session s) { _s = s; }

    private static void AirplaneMode(CondCtx c, bool active)
    {
        var r = c.Adb.Sh(c.Serial, $"cmd connectivity airplane-mode {(active ? "enable" : "disable")}", 15) ?? "";
        if (System.Text.RegularExpressions.Regex.IsMatch(r, "Unknown|Error|Exception|not found", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            // Android 10 o anterior
            c.Sh($"settings put global airplane_mode_on {(active ? 1 : 0)}");
            c.Sh($"am broadcast -a android.intent.action.AIRPLANE_MODE --ez state {(active ? "true" : "false")}");
        }
    }

    private static string? Network(CondCtx c, bool wifi, bool data)
    {
        AirplaneMode(c, false);
        c.Sh($"svc wifi {(wifi ? "enable" : "disable")}");
        c.Sh($"svc data {(data ? "enable" : "disable")}");
        return null;
    }

    private static string? Speed(CondCtx c, string speed, string delay)
    {
        c.Emu($"network speed {speed}");
        c.Emu($"network delay {delay}");
        return null;
    }

    private static string? Battery(CondCtx c, int level, bool loading, bool saving = false)
    {
        if (loading) { c.Emu("power ac on"); c.Emu("power status charging"); }
        else { c.Emu("power ac off"); c.Emu("power status discharging"); }
        c.Emu($"power capacity {level}");
        var m = saving ? 1 : 0;
        c.Sh($"settings put global low_power {m}");
        c.Sh($"cmd power set-mode {m}");
        return null;
    }

    private static string? Screen(CondCtx c, int rotation)
    {
        c.Sh("settings put system accelerometer_rotation 0");
        c.Sh($"settings put system user_rotation {rotation}");
        return null;
    }

    /// <summary>Pide "latitud, longitud" y fija el GPS. <c>geo fix</c> pide la longitud primero, con cultura invariante.</summary>
    private static string? Gps(CondCtx c)
    {
        var t = c.Session.Ui.Ask("Latitud, longitud (ej: 14.0723, -87.1921; vacío = cancelar)")?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        var parts = t.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var inv = CultureInfo.InvariantCulture;
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, inv, out var lat) ||
            !double.TryParse(parts[1], NumberStyles.Float, inv, out var lon))
        {
            c.Session.Ui.Write("Formato no válido.", Level.Error);
            return null;
        }
        c.Emu($"geo fix {lon.ToString(inv)} {lat.ToString(inv)}");
        return $"{lat.ToString(inv)}, {lon.ToString(inv)}";
    }

    public static readonly IReadOnlyList<Preset> Presets = new List<Preset>
    {
        new(1,  "Red",       "Normal",            true,  c => Network(c, true, true)),
        new(2,  "Red",       "Modo avión",        false, c => { AirplaneMode(c, true); return null; }),
        new(3,  "Red",       "Sin internet",      false, c => Network(c, false, false)),
        new(4,  "Red",       "Solo datos",        false, c => Network(c, false, true)),
        new(5,  "Red",       "Solo Wi-Fi",        false, c => Network(c, true, false)),

        new(6,  "Velocidad", "Normal",            true,  c => Speed(c, "full", "none")),
        new(7,  "Velocidad", "4G",                false, c => Speed(c, "lte", "none")),
        new(8,  "Velocidad", "3G",                false, c => Speed(c, "umts", "umts")),
        new(9,  "Velocidad", "EDGE (lenta)",      false, c => Speed(c, "edge", "edge")),
        new(10, "Velocidad", "GPRS (muy lenta)",  false, c => Speed(c, "gprs", "gprs")),

        new(11, "Bateria",   "Normal",            true,  c => Battery(c, 100, true)),
        new(12, "Bateria",   "50% sin cargar",    false, c => Battery(c, 50, false)),
        new(13, "Bateria",   "Baja 15%",          false, c => Battery(c, 15, false)),
        new(14, "Bateria",   "Crítica 4%",        false, c => Battery(c, 4, false)),
        new(15, "Bateria",   "Ahorro de batería", false, c => Battery(c, 40, false, true)),

        new(16, "Tema",      "Claro",             true,  c => { c.Sh("cmd uimode night no"); return null; }),
        new(17, "Tema",      "Oscuro",            false, c => { c.Sh("cmd uimode night yes"); return null; }),

        new(18, "Texto",     "Normal",            true,  c => { c.Sh("settings put system font_scale 1.0"); return null; }),
        new(19, "Texto",     "Grande (130%)",     false, c => { c.Sh("settings put system font_scale 1.3"); return null; }),
        new(20, "Texto",     "Enorme (200%)",     false, c => { c.Sh("settings put system font_scale 2.0"); return null; }),

        new(21, "Pantalla",  "Vertical",          true,  c => Screen(c, 0)),
        new(22, "Pantalla",  "Horizontal",        false, c => Screen(c, 1)),

        new(23, "Ubicacion", "Fijar GPS...",      false, Gps),
    };

    public static string Title(Preset p) => $"{p.Cat}: {p.Txt}";

    // ------------------------------------------------------------ Estado guardado (por AVD)

    public Dictionary<string, string> Active() => _s.State.ReadConditions(_s.Avd);

    /// <summary>"Normal" si no hay nada activo; si no, "Red: Modo avión | Tema: Oscuro".</summary>
    public string Text()
    {
        var h = Active();
        if (h.Count == 0) return "Normal";
        return string.Join(" | ", StateStore.ConditionCategories.Where(h.ContainsKey).Select(c => $"{c}: {h[c]}"));
    }

    // ------------------------------------------------------------ Aplicar

    private void Apply(Preset p, CondCtx ctx, Dictionary<string, string> h)
    {
        _s.Ui.Write($"  {p.Cat}: {p.Txt}", Level.Muted);
        var res = p.Cmd(ctx);
        if (p.Cat == "Ubicacion") { if (res is not null) h[p.Cat] = res; return; }
        if (p.Normal) h.Remove(p.Cat); else h[p.Cat] = p.Txt;
    }

    /// <summary>Aplica varias condiciones en el emulador que está corriendo y las recuerda.</summary>
    public bool Apply(IEnumerable<Preset> presets)
    {
        var serial = SerialReady();
        if (serial is null) return false;
        var ctx = new CondCtx(_s, serial);
        var h = Active();
        foreach (var p in presets) Apply(p, ctx, h);
        _s.State.SaveConditions(_s.Avd, h);
        _s.Ui.Write("Listo.", Level.Ok);
        return true;
    }

    /// <summary>Acepta números (3 13 17) y "0" para restablecer todo.</summary>
    public bool ApplyNumbers(IEnumerable<string> tokens)
    {
        var list = new List<Preset>();
        var reset = false;
        foreach (var t in tokens)
        {
            if (t == "0") { reset = true; continue; }
            var p = Presets.FirstOrDefault(x => x.N.ToString() == t);
            if (p is null) _s.Ui.Write($"  Opción no válida: {t}", Level.Error);
            else list.Add(p);
        }
        var ok = true;
        if (reset) ok = Reset();
        return list.Count == 0 ? ok : Apply(list) && ok;
    }

    private string? SerialReady()
    {
        if (!_s.AssertComp(Config.ProjectComponent.Mobile) || _s.Adb is null)
        {
            _s.Ui.Write("No encontré el Android SDK (emulator y platform-tools).", Level.Error);
            return null;
        }
        var e = _s.Emulator.State();
        if (e.Code != EmuCode.Ok)
        {
            _s.Ui.Write("El emulador tiene que estar corriendo y respondiendo.", Level.Warning);
            return null;
        }
        return e.Serial;
    }

    /// <summary>Vuelve a "Normal" lo que esté activo (o red/velocidad/batería si no hay nada registrado).</summary>
    public bool Reset(string? serial = null)
    {
        serial ??= SerialReady();
        if (serial is null) return false;
        var ctx = new CondCtx(_s, serial);
        var h = Active();
        var cats = h.Keys.ToList();
        if (cats.Count == 0) cats = new List<string> { "Red", "Velocidad", "Bateria" };
        foreach (var cat in cats)
        {
            var p = Presets.FirstOrDefault(x => x.Cat == cat && x.Normal);
            if (p is not null) Apply(p, ctx, h); else h.Remove(cat);
        }
        _s.State.SaveConditions(_s.Avd, h);
        return true;
    }

    /// <summary>Tras arrancar, la velocidad siempre empieza normal: se quita de lo guardado y se avisa si quedan otras activas.</summary>
    public void AfterStartup()
    {
        var h = Active();
        if (h.Count == 0) return;
        h.Remove("Velocidad");
        _s.State.SaveConditions(_s.Avd, h);
        if (h.Count > 0)
        {
            _s.Ui.Write($"Ojo, quedan condiciones activas de la sesión anterior: {Text()}", Level.Warning);
            _s.Ui.Write("Restablécelas desde el menú de condiciones.", Level.Warning);
        }
    }
}
