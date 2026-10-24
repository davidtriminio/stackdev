using System.Diagnostics;
using System.Globalization;

namespace StackDev.Core.Proc;

/// <summary>
/// PIDs de los procesos que lanzó la herramienta (API, web, app...), guardados en el estado personal
/// junto con la hora de inicio para no confundirlos si el sistema reutiliza el PID.
/// Formato de cada línea (igual que the previous script): <c>clave|pid|ticks</c>.
/// </summary>
public sealed class ProcessRegistry
{
    private readonly string _file;

    public ProcessRegistry(string file) { _file = file; }

    private Dictionary<string, (int Pid, long Ticks)> Read()
    {
        var h = new Dictionary<string, (int, long)>();
        if (!File.Exists(_file)) return h;
        try
        {
            foreach (var l in File.ReadAllLines(_file))
            {
                var p = l.Trim().Split('|');
                if (p.Length == 3 && int.TryParse(p[1], out var pid) && long.TryParse(p[2], out var t)) h[p[0]] = (pid, t);
            }
        }
        catch (IOException) { }
        return h;
    }

    private void Write(Dictionary<string, (int Pid, long Ticks)> h)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllLines(_file, h.Select(kv => $"{kv.Key}|{kv.Value.Pid}|{kv.Value.Ticks}"));
    }

    public void Save(string key, int pid, long ticks)
    {
        var h = Read();
        h[key] = (pid, ticks);
        Write(h);
    }

    public void Save(string key, Process p)
    {
        long ticks = 0;
        try { ticks = p.StartTime.Ticks; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        Save(key, p.Id, ticks);
    }

    public IReadOnlyList<string> Keys() => Read().Keys.ToList();

    public void Forget(string key)
    {
        var h = Read();
        if (h.Remove(key)) Write(h);
    }

    /// <summary>El proceso de esa clave si sigue vivo y es el mismo (misma hora de inicio); si no, null.</summary>
    public Process? Live(string key)
    {
        var h = Read();
        if (!h.TryGetValue(key, out var e)) return null;
        Process p;
        try { p = Process.GetProcessById(e.Pid); }
        catch (ArgumentException) { return null; }
        try
        {
            if (p.HasExited) return null;
            if (e.Ticks != 0 && p.StartTime.Ticks != e.Ticks) return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return p;
    }

    /// <summary>Lee un PID escrito por un script de PowerShell en su propia ventana (<c>pid|ticks</c>) y lo registra.</summary>
    public bool AdoptFromFile(string key, string filePid)
    {
        try
        {
            if (!File.Exists(filePid)) return false;
            var p = File.ReadAllText(filePid).Trim().Split('|');
            if (p.Length < 1 || !int.TryParse(p[0], out var pid)) return false;
            long ticks = p.Length > 1 && long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : 0;
            Save(key, pid, ticks);
            return true;
        }
        catch (IOException) { return false; }
    }
}
