using System.Diagnostics;
using StackDev.Core.Proc;
using StackDev.Core.Ui;
using StackDev.Core.Util;

namespace StackDev.Core.Docker;

/// <summary>docker.exe con tiempo límite, y abrir Docker Desktop si el motor no responde.</summary>
public sealed class DockerService
{
    private readonly Session _s;

    public DockerService(Session s)
    {
        _s = s;
        Exe = Search();
    }

    public string? Exe { get; }
    public bool Installed => Exe is not null;

    private static string? Search()
    {
        var p = Shell.SearchInPath("docker");
        if (p is not null) return p;
        var pf = Environment.GetEnvironmentVariable("ProgramFiles");
        if (!string.IsNullOrEmpty(pf))
        {
            var f = Path.Combine(pf, "Docker", "Docker", "resources", "bin", "docker.exe");
            if (File.Exists(f)) return f;
        }
        return null;
    }

    /// <summary>Cómo se escribe docker dentro de un script de PowerShell.</summary>
    public string PsCommand => Shell.SearchInPath("docker") is not null ? "docker" : $"& {Args.PsLiteral(Exe ?? "docker")}";

    public ProcResult Run(IEnumerable<string> args, int timeoutSec = 15) =>
        Exe is null ? new ProcResult(-1, "", "", ErrorHome: "Docker no está instalado") : ProcessRunner.Run(Exe, args, timeoutSec, ct: _s.Ct);

    /// <summary>Corre mostrando su salida en esta consola (el progreso de la descarga de una imagen).</summary>
    public int RunVisible(IEnumerable<string> args) =>
        Exe is null ? 127 : _s.RunVisible(Exe, args, null, null);

    public ProcResult RunWithFiles(IEnumerable<string> args, string? input, string? output, int timeoutSec = 900) =>
        Exe is null ? new ProcResult(-1, "", "", ErrorHome: "Docker no está instalado")
                    : ProcessRunner.RunWithFiles(Exe, args, input, output, timeoutSec, _s.Ct);

    /// <summary>¿El motor de Docker responde? (<c>docker info</c> con 8 segundos de plazo)</summary>
    public bool Live() => Installed && Run(new[] { "info", "--format", "{{.ServerVersion}}" }, 8).Ok;

    /// <summary>Si el motor no responde, abre Docker Desktop y espera hasta 150 s (se cancela con C o Ctrl+C).</summary>
    public bool StartDesktop()
    {
        if (!Installed)
        {
            _s.Ui.Write("Docker no está instalado o no está en el PATH.", Level.Error);
            return false;
        }
        if (Live()) return true;

        var candidates = new[]
        {
            Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? "", "Docker", "Docker", "Docker Desktop.exe"),
            Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "Docker", "Docker Desktop.exe"),
        };
        var exe = candidates.FirstOrDefault(File.Exists);
        if (exe is not null)
        {
            _s.Ui.Write("Abriendo Docker Desktop...", Level.Info);
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        else _s.Ui.Write("Abre Docker Desktop manualmente.", Level.Warning);

        _s.Ui.WriteInline("Esperando a Docker (C o Ctrl+C para cancelar)");
        var limit = Environment.TickCount64 + 150_000;
        while (Environment.TickCount64 < limit)
        {
            if (Live()) { _s.Ui.Write(""); _s.Ui.Write("Docker listo.", Level.Ok); return true; }
            if (_s.Ct.IsCancellationRequested || ConsoleKeys.PressCancel()) break;
            _s.Ui.WriteInline(".");
            _s.Ct.WaitHandle.WaitOne(3000);
        }
        _s.Ui.Write("");
        _s.Ui.Write("Docker no respondió.", Level.Error);
        return false;
    }
}
