using System.Text;
using StackDev.Core.Util;

namespace StackDev.Core.Proc;

/// <summary>PowerShell and Windows Terminal: where they are and how to pass them a script without quoting problems.</summary>
public static class Shell
{
    private static string? _ps;
    private static string? _wt;
    private static bool _psSearched, _wtSearched;

    /// <summary>pwsh (PowerShell 7) if it is on the PATH; otherwise Windows PowerShell 5.1.</summary>
    public static string PowerShellExe
    {
        get
        {
            if (!_psSearched)
            {
                _psSearched = true;
                _ps = SearchInPath("pwsh");
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

    /// <summary>wt.exe (Windows Terminal), or null.</summary>
    public static string? WindowsTerminal
    {
        get
        {
            if (!_wtSearched) { _wtSearched = true; _wt = SearchInPath("wt"); }
            return _wt;
        }
    }

    public static string? SearchInPath(string name)
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
                    var f = Path.Combine(dir.Trim('"'), name + ext);
                    if (File.Exists(f)) return f;
                }
                catch (ArgumentException) { }
            }
        }
        return null;
    }

    /// <summary>PowerShell script in Base64 UTF-16 for <c>-EncodedCommand</c>.</summary>
    public static string Encode(IEnumerable<string> lines) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(string.Join("\n", lines)));

    public static IEnumerable<string> EnvLines(IReadOnlyDictionary<string, string>? env)
    {
        if (env is null) yield break;
        foreach (var (k, v) in env)
            yield return $"[Environment]::SetEnvironmentVariable({Args.PsLiteral(k)}, {Args.PsLiteral(v)}, 'Process')";
    }

    /// <summary>PowerShell arguments to run a script without a profile.</summary>
    public static List<string> ArgsScript(IEnumerable<string> lines, bool noExit = false, bool textFlat = false)
    {
        var a = new List<string> { "-NoLogo", "-NoProfile" };
        if (noExit) a.Add("-NoExit");
        if (textFlat) { a.Add("-OutputFormat"); a.Add("Text"); }
        a.Add("-EncodedCommand");
        a.Add(Encode(lines));
        return a;
    }
}
