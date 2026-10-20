using System.Diagnostics;
using StackDev.Core.Config;
using StackDev.Core.Proc;
using StackDev.Core.Ui;
using StackDev.Core.Util;

namespace StackDev.Core.Servicios;

/// <summary>Correr comandos del proyecto: en esta consola (con tiempo y código de salida) o en otra ventana.</summary>
public sealed class EjecutorComandos
{
    private readonly Sesion _s;

    public EjecutorComandos(Sesion s) { _s = s; }

    /// <summary>
    /// Ejecuta en esta misma consola mostrando la salida, el tiempo y el código. Corre en un PowerShell hijo
    /// (conserva los colores y un error no rompe la herramienta). Ctrl+C cancela solo este comando.
    /// </summary>
    public int Aqui(string comando, string? carpeta = null, IReadOnlyDictionary<string, string>? env = null)
    {
        carpeta ??= _s.Raiz;
        if (!Directory.Exists(carpeta))
        {
            _s.Ui.Escribir($"No existe la carpeta: {carpeta}", Nivel.Error);
            return 1;
        }
        var entorno = Mezclar(_s.EnvPara(carpeta), env);
        _s.Ui.Escribir($"\n[{_s.Rel(carpeta)}] > {comando}", Nivel.Info);
        var inicio = DateTime.Now;
        var lineas = new[] { comando, "$__ok = $?; if ($LASTEXITCODE) { exit $LASTEXITCODE }; if (-not $__ok) { exit 1 }" };
        var codigo = _s.CorrerVisible(Shell.PowerShellExe, Shell.ArgsScript(lineas, textoPlano: true), carpeta, entorno);
        var seg = (int)(DateTime.Now - inicio).TotalSeconds;
        if (codigo == 130) _s.Ui.Escribir($"Cancelado ({seg}s).", Nivel.Aviso);
        else if (codigo != 0) _s.Ui.Escribir($"Terminó con código {codigo} ({seg}s).", Nivel.Error);
        else _s.Ui.Escribir($"Terminado ({seg}s).", Nivel.Ok);
        return codigo;
    }

    /// <summary>Un comando de la configuración (api.test, web.build, movil.analizar...). Devuelve su código de salida.</summary>
    public int Paso(Componente c, string clave)
    {
        if (!_s.AfirmarComp(c)) return 1;
        var cmd = _s.Comando(c, clave);
        if (cmd is null)
        {
            _s.Ui.Escribir($"No hay comando '{clave}' para {Sesion.Nombres[c]} en la configuración.", Nivel.Aviso);
            return 1;
        }
        if (c == Componente.Web && clave != "instalar" && !_s.Servicios.AsegurarNodeModules()) return 1;
        return Aqui(cmd, _s.Ctx.Carpeta(c));
    }

    private static IReadOnlyDictionary<string, string>? Mezclar(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b)
    {
        if (b is null || b.Count == 0) return a;
        if (a is null) return b;
        var r = new Dictionary<string, string>(a);
        foreach (var (k, v) in b) r[k] = v;
        return r;
    }

    // ------------------------------------------------------------ Ventanas / pestañas

    private string ArchivoPid(string clave) => Path.Combine(Path.GetTempPath(), $"stackdev-{_s.Clave}-{clave}.pid");

    /// <summary>"auto": Windows Terminal si está instalado, si no una ventana de consola nueva.</summary>
    public bool UsarWindowsTerminal()
    {
        var t = (_s.Ctx.Config.Terminal ?? "auto").ToLowerInvariant();
        if (t == "ventana") return false;
        return Shell.WindowsTerminal is not null;
    }

