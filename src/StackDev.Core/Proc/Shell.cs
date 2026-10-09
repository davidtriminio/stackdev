using System.Text;
using StackDev.Core.Util;

namespace StackDev.Core.Proc;

/// <summary>PowerShell y Windows Terminal: dónde están y cómo se les pasa un script sin problemas de comillas.</summary>
public static class Shell
{
    private static string? _ps;
    private static string? _wt;
    private static bool _psBuscado, _wtBuscado;

    /// <summary>pwsh (PowerShell 7) si está en el PATH; si no, Windows PowerShell 5.1.</summary>
    public static string PowerShellExe
    {
        get
        {
            if (!_psBuscado)
            {
                _psBuscado = true;
                _ps = BuscarEnPath("pwsh");
                if (_ps is null && OperatingSystem.IsWindows())
                {
                    var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    var w = Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe");
                    _ps = File.Exists(w) ? w : "powershell.exe";
                }
            }
            return _ps ?? "pwsh";
        }
    }

    /// <summary>wt.exe (Windows Terminal), o null.</summary>
    public static string? WindowsTerminal
    {
        get
        {
            if (!_wtBuscado) { _wtBuscado = true; _wt = BuscarEnPath("wt"); }
            return _wt;
        }
    }

    public static string? BuscarEnPath(string nombre)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : new[] { "" };
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                try
                {
                    var f = Path.Combine(dir.Trim('"'), nombre + ext);
                    if (File.Exists(f)) return f;
                }
                catch (ArgumentException) { }
            }
        }
        return null;
    }

    /// <summary>Script de PowerShell en Base64 UTF-16 para <c>-EncodedCommand</c>.</summary>
    public static string Codificar(IEnumerable<string> lineas) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(string.Join("\n", lineas)));

    public static IEnumerable<string> LineasEnv(IReadOnlyDictionary<string, string>? env)
    {
        if (env is null) yield break;
        foreach (var (k, v) in env)
            yield return $"[Environment]::SetEnvironmentVariable({Args.PsLiteral(k)}, {Args.PsLiteral(v)}, 'Process')";
    }

    /// <summary>Argumentos de PowerShell para correr un script sin perfil.</summary>
    public static List<string> ArgsScript(IEnumerable<string> lineas, bool noSalir = false, bool textoPlano = false)
    {
        var a = new List<string> { "-NoLogo", "-NoProfile" };
        if (noSalir) a.Add("-NoExit");
        if (textoPlano) { a.Add("-OutputFormat"); a.Add("Text"); }
        a.Add("-EncodedCommand");
        a.Add(Codificar(lineas));
        return a;
    }
}
