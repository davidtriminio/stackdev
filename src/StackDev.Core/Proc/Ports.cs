using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace StackDev.Core.Proc;

/// <summary>Listening TCP ports and which process holds them.</summary>
public static class Ports
{
    public static bool IsListening(int port)
    {
        if (port <= 0) return false;
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);
        }
        catch (NetworkInformationException) { return Pids(port).Count > 0; }
    }

    /// <summary>PIDs listening on the port: GetExtendedTcpTable (Windows) or, as a fallback, <c>netstat -ano</c>.</summary>
    public static List<int> Pids(int port)
    {
        if (port <= 0) return new();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var list = TcpTable(2, 24).Concat(TcpTable(23, 56)).Where(x => x.Port == port && x.Pid > 0)
                    .Select(x => x.Pid).Distinct().ToList();
                return list;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException) { }
        }
        return PidsNetstat(port);
    }

    private static List<int> PidsNetstat(int port)
    {
        var r = ProcessRunner.Run("netstat.exe", new[] { "-ano" }, 10);
        var res = new List<int>();
        foreach (var l in r.Output.Split('\n'))
        {
            var c = l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (c.Length >= 5 && c[0] == "TCP" && c[1].EndsWith($":{port}") && c[2].EndsWith(":0") && int.TryParse(c[4], out var pid))
                res.Add(pid);
        }
        return res.Distinct().ToList();
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int kind, uint reserved);

    private const int TcpTableOwnerPidListener = 3;

    private static List<(int Port, int Pid)> TcpTable(int family, int sizeRow)
    {
        var res = new List<(int, int)>();
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidListener, 0);
        if (size <= 0) return res;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, family, TcpTableOwnerPidListener, 0) != 0) return res;
            var n = Marshal.ReadInt32(buf);
            var row = IntPtr.Add(buf, 4);
            for (var i = 0; i < n; i++, row = IntPtr.Add(row, sizeRow))
            {
                // IPv4: estado, dirLocal, puertoLocal, dirRemota, puertoRemoto, pid.  IPv6: dirLocal[16], scope, puertoLocal, ...
                var offPort = family == 2 ? 8 : 20;
                var offPid = family == 2 ? 20 : 52;
                var p = Marshal.ReadInt32(row, offPort);
                var port = ((p & 0xFF) << 8) | ((p >> 8) & 0xFF);
                res.Add((port, Marshal.ReadInt32(row, offPid)));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return res;
    }
}
