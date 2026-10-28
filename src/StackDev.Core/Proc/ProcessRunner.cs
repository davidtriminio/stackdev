using System.Diagnostics;
using StackDev.Core.Util;

namespace StackDev.Core.Proc;

public sealed record ProcResult(int Code, string Output, string Error, bool TimeExpired = false, bool Cancelled = false, string? ErrorHome = null)
{
    public bool Ok => Code == 0 && !TimeExpired && !Cancelled && ErrorHome is null;
    public string Text => string.IsNullOrWhiteSpace(Error) ? Output : Error;
}

/// <summary>
/// External processes. Everything has a time limit (adb and docker hang when the emulator or Docker do not answer)
/// and on cancel or timeout the whole process tree is closed.
/// </summary>
public static class ProcessRunner
{
    /// <summary>Exit code <see cref="RunCapturing"/> returns when it had to stop a process that ran too long or went silent (same value as timeout(1)).</summary>
    public const int TimedOut = 124;

    private static ProcessStartInfo Psi(string exe, IEnumerable<string> args, string? cwd, IReadOnlyDictionary<string, string>? env)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        if (!string.IsNullOrEmpty(cwd)) psi.WorkingDirectory = cwd;
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        return psi;
    }

    public static void KillTree(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    public static void KillTree(int pid)
    {
        try { using var p = Process.GetProcessById(pid); KillTree(p); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
    }

    /// <summary>Runs and captures the output. Never waits longer than <paramref name="timeoutSec"/>.</summary>
    public static ProcResult Run(string exe, IEnumerable<string> args, int timeoutSec = 10, string? cwd = null,
        IReadOnlyDictionary<string, string>? env = null, CancellationToken ct = default)
    {
        var psi = Psi(exe, args, cwd, env);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;   // closed right away: nothing is left waiting for data
        Process p;
        try { p = Process.Start(psi)!; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        { return new ProcResult(-1, "", "", ErrorHome: ex.Message); }

        using (p)
        {
            try { p.StandardInput.Close(); } catch (IOException) { }
            var os = p.StandardOutput.ReadToEndAsync();
            var self = p.StandardError.ReadToEndAsync();
            var limit = Environment.TickCount64 + timeoutSec * 1000L;
            while (!p.WaitForExit(150))
            {
                if (ct.IsCancellationRequested) { KillTree(p); return new ProcResult(-1, "", "", Cancelled: true); }
                if (Environment.TickCount64 > limit) { KillTree(p); return new ProcResult(-1, "", "", TimeExpired: true); }
            }
            p.WaitForExit();
            var output = os.Wait(2000) ? os.Result : "";
            var error = self.Wait(2000) ? self.Result : "";
            return new ProcResult(p.ExitCode, output, error);
        }
    }

    /// <summary>Like <see cref="Run"/> but with stdin and stdout connected to files, without touching a single byte.</summary>
    public static ProcResult RunWithFiles(string exe, IEnumerable<string> args, string? input, string? output,
        int timeoutSec = 900, CancellationToken ct = default, IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = Psi(exe, args, null, env);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = input is not null;
        Process p;
        try { p = Process.Start(psi)!; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        { return new ProcResult(-1, "", "", ErrorHome: ex.Message); }

        FileStream? fs = null;
        try
        {
            using (p)
            {
                var errT = p.StandardError.ReadToEndAsync();
                Task? copy = null;
                Task<string>? outT = null;
                if (output is not null)
                {
                    fs = File.Create(output);
                    copy = p.StandardOutput.BaseStream.CopyToAsync(fs);
                }
                else outT = p.StandardOutput.ReadToEndAsync();

                if (input is not null)
                {
                    try
                    {
                        using var inp = File.OpenRead(input);
                        inp.CopyTo(p.StandardInput.BaseStream);
                        p.StandardInput.Close();
                    }
                    catch (IOException) { /* el proceso cerró stdin antes de tiempo: se verá en el código de salida */ }
                }

                var limit = Environment.TickCount64 + timeoutSec * 1000L;
                while (!p.WaitForExit(200))
                {
                    if (ct.IsCancellationRequested) { KillTree(p); return new ProcResult(-1, "", "Cancelado", Cancelled: true); }
                    if (Environment.TickCount64 > limit) { KillTree(p); return new ProcResult(-1, "", "Tiempo agotado", TimeExpired: true); }
                }
                p.WaitForExit();
                copy?.Wait(5000);
                var text = outT is not null && outT.Wait(2000) ? outT.Result : "";
                var error = errT.Wait(5000) ? errT.Result : "";
                return new ProcResult(p.ExitCode, text, error);
            }
        }
        finally { fs?.Dispose(); }
    }

    /// <summary>
    /// Runs showing the output in this same console (tests, builds, flutter analyze...).
    /// Ctrl+C (the <paramref name="ct"/>) cancels only this command and returns 130.
    /// </summary>
    public static int RunInheriting(string exe, IEnumerable<string> args, string? cwd, IReadOnlyDictionary<string, string>? env, CancellationToken ct)
    {
        var psi = Psi(exe, args, cwd, env);
        psi.CreateNoWindow = false;
        Process p;
        try { p = Process.Start(psi)!; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Console.Error.WriteLine($"No se pudo iniciar {exe}: {ex.Message}");
            return 127;
        }
        using (p)
        {
            while (!p.WaitForExit(150))
            {
                if (ct.IsCancellationRequested) { KillTree(p); p.WaitForExit(3000); return 130; }
            }
            return p.ExitCode;
        }
    }

    /// <summary>
    /// Like <see cref="RunInheriting"/> but hands each output line to <paramref name="line"/> (for the graphical interface,
    /// which has no console). Returns 130 if cancelled.
    /// </summary>
    public static int RunCapturing(string exe, IEnumerable<string> args, string? cwd, IReadOnlyDictionary<string, string>? env,
        CancellationToken ct, Action<string> line, int timeoutSec = 0, int idleSec = 0, bool closeStdin = false)
    {
        var psi = Psi(exe, args, cwd, env);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = closeStdin;   // closed right away: a child (ssh) never waits for a keyboard that does not exist
        psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
        psi.StandardErrorEncoding = System.Text.Encoding.UTF8;
        Process p;
        try { p = Process.Start(psi)!; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            line($"No se pudo iniciar {exe}: {ex.Message}");
            return 127;
        }
        using (p)
        {
            var lastOutput = Environment.TickCount64;
            p.OutputDataReceived += (_, e) => { if (e.Data is null) return; Volatile.Write(ref lastOutput, Environment.TickCount64); line(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is null) return; Volatile.Write(ref lastOutput, Environment.TickCount64); line(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (closeStdin) { try { p.StandardInput.Close(); } catch (IOException) { } }
            var started = Environment.TickCount64;
            while (!p.WaitForExit(150))
            {
                if (ct.IsCancellationRequested) { KillTree(p); p.WaitForExit(3000); return 130; }
                var now = Environment.TickCount64;
                if ((timeoutSec > 0 && now - started > timeoutSec * 1000L) || (idleSec > 0 && now - Volatile.Read(ref lastOutput) > idleSec * 1000L))
                {
                    KillTree(p); p.WaitForExit(3000);
                    return TimedOut;
                }
            }
            // WaitForExit(int) does not flush the asynchronous output events; the parameterless one does, but it waits for the pipes to close,
            // which a grandchild that kept them open would never allow. So: flush on another thread and give it a limit.
            if (!Task.Run(() => p.WaitForExit()).Wait(10000)) { KillTree(p); return TimedOut; }
            return p.ExitCode;
        }
    }

    /// <summary>Opens the program in a new console window (for interactive things: flutter run, psql...).</summary>
    public static Process? StartInWindow(string exe, string arguments, string? cwd)
    {
        var psi = new ProcessStartInfo(exe, arguments) { UseShellExecute = true };
        if (!string.IsNullOrEmpty(cwd)) psi.WorkingDirectory = cwd;
        try { return Process.Start(psi); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }
}
