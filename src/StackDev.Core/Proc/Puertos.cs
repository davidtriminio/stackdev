using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace StackDev.Core.Proc;

/// <summary>Puertos TCP en escucha y qué proceso los tiene.</summary>
public static class Puertos
{
    public static bool EstaEscuchando(int puerto)
    {
        if (puerto <= 0) return false;
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == puerto);
        }
        catch (NetworkInformationException) { return Pids(puerto).Count > 0; }
    }

    /// <summary>PIDs que escuchan en el puerto: GetExtendedTcpTable (Windows) o, de respaldo, <c>netstat -ano</c>.</summary>
    public static List<int> Pids(int puerto)
    {
        if (puerto <= 0) return new();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var lista = TablaTcp(2, 24).Concat(TablaTcp(23, 56)).Where(x => x.Puerto == puerto && x.Pid > 0)
                    .Select(x => x.Pid).Distinct().ToList();
                return lista;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException) { }
        }
        return PidsNetstat(puerto);
    }

    private static List<int> PidsNetstat(int puerto)
    {
        var r = ProcessRunner.Run("netstat.exe", new[] { "-ano" }, 10);
        var res = new List<int>();
        foreach (var l in r.Salida.Split('\n'))
        {
            var c = l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (c.Length >= 5 && c[0] == "TCP" && c[1].EndsWith($":{puerto}") && c[2].EndsWith(":0") && int.TryParse(c[4], out var pid))
                res.Add(pid);
        }
        return res.Distinct().ToList();
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr tabla, ref int tamano, bool ordenar, int familia, int clase, uint reservado);

    private const int TcpTableOwnerPidListener = 3;

    private static List<(int Puerto, int Pid)> TablaTcp(int familia, int tamanoFila)
    {
        var res = new List<(int, int)>();
        var tamano = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref tamano, false, familia, TcpTableOwnerPidListener, 0);
        if (tamano <= 0) return res;
        var buf = Marshal.AllocHGlobal(tamano);
        try
        {
            if (GetExtendedTcpTable(buf, ref tamano, false, familia, TcpTableOwnerPidListener, 0) != 0) return res;
            var n = Marshal.ReadInt32(buf);
            var fila = IntPtr.Add(buf, 4);
            for (var i = 0; i < n; i++, fila = IntPtr.Add(fila, tamanoFila))
            {
                // IPv4: estado, dirLocal, puertoLocal, dirRemota, puertoRemoto, pid.  IPv6: dirLocal[16], scope, puertoLocal, ...
                var offPuerto = familia == 2 ? 8 : 20;
                var offPid = familia == 2 ? 20 : 52;
                var p = Marshal.ReadInt32(fila, offPuerto);
                var puerto = ((p & 0xFF) << 8) | ((p >> 8) & 0xFF);
                res.Add((puerto, Marshal.ReadInt32(fila, offPid)));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return res;
    }
}
