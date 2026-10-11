using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using StackDev.Core.Config;
using StackDev.Core.Docker;
using StackDev.Core.Proc;
using StackDev.Core.Ui;

namespace StackDev.Core.Servicios;

public enum CodigoServicio { Detenido, Arrancando, Corriendo, Error, SinConfig }

public sealed record EstadoServicio(CodigoServicio Codigo, string Texto, string? Url = null, int? Pid = null)
{
    public bool Corriendo => Codigo == CodigoServicio.Corriendo;
}

/// <summary>
/// API y web: corren ocultas con su salida a un archivo de log (siguen vivas aunque cierres la herramienta).
/// "Corriendo" = escuchando en su puerto; "arrancando" = el proceso vive pero aún no escucha;
/// "error" = terminó con un código distinto de cero (se guarda en un archivo marcador).
/// </summary>
public sealed class ServiciosManager
{
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private readonly Sesion _s;

    public ServiciosManager(Sesion s)
    {
        _s = s;
        Registro = new RegistroProcesos(Path.Combine(s.Estado.Dir, $"ventanas-{s.Clave}.txt"));
    }

    public RegistroProcesos Registro { get; }

    public static string ClaveDe(Componente c) => c switch
    {
        Componente.Api => "api", Componente.Web => "web", _ => throw new ArgumentException("Solo API y web son servicios con puerto."),
    };

    private string Marca(string clave) => Path.Combine(Path.GetTempPath(), $"devmenu-{_s.Clave}-{clave}.exit");

    public string RutaLog(string clave) => Path.Combine(_s.Estado.Dir, "logs", _s.Clave, $"{clave}.log");

    // ------------------------------------------------------------ Estado

    public EstadoServicio Estado(Componente c)
    {
        if (!_s.Ctx.Tiene(c)) return new(CodigoServicio.SinConfig, "no configurado");
        var clave = ClaveDe(c);
        var url = _s.Ctx.Url(c);
        var viva = Registro.Viva(clave);
        if (Puertos.EstaEscuchando(_s.Ctx.Puerto(c))) return new(CodigoServicio.Corriendo, "CORRIENDO", url, viva?.Id);
        if (viva is not null) return new(CodigoServicio.Arrancando, "ARRANCANDO", url, viva.Id);
        var codigo = LeerMarca(clave);
        if (codigo is not null && codigo != "0")
            return new(CodigoServicio.Error, $"TERMINÓ (código {codigo}): revisa el log");
        return new(CodigoServicio.Detenido, "DETENIDA");
    }

    private string? LeerMarca(string clave)
    {
        try { var f = Marca(clave); return File.Exists(f) ? File.ReadAllText(f).Trim() : null; }
        catch (IOException) { return null; }
    }

    /// <summary>"OK" si ya escucha; "" si todavía arranca; si no, el motivo por el que no va a arrancar.</summary>
    public string TestListo(Componente c)
    {
        var puerto = _s.Ctx.Puerto(c);
        if (puerto <= 0) return "no tiene una url válida en la configuración";
        if (Puertos.EstaEscuchando(puerto)) return "OK";
        var clave = ClaveDe(c);
        if (Registro.Viva(clave) is not null) return "";
        var codigo = LeerMarca(clave);
        return codigo is not null ? $"terminó con código {codigo} (revisa el log)" : "su proceso se cerró";
    }

    // ------------------------------------------------------------ Iniciar / detener

    public bool AsegurarNodeModules()
    {
        var dir = _s.Ctx.Carpeta(Componente.Web);
        if (Directory.Exists(Path.Combine(dir, "node_modules"))) return true;
        var inst = _s.Comando(Componente.Web, "instalar") ?? "npm install";
        _s.Ui.Escribir("Faltan las dependencias de la web (node_modules).", Nivel.Aviso);
        if (!_s.Ui.Confirmar($"¿Instalarlas ahora con '{inst}'?", true)) return false;
        return _s.Ejecutor.Aqui(inst, dir) == 0;
    }

