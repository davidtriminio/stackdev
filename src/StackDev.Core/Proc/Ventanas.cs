using System.Diagnostics;
using System.Globalization;

namespace StackDev.Core.Proc;

/// <summary>
/// PIDs de los procesos que lanzó la herramienta (API, web, app...), guardados en el estado personal
/// junto con la hora de inicio para no confundirlos si el sistema reutiliza el PID.
/// Formato de cada línea (igual que flutter.ps1): <c>clave|pid|ticks</c>.
/// </summary>
public sealed class RegistroProcesos
{
    private readonly string _archivo;

    public RegistroProcesos(string archivo) { _archivo = archivo; }

    private Dictionary<string, (int Pid, long Ticks)> Leer()
    {
        var h = new Dictionary<string, (int, long)>();
        if (!File.Exists(_archivo)) return h;
        try
        {
            foreach (var l in File.ReadAllLines(_archivo))
            {
                var p = l.Trim().Split('|');
                if (p.Length == 3 && int.TryParse(p[1], out var pid) && long.TryParse(p[2], out var t)) h[p[0]] = (pid, t);
            }
        }
        catch (IOException) { }
        return h;
    }

    private void Escribir(Dictionary<string, (int Pid, long Ticks)> h)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_archivo)!);
        File.WriteAllLines(_archivo, h.Select(kv => $"{kv.Key}|{kv.Value.Pid}|{kv.Value.Ticks}"));
    }

    public void Guardar(string clave, int pid, long ticks)
    {
        var h = Leer();
        h[clave] = (pid, ticks);
        Escribir(h);
    }

    public void Guardar(string clave, Process p)
    {
        long ticks = 0;
        try { ticks = p.StartTime.Ticks; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        Guardar(clave, p.Id, ticks);
    }

    public IReadOnlyList<string> Claves() => Leer().Keys.ToList();

    public void Olvidar(string clave)
    {
        var h = Leer();
        if (h.Remove(clave)) Escribir(h);
    }

    /// <summary>El proceso de esa clave si sigue vivo y es el mismo (misma hora de inicio); si no, null.</summary>
    public Process? Viva(string clave)
    {
        var h = Leer();
        if (!h.TryGetValue(clave, out var e)) return null;
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
    public bool AdoptarDesdeArchivo(string clave, string archivoPid)
    {
        try
        {
            if (!File.Exists(archivoPid)) return false;
            var p = File.ReadAllText(archivoPid).Trim().Split('|');
            if (p.Length < 1 || !int.TryParse(p[0], out var pid)) return false;
            long ticks = p.Length > 1 && long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : 0;
            Guardar(clave, pid, ticks);
            return true;
        }
        catch (IOException) { return false; }
    }
}