    /// <summary>
    /// Abre un comando en su propia ventana o pestaña (para lo interactivo: flutter run con r/R/q, psql, logs).
    /// El script guarda su PID, así se puede cerrar el árbol completo después. Devuelve false si no se pudo abrir.
    /// </summary>
    public bool EnVentana(string clave, string titulo, string carpeta, string comando, IReadOnlyDictionary<string, string>? env = null,
        bool esperarEnter = true)
    {
        var pidFile = ArchivoPid(clave);
        try { File.Delete(pidFile); } catch (IOException) { }
        _s.Servicios.Registro.Olvidar(clave);

        var entorno = Mezclar(_s.EnvPara(carpeta), env);
        var tituloLimpio = titulo.Replace(';', ',').Replace('"', '\'');
        var lineas = new List<string>
        {
            $"$host.UI.RawUI.WindowTitle = {Args.PsLiteral(tituloLimpio)}",
            $"Set-Location -LiteralPath {Args.PsLiteral(carpeta)}",
            $"Set-Content -LiteralPath {Args.PsLiteral(pidFile)} -Value ('' + $PID + '|' + (Get-Process -Id $PID).StartTime.Ticks)",
        };
        lineas.AddRange(Shell.LineasEnv(entorno));
        lineas.Add($"Write-Host {Args.PsLiteral("> " + comando)} -ForegroundColor Cyan");
        lineas.Add(comando);
        lineas.Add("$__ok = $?; $__c = if ($LASTEXITCODE) { $LASTEXITCODE } elseif ($__ok) { 0 } else { 1 }");
        if (esperarEnter)
            lineas.Add("Write-Host ''; Write-Host ('Terminó (código ' + $__c + '). Enter para cerrar esta ventana.') -ForegroundColor Yellow; [void](Read-Host)");

        var ps = Shell.PowerShellExe;
        var psArgs = Shell.ArgsScript(lineas);
        bool abierto;
        if (UsarWindowsTerminal())
        {
            var args = $"-w 0 new-tab --title {Args.Quote(tituloLimpio)} -d {Args.Quote(carpeta)} {Args.Quote(ps)} {Args.Join(psArgs)}";
            abierto = IniciarShell(Shell.WindowsTerminal!, args);
        }
        else abierto = ProcessRunner.IniciarEnVentana(ps, Args.Join(psArgs), carpeta) is not null;

        if (!abierto) return false;
        var limite = Environment.TickCount64 + 8000;
        while (Environment.TickCount64 < limite)
        {
            if (_s.Servicios.Registro.AdoptarDesdeArchivo(clave, pidFile)) return true;
            Thread.Sleep(150);
        }
        return true;   // se abrió, aunque no pudimos leer su PID
    }

    private static bool IniciarShell(string exe, string argumentos)
    {
        try { Process.Start(new ProcessStartInfo(exe, argumentos) { UseShellExecute = true }); return true; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    /// <summary>Una terminal nueva ya parada en la carpeta (Windows Terminal si existe).</summary>
    public void AbrirTerminal(string dir)
    {
        var env = _s.EnvPara(dir);
        var lineas = new List<string> { $"Set-Location -LiteralPath {Args.PsLiteral(dir)}" };
        lineas.AddRange(Shell.LineasEnv(env));
        var ps = Shell.PowerShellExe;
        var psArgs = Shell.ArgsScript(lineas, noSalir: true);
        if (UsarWindowsTerminal())
            IniciarShell(Shell.WindowsTerminal!, $"-w 0 new-tab -d {Args.Quote(dir)} {Args.Quote(ps)} {Args.Join(psArgs)}");
        else ProcessRunner.IniciarEnVentana(ps, Args.Join(psArgs), dir);
        _s.Ui.Escribir($"Terminal abierta en {dir}", Nivel.Ok);
    }

    public void AbrirNavegador(string url)
    {
        try
        {
            var preferido = string.IsNullOrWhiteSpace(_s.Ctx.Config.Navegador) ? _s.Estado.Navegador : _s.Ctx.Config.Navegador;
            var aviso = Navegadores.Abrir(url, preferido);
            if (aviso is not null) _s.Ui.Escribir(aviso, Nivel.Aviso);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { _s.Ui.Escribir($"No pude abrir el navegador: {ex.Message}", Nivel.Error); }
    }
}