    public bool IniciarApi(bool esperar = true, bool sinPostgres = false)
    {
        if (!_s.AfirmarComp(Componente.Api)) return false;
        if (Puertos.EstaEscuchando(_s.Ctx.Puerto(Componente.Api)))
        {
            _s.Ui.Escribir($"La API ya está corriendo en {_s.Ctx.Url(Componente.Api)}.", Nivel.Aviso);
            return true;
        }
        if (!sinPostgres && _s.Ctx.Tiene(Componente.Postgres) && _s.Postgres.Estado().Codigo != CodigoPostgres.Corriendo)
        {
            _s.Ui.Escribir("La API necesita Postgres; lo inicio primero.", Nivel.Info);
            if (!_s.Postgres.Iniciar() && !_s.Ui.Confirmar("Postgres no arrancó. ¿Iniciar la API de todos modos?", false)) return false;
        }
        var cmd = _s.Comando(Componente.Api, "iniciar");
        if (cmd is null) { _s.Ui.Escribir("Falta api.iniciar en la configuración.", Nivel.Error); return false; }
        _s.Ui.Escribir("Iniciando la API en segundo plano (su salida va al log)...", Nivel.Info);
        if (!IniciarOculto("api", _s.Ctx.Carpeta(Componente.Api), cmd, _s.Ctx.Config.Api?.Env)) return false;
        return !esperar || _s.Todo.Esperar(new[] { "api" });
    }

    public bool IniciarWeb(bool esperar = true)
    {
        if (!_s.AfirmarComp(Componente.Web)) return false;
        if (Puertos.EstaEscuchando(_s.Ctx.Puerto(Componente.Web)))
        {
            _s.Ui.Escribir($"La web ya está corriendo en {_s.Ctx.Url(Componente.Web)}.", Nivel.Aviso);
            return true;
        }
        if (!AsegurarNodeModules()) return false;
        var cmd = _s.Comando(Componente.Web, "iniciar");
        if (cmd is null) { _s.Ui.Escribir("Falta web.iniciar en la configuración.", Nivel.Error); return false; }
        _s.Ui.Escribir("Iniciando la web en segundo plano (su salida va al log)...", Nivel.Info);
        if (!IniciarOculto("web", _s.Ctx.Carpeta(Componente.Web), cmd, null)) return false;
        return !esperar || _s.Todo.Esperar(new[] { "web" });
    }

    /// <summary>
    /// Lanza el comando en un cmd oculto con su salida a un archivo. Al terminar escribe su código en el marcador.
    /// Sin ventana propia no recibe el Ctrl+C de esta consola, así que sigue vivo si cierras la herramienta.
    /// </summary>
    private bool IniciarOculto(string clave, string carpeta, string comando, IReadOnlyDictionary<string, string>? env)
    {
        var log = RutaLog(clave);
        var marca = Marca(clave);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            if (File.Exists(log)) File.Copy(log, Path.ChangeExtension(log, ".anterior.log"), overwrite: true);
            File.WriteAllText(log, $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {comando}  ({carpeta}) ===\n", new UTF8Encoding(false));
            if (File.Exists(marca)) File.Delete(marca);
        }
        catch (IOException ex) { _s.Ui.Escribir($"No pude preparar el log: {ex.Message}", Nivel.Error); return false; }

        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = carpeta,
            // /v:on para que !errorlevel! se evalúe al ejecutar; chcp 65001 para que la salida quede en UTF-8
            Arguments = $"/v:on /d /s /c \"chcp 65001>nul & ({comando}) >> \"{log}\" 2>&1 & echo !errorlevel!> \"{marca}\"\"",
        };
        psi.Environment["NO_COLOR"] = "1";
        foreach (var (k, v) in _s.EnvPara(carpeta) ?? new Dictionary<string, string>()) psi.Environment[k] = v;
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;

