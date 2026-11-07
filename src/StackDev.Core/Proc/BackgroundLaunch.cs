using System.Diagnostics;

namespace StackDev.Core.Proc;

/// <summary>
/// How to start a command in the background with its output going to a log file and its exit code to a marker file,
/// on each operating system. The command lines are built here so they can be tested without starting anything.
/// </summary>
public static class BackgroundLaunch
{
    /// <summary>
    /// Arguments for <c>cmd.exe</c>: /v:on so that !errorlevel! is evaluated at run time, chcp 65001 so the output stays in UTF-8.
    /// It is one raw string because cmd has its own quoting rules.
    /// </summary>
    public static string WindowsArguments(string command, string log, string mark) =>
        $"/v:on /d /s /c \"chcp 65001>nul & ({command}) >> \"{log}\" 2>&1 & echo !errorlevel!> \"{mark}\"\"";

    /// <summary>The script given to <c>sh -c</c>: appends to the log and writes the exit code to the marker file when it ends.</summary>
    public static string PosixScript(string command, string log, string mark) =>
        $"({command}) >> {Quote(log)} 2>&1; echo $? > {Quote(mark)}";

    /// <summary>Single-quoted word for a POSIX shell.</summary>
    internal static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public static ProcessStartInfo Info(string command, string folder, string log, string mark)
    {
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
            psi = new ProcessStartInfo("cmd.exe") { Arguments = WindowsArguments(command, log, mark) };
        else
        {
            psi = new ProcessStartInfo("/bin/sh");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(PosixScript(command, log, mark));
        }
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = folder;
        return psi;
    }

    /// <summary>Opens an address with the system's default browser (Windows shell, macOS <c>open</c> or <c>xdg-open</c>).</summary>
    public static void OpenWithSystem(string url)
    {
        if (OperatingSystem.IsWindows()) { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return; }
        var psi = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(url);
        Process.Start(psi);
    }
}
