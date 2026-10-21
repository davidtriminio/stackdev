using System.Diagnostics;
using StackDev.Core.Util;

namespace StackDev.Core.Proc;

public sealed record ProcResult(int Code, string Output, string Error, bool TimeExpired = false, bool Cancelled = false, string? ErrorHome = null)
{
    public bool Ok => Code == 0 && !TimeExpired && !Cancelled && ErrorHome is null;
    public string Text => string.IsNullOrWhiteSpace(Error) ? Output : Error;
}

/// <summary>
/// Procesos externos. Todo lleva tiempo límite (adb y docker se cuelgan cuando el emulador o Docker no responden)
/// y al cancelar o vencer el tiempo se cierra el árbol completo de procesos.
/// </summary>
public static class ProcessRunner
{
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

    /// <summary>Ejecuta y captura la salida. Nunca queda esperando más de <paramref name="timeoutSec"/>.</summary>
    public static ProcResult Run(string exe, IEnumerable<string> args, int timeoutSec = 10, string? cwd = null,
        IReadOnlyDictionary<string, string>? env = null, CancellationToken ct = default)
    {
        var psi = Psi(exe, args, cwd, env);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;   // se cierra enseguida: nada se queda esperando datos
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

    /// <summary>Como <see cref="Run"/> pero con stdin y stdout conectados a archivos, sin tocar un solo byte.</summary>
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
    /// Corre mostrando la salida en esta misma consola (tests, builds, flutter analyze...).
    /// Ctrl+C (el <paramref name="ct"/>) cancela solo este comando y devuelve 130.
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
    /// Como <see cref="RunInheriting"/> pero entrega cada línea de salida a <paramref name="line"/> (para la interfaz gráfica,
    /// que no tiene consola). Devuelve 130 si se cancela.
    /// </summary>
    public static int RunCapturing(string exe, IEnumerable<string> args, string? cwd, IReadOnlyDictionary<string, string>? env,
        CancellationToken ct, Action<string> line)
    {
        var psi = Psi(exe, args, cwd, env);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
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
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) line(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) line(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            while (!p.WaitForExit(150))
            {
                if (ct.IsCancellationRequested) { KillTree(p); p.WaitForExit(3000); return 130; }
            }
            p.WaitForExit();   // vacía los últimos eventos de salida
            return p.ExitCode;
        }
    }

    /// <summary>Abre el programa en una ventana de consola nueva (para lo interactivo: flutter run, psql...).</summary>
    public static Process? StartInWindow(string exe, string arguments, string? cwd)
    {
        var psi = new ProcessStartInfo(exe, arguments) { UseShellExecute = true };
        if (!string.IsNullOrEmpty(cwd)) psi.WorkingDirectory = cwd;
        try { return Process.Start(psi); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }
}
