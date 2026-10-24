using StackDev.Core.Proc;

namespace StackDev.Core.Android;

public sealed record AdbDevice(string Serial, string State, string Description = "")
{
    public bool IsEmulator => Serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase);
    /// <summary>Conectado por Wi-Fi: «ip:puerto» o el nombre mDNS de la depuración inalámbrica.</summary>
    public bool IsNetwork => !IsEmulator && (Serial.Contains(':') || Serial.Contains("_adb-tls-"));
    public bool Ready => State == "device";
}

/// <summary>adb.exe con tiempo límite. Devuelve null cuando se cuelga o no se pudo ejecutar (el emulador no responde).</summary>
public sealed class AdbClient
{
    private readonly string _exe;
    private readonly IReadOnlyDictionary<string, string> _env;

    public AdbClient(string exe, IReadOnlyDictionary<string, string> env)
    {
        _exe = exe;
        _env = env;
    }

    public string? Run(IEnumerable<string> args, int timeoutSec = 10, CancellationToken ct = default)
    {
        var r = ProcessRunner.Run(_exe, args, timeoutSec, env: _env, ct: ct);
        if (r.TimeExpired || r.Cancelled || r.Code == -1) return null;
        return r.Output;
    }

    public void StartServer() => Run(new[] { "start-server" }, 20);

    public void RestartServer()
    {
        Run(new[] { "kill-server" }, 10);
        Run(new[] { "start-server" }, 20);
    }

    public List<AdbDevice> Emulators()
    {
        var list = new List<AdbDevice>();
        var output = Run(new[] { "devices" }, 10);
        if (output is null) return list;
        foreach (var l in output.Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(l.Trim(), @"^(emulator-\d+)\s+(\S+)");
            if (m.Success) list.Add(new AdbDevice(m.Groups[1].Value, m.Groups[2].Value));
        }
        return list;
    }

    /// <summary>Todos los dispositivos que ve adb (emuladores, teléfonos por USB y por red), con su modelo.</summary>
    public List<AdbDevice> Devices()
    {
        var list = new List<AdbDevice>();
        var output = Run(new[] { "devices", "-l" }, 10);
        if (output is null) return list;
        foreach (var l in output.Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(l.Trim(), @"^(\S+)\s+(device|offline|unauthorized|authorizing|connecting|recovery|sideload|no permissions\S*)\s*(.*)$");
            if (!m.Success) continue;
            var model = System.Text.RegularExpressions.Regex.Match(m.Groups[3].Value, @"model:(\S+)");
            list.Add(new AdbDevice(m.Groups[1].Value, m.Groups[2].Value, model.Success ? model.Groups[1].Value.Replace('_', ' ') : ""));
        }
        return list;
    }

    /// <summary><c>adb connect ip:puerto</c>. Devuelve si quedó conectado y el texto de adb.</summary>
    public (bool Ok, string Text) Connect(string address)
    {
        var r = Run(new[] { "connect", address }, 20)?.Trim() ?? "adb no contestó (tiempo agotado).";
        var ok = r.Contains("connected to", StringComparison.OrdinalIgnoreCase) && !r.Contains("failed", StringComparison.OrdinalIgnoreCase)
                 && !r.Contains("cannot", StringComparison.OrdinalIgnoreCase);
        return (ok, r);
    }

    /// <summary><c>adb pair ip:puerto código</c> (depuración inalámbrica de Android 11 o superior).</summary>
    public (bool Ok, string Text) Pair(string address, string code)
    {
        var r = Run(new[] { "pair", address, code }, 30)?.Trim() ?? "adb no contestó (tiempo agotado).";
        return (r.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase), r);
    }

    /// <summary>Sin dirección desconecta todos los dispositivos de red.</summary>
    public string Disconnect(string? address = null) =>
        Run(address is null ? new[] { "disconnect" } : new[] { "disconnect", address }, 10)?.Trim() ?? "adb no contestó.";

    /// <summary>Pasa un teléfono conectado por USB a modo red (<c>adb tcpip</c>) y devuelve su IP si la encuentra.</summary>
    public string? EnableTcpip(string serial, int port = 5555)
    {
        var ip = Sh(serial, "ip -f inet addr show wlan0", 10);
        var m = ip is null ? null : System.Text.RegularExpressions.Regex.Match(ip, @"inet (\d+\.\d+\.\d+\.\d+)");
        Run(new[] { "-s", serial, "tcpip", port.ToString() }, 15);
        return m is { Success: true } ? m.Groups[1].Value : null;
    }

    /// <summary><c>adb reverse tcp:P tcp:P</c>: el «localhost:P» del dispositivo llega al «localhost:P» de este equipo.</summary>
    public (bool Ok, string Text) Forward(string serial, int port)
    {
        var r = Run(new[] { "-s", serial, "reverse", $"tcp:{port}", $"tcp:{port}" }, 10);
        return (r is not null && !r.Contains("error", StringComparison.OrdinalIgnoreCase), r?.Trim() ?? "adb no contestó.");
    }

    public string? Echo(string serial) => Run(new[] { "-s", serial, "shell", "echo", "ok" }, 5);

    public bool FullStartup(string serial) =>
        Run(new[] { "-s", serial, "shell", "getprop", "sys.boot_completed" }, 5)?.Trim() == "1";

    public string AvdName(string serial)
    {
        var r = Run(new[] { "-s", serial, "emu", "avd", "name" }, 5);
        var n = r?.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0 && x != "OK");
        return n ?? "?";
    }

    /// <summary>Un comando de shell del dispositivo, como un único argumento (igual que el script).</summary>
    public string? Sh(string serial, string command, int timeoutSec = 15) =>
        Run(new[] { "-s", serial, "shell", command }, timeoutSec);

    /// <summary>Un comando de la consola del emulador (<c>adb emu ...</c>).</summary>
    public string? Emu(string serial, string command, int timeoutSec = 10) =>
        Run(new[] { "-s", serial, "emu", command }, timeoutSec);
}