        try
        {
            var p = Process.Start(psi)!;
            Registro.Guardar(clave, p);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _s.Ui.Escribir($"No se pudo iniciar: {ex.Message}", Nivel.Error);
            return false;
        }
    }

    /// <summary>Cierra el árbol completo de procesos y, si el puerto sigue ocupado, pregunta si se cierra quien lo tiene.</summary>
    public void Detener(Componente c, bool silencioso = false)
    {
        if (!_s.Ctx.Tiene(c)) return;
        var clave = ClaveDe(c);
        var nombre = Sesion.Nombres[c];
        var habia = DetenerProceso(clave);
        if (habia) Thread.Sleep(800);

        var puerto = _s.Ctx.Puerto(c);
        foreach (var id in Puertos.Pids(puerto))
        {
            string n = "?";
            try { using var proc = Process.GetProcessById(id); n = proc.ProcessName; } catch (ArgumentException) { }
            if (_s.Ui.Confirmar($"El puerto {puerto} lo usa {n} (PID {id}). ¿Cerrarlo?", true))
            {
                ProcessRunner.KillTree(id);
                habia = true;
            }
        }
        try { File.Delete(Marca(clave)); } catch (IOException) { }
        if (habia) _s.Ui.Escribir($"{nombre} detenida.", Nivel.Ok);
        else if (!silencioso) _s.Ui.Escribir($"{nombre} no estaba corriendo.", Nivel.Tenue);
    }

    /// <summary>Las claves de las sesiones de flutter run: «app» (emulador) y «app-serial» (otros dispositivos).</summary>
    public static bool EsClaveApp(string k) => k == "app" || k.StartsWith("app-", StringComparison.Ordinal);

    public List<string> AppsVivas() => Registro.Claves().Where(k => EsClaveApp(k) && Registro.Viva(k) is not null).ToList();

    /// <summary>Cierra todas las sesiones de flutter run. Devuelve cuántas había.</summary>
    public int DetenerApps()
    {
        var n = 0;
        foreach (var k in Registro.Claves().Where(EsClaveApp).ToList())
            if (DetenerProceso(k)) n++;
        return n;
    }

    /// <summary>Cierra el árbol de procesos registrado con esa clave. Devuelve true si había uno vivo.</summary>
    public bool DetenerProceso(string clave)
    {
        var p = Registro.Viva(clave);
        if (p is null) return false;
        ProcessRunner.KillTree(p);
        Registro.Olvidar(clave);
        return true;
    }

    public void Reiniciar(Componente c)
    {
        Detener(c, silencioso: true);
        if (c == Componente.Api) IniciarApi(); else IniciarWeb();
    }

    public void AbrirEnNavegador(Componente c)
    {
        if (!_s.AfirmarComp(c)) return;
        var url = _s.Ctx.UrlParaAbrir(c);
        if (url is null) return;
        if (!Puertos.EstaEscuchando(_s.Ctx.Puerto(c))) _s.Ui.Escribir($"Ojo: {Sesion.Nombres[c]} no está corriendo.", Nivel.Aviso);
        _s.Ejecutor.AbrirNavegador(url);
    }

    // ------------------------------------------------------------ Logs

    public static string LimpiarAnsi(string texto) => Ansi.Replace(texto, "");

    /// <summary>Las últimas líneas del log (sin códigos de color).</summary>
    public List<string> UltimasLineas(string clave, int n)
    {
        var f = RutaLog(clave);
        if (!File.Exists(f)) return new();
        try
        {
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var tam = Math.Min(fs.Length, 256 * 1024);
            fs.Seek(-tam, SeekOrigin.End);
            var buf = new byte[tam];
            var leidos = fs.Read(buf, 0, buf.Length);
            var texto = LimpiarAnsi(Encoding.UTF8.GetString(buf, 0, leidos));
            var lineas = texto.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
            return lineas.Count <= n ? lineas : lineas.GetRange(lineas.Count - n, n);
        }
        catch (IOException) { return new(); }
    }

    public string? UltimaLinea(string clave) => UltimasLineas(clave, 1).FirstOrDefault();

    /// <summary>Muestra el log y sigue mostrando lo nuevo hasta que se cancela (Ctrl+C).</summary>
    public void Seguir(string clave, int inicial, CancellationToken ct, Action<string> escribir)
    {
        foreach (var l in UltimasLineas(clave, inicial)) escribir(l);
        var f = RutaLog(clave);
        long pos = File.Exists(f) ? new FileInfo(f).Length : 0;
        var pendiente = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(f))
                {
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (fs.Length < pos) { pos = 0; escribir("--- el log se reinició ---"); }
                    if (fs.Length > pos)
                    {
                        fs.Seek(pos, SeekOrigin.Begin);
                        var buf = new byte[fs.Length - pos];
                        var n = fs.Read(buf, 0, buf.Length);
                        pos += n;
                        pendiente.Append(Encoding.UTF8.GetString(buf, 0, n));
                        var texto = pendiente.ToString();
                        var corte = texto.LastIndexOf('\n');
                        if (corte >= 0)
                        {
                            foreach (var l in LimpiarAnsi(texto[..corte]).Split('\n')) escribir(l.TrimEnd('\r'));
                            pendiente.Clear().Append(texto[(corte + 1)..]);
                        }
                    }
                }
            }
            catch (IOException) { }
            ct.WaitHandle.WaitOne(300);
        }
    }

    /// <summary>Abre una pestaña (o ventana) que sigue el log en vivo.</summary>
    public void AbrirLogEnPestana(string clave)
    {
        var f = RutaLog(clave);
        if (!File.Exists(f)) { _s.Ui.Escribir("Todavía no hay log de este servicio.", Nivel.Aviso); return; }
        var cmd = $"Get-Content -LiteralPath {Util.Args.PsLiteral(f)} -Wait -Tail 200 -Encoding UTF8";
        if (_s.Ejecutor.EnVentana("log-" + clave, $"Log {clave} - {_s.Ctx.Nombre}", _s.Raiz, cmd, esperarEnter: false))
            _s.Ui.Escribir("Log abierto en otra pestaña.", Nivel.Ok);
        else _s.Ui.Escribir("No se pudo abrir la pestaña.", Nivel.Error);
    }
}
