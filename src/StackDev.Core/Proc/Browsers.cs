using System.Diagnostics;
using StackDev.Core.Util;

namespace StackDev.Core.Proc;

public sealed record BrowserInfo(string Id, string Name, string Exe);

/// <summary>
/// Abrir direcciones con el navegador que elige el usuario (por proyecto o para todos) en lugar del predeterminado de Windows.
/// Un navegador se guarda como un id conocido (chrome, edge, firefox, brave, opera, vivaldi) o como la ruta de un .exe.
/// </summary>
public static class Browsers
{
    public const string SystemDefault = "";   // el predeterminado de Windows

    private static IEnumerable<(string Id, string Name, string[] Paths)> Known()
    {
        string E(string v) => Environment.GetEnvironmentVariable(v) ?? "";
        var pf = E("ProgramFiles"); var pf86 = E("ProgramFiles(x86)"); var local = E("LOCALAPPDATA");
        yield return ("chrome", "Google Chrome", new[]
        {
            Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"),
        });
        yield return ("edge", "Microsoft Edge", new[]
        {
            Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"),
        });
        yield return ("firefox", "Mozilla Firefox", new[]
        {
            Path.Combine(pf, "Mozilla Firefox", "firefox.exe"),
            Path.Combine(pf86, "Mozilla Firefox", "firefox.exe"),
        });
        yield return ("brave", "Brave", new[]
        {
            Path.Combine(pf, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
            Path.Combine(pf86, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
            Path.Combine(local, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
        });
        yield return ("opera", "Opera", new[]
        {
            Path.Combine(local, "Programs", "Opera", "opera.exe"),
            Path.Combine(pf, "Opera", "opera.exe"),
        });
        yield return ("vivaldi", "Vivaldi", new[]
        {
            Path.Combine(local, "Vivaldi", "Application", "vivaldi.exe"),
            Path.Combine(pf, "Vivaldi", "Application", "vivaldi.exe"),
        });
    }

    /// <summary>Los navegadores que están instalados en este equipo.</summary>
    public static List<BrowserInfo> Installed() =>
        Known().Select(c => (c.Id, c.Name, Exe: c.Paths.FirstOrDefault(File.Exists)))
                   .Where(c => c.Exe is not null)
                   .Select(c => new BrowserInfo(c.Id, c.Name, c.Exe!)).ToList();

    /// <summary>De un id o una ruta, el .exe a usar; null = predeterminado del sistema (o no se encontró).</summary>
    public static string? Resolve(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return null;
        var v = val.Trim();
        var known = Installed().FirstOrDefault(n => n.Id.Equals(v, StringComparison.OrdinalIgnoreCase));
        if (known is not null) return known.Exe;
        return File.Exists(v) ? v : null;
    }

    /// <summary>Nombre para mostrar de lo guardado ("" = predeterminado del sistema).</summary>
    public static string Name(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return "Predeterminado del sistema";
        var v = val.Trim();
        var known = Known().FirstOrDefault(n => n.Id.Equals(v, StringComparison.OrdinalIgnoreCase));
        return known.Name ?? Path.GetFileNameWithoutExtension(v);
    }

    /// <summary>Abre la dirección con el navegador elegido; si no se encuentra o falla, con el predeterminado. Devuelve un aviso si tuvo que volver al predeterminado.</summary>
    public static string? Open(string url, string? preferred)
    {
        string? warning = null;
        var exe = Resolve(preferred);
        if (exe is not null)
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe, Args.Quote(url)) { UseShellExecute = false });
                return null;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            { warning = $"No pude abrir {Name(preferred)}: {ex.Message}. Uso el predeterminado."; }
        }
        else if (!string.IsNullOrWhiteSpace(preferred))
            warning = $"No encuentro el navegador «{Name(preferred)}». Uso el predeterminado.";

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return warning;
    }
}
