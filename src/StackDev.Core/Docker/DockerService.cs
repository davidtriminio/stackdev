using System.Diagnostics;
using StackDev.Core.Proc;
using StackDev.Core.Ui;
using StackDev.Core.Util;

namespace StackDev.Core.Docker;

/// <summary>docker.exe con tiempo límite, y abrir Docker Desktop si el motor no responde.</summary>
public sealed class DockerService
{
    private readonly Sesion _s;

    public DockerService(Sesion s)
    {
        _s = s;
        Exe = Buscar();
    }

    public string? Exe { get; }
    public bool Instalado => Exe is not null;

    private static string? Buscar()
    {
        var p = Shell.BuscarEnPath("docker");
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
    public string ComandoPs => Shell.BuscarEnPath("docker") is not null ? "docker" : $"& {Args.PsLiteral(Exe ?? "docker")}";

    public ProcResult Run(IEnumerable<string> args, int timeoutSeg = 15) =>
        Exe is null ? new ProcResult(-1, "", "", ErrorInicio: "Docker no está instalado") : ProcessRunner.Run(Exe, args, timeoutSeg, ct: _s.Ct);

    /// <summary>Corre mostrando su salida en esta consola (el progreso de la descarga de una imagen).</summary>
    public int RunVisible(IEnumerable<string> args) =>
        Exe is null ? 127 : _s.CorrerVisible(Exe, args, null, null);

    public ProcResult RunConArchivos(IEnumerable<string> args, string? entrada, string? salida, int timeoutSeg = 900) =>
        Exe is null ? new ProcResult(-1, "", "", ErrorInicio: "Docker no está instalado")
                    : ProcessRunner.RunConArchivos(Exe, args, entrada, salida, timeoutSeg, _s.Ct);

    /// <summary>¿El motor de Docker responde? (<c>docker info</c> con 8 segundos de plazo)</summary>
    public bool Vivo() => Instalado && Run(new[] { "info", "--format", "{{.ServerVersion}}" }, 8).Ok;

    /// <summary>Si el motor no responde, abre Docker Desktop y espera hasta 150 s (se cancela con C o Ctrl+C).</summary>
    public bool IniciarDesktop()
    {
        if (!Instalado)
        {
            _s.Ui.Escribir("Docker no está instalado o no está en el PATH.", Nivel.Error);
            return false;
        }
        if (Vivo()) return true;

        var candidatos = new[]
        {
            Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? "", "Docker", "Docker", "Docker Desktop.exe"),
            Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "Docker", "Docker Desktop.exe"),
        };
        var exe = candidatos.FirstOrDefault(File.Exists);
        if (exe is not null)
        {
            _s.Ui.Escribir("Abriendo Docker Desktop...", Nivel.Info);
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        else _s.Ui.Escribir("Abre Docker Desktop manualmente.", Nivel.Aviso);

        _s.Ui.EscribirInline("Esperando a Docker (C o Ctrl+C para cancelar)");
        var limite = Environment.TickCount64 + 150_000;
        while (Environment.TickCount64 < limite)
        {
            if (Vivo()) { _s.Ui.Escribir(""); _s.Ui.Escribir("Docker listo.", Nivel.Ok); return true; }
            if (_s.Ct.IsCancellationRequested || Teclado.PulsoCancelar()) break;
            _s.Ui.EscribirInline(".");
            _s.Ct.WaitHandle.WaitOne(3000);
        }
        _s.Ui.Escribir("");
        _s.Ui.Escribir("Docker no respondió.", Nivel.Error);
        return false;
    }
}
