using StackDev.Core.Config;
using StackDev.Core.Detection;
using StackDev.Core.Proc;
using StackDev.Core.Ui;
using StackDev.Core.Util;

namespace StackDev.Core.Docker;

public enum CodigoPostgres { Corriendo, Detenido, NoExiste, DockerApagado, DockerNoResponde, SinDocker, SinConfig }

public sealed record EstadoPostgres(CodigoPostgres Codigo, string Texto)
{
    public bool Corriendo => Codigo == CodigoPostgres.Corriendo;
}

/// <summary>Postgres en un contenedor de Docker: iniciar, crear, esperar, psql, logs, respaldo, restaurar y recrear.</summary>
public sealed class PostgresService
{
    private readonly Sesion _s;

    public PostgresService(Sesion s) { _s = s; }

    private PostgresConfig Cfg => _s.Ctx.Config.Postgres ?? new PostgresConfig();
    private string Contenedor => Cfg.ContenedorEf;

    public EstadoPostgres Estado()
    {
        if (!_s.Ctx.Tiene(Componente.Postgres)) return new(CodigoPostgres.SinConfig, "no configurado");
        if (!_s.Docker.Instalado) return new(CodigoPostgres.SinDocker, "Docker no instalado");
        var r = _s.Docker.Run(new[] { "inspect", "-f", "{{.State.Status}}", Contenedor }, 6);
        if (r.TiempoAgotado) return new(CodigoPostgres.DockerNoResponde, "DOCKER NO RESPONDE");
        if (r.Codigo == 0)
        {
            return r.Salida.Trim() == "running"
                ? new(CodigoPostgres.Corriendo, $"CORRIENDO  {Contenedor}  localhost:{Cfg.PuertoEf}")
                : new(CodigoPostgres.Detenido, $"DETENIDO  {Contenedor}");
        }
        if ((r.Error + r.Salida).Contains("No such", StringComparison.OrdinalIgnoreCase))
            return new(CodigoPostgres.NoExiste, $"NO EXISTE el contenedor {Contenedor}");
        return new(CodigoPostgres.DockerApagado, "DOCKER APAGADO (se abre al iniciar)");
    }

    // ------------------------------------------------------------ Iniciar / detener

    public bool Iniciar()
    {
        if (!_s.AfirmarComp(Componente.Postgres)) return false;
        if (!_s.Docker.IniciarDesktop()) return false;
        var e = Estado();
        if (e.Corriendo) { _s.Ui.Escribir($"Postgres ya está corriendo ({Contenedor}).", Nivel.Tenue); return true; }
        if (e.Codigo == CodigoPostgres.NoExiste)
        {
            _s.Ui.Escribir($"El contenedor '{Contenedor}' no existe.", Nivel.Aviso);
            if (!_s.Ui.Confirmar("¿Crearlo ahora con la configuración del proyecto?", true)) return false;
            if (!Crear()) return false;
        }
        else
        {
            _s.Ui.Escribir($"docker start {Contenedor}", Nivel.Tenue);
            var r = _s.Docker.Run(new[] { "start", Contenedor }, 60);
            if (!r.Ok) { _s.Ui.Escribir($"No se pudo iniciar: {r.Texto.Trim()}", Nivel.Error); return false; }
        }
        return Esperar();
    }

    public void Detener()
    {
        if (!_s.AfirmarComp(Componente.Postgres)) return;
        if (!Estado().Corriendo) { _s.Ui.Escribir("Postgres no está corriendo.", Nivel.Tenue); return; }
        _s.Ui.Escribir($"Deteniendo {Contenedor}...", Nivel.Info);
        var r = _s.Docker.Run(new[] { "stop", Contenedor }, 60);
        if (r.Ok) _s.Ui.Escribir("Postgres detenido.", Nivel.Ok);
        else _s.Ui.Escribir($"No se pudo detener: {r.Texto.Trim()}", Nivel.Error);
    }

    /// <summary>Espera (hasta 60 s) a que la base acepte conexiones: <c>pg_isready</c> dentro del contenedor.</summary>
    public bool Esperar()
    {
        _s.Ui.EscribirInline("Esperando a Postgres");
        var limite = Environment.TickCount64 + 60_000;
        while (Environment.TickCount64 < limite)
        {
            var r = _s.Docker.Run(new[] { "exec", Contenedor, "pg_isready", "-U", Cfg.UsuarioEf, "-d", Cfg.BaseDatosEf, "-q" }, 8);
            if (r.Ok)
            {
                _s.Ui.Escribir("");
                _s.Ui.Escribir($"Postgres listo en localhost:{Cfg.PuertoEf}.", Nivel.Ok);
                return true;
            }
            if (_s.Ct.IsCancellationRequested || Teclado.PulsoCancelar()) break;
            _s.Ui.EscribirInline(".");
            _s.Ct.WaitHandle.WaitOne(1000);
        }
        _s.Ui.Escribir("");
        _s.Ui.Escribir("Postgres no respondió. Revisa sus logs.", Nivel.Error);
        return false;
    }

    /// <summary>Argumentos de <c>docker run</c> con la configuración del proyecto (y el volumen, si lo hay).</summary>
    public List<string> ArgsCrear()
    {
        var a = new List<string>
        {
            "run", "-d", "--name", Contenedor,
            "-e", $"POSTGRES_USER={Cfg.UsuarioEf}",
            "-e", $"POSTGRES_PASSWORD={Cfg.PasswordEf}",
            "-e", $"POSTGRES_DB={Cfg.BaseDatosEf}",
            "-p", $"{Cfg.PuertoEf}:5432",
        };
        if (!string.IsNullOrEmpty(Cfg.VolumenEf)) { a.Add("-v"); a.Add($"{Cfg.VolumenEf}:{PostgresConfig.RutaDatos(Cfg.ImagenEf)}"); }
        a.Add(Cfg.ImagenEf);
        return a;
    }

    /// <summary>Guarda otro puerto para Postgres en la configuración del proyecto.</summary>
    public static void CambiarPuerto(Sesion s, int puerto)
    {
        var copia = ConfigJson.Leer(ConfigJson.Escribir(s.Ctx.Config));
        (copia.Postgres ??= new PostgresConfig()).Puerto = puerto;
        s.AplicarConfig(copia);
        s.Ui.Escribir($"Postgres ahora usa el puerto {puerto}.", Nivel.Ok);
    }

    /// <summary>Si el puerto configurado choca con algo, propone el siguiente libre. false = el usuario prefiere no seguir.</summary>
    private bool ResolverPuerto()
    {
        var mapa = _s.Libres.Tomar(ignorarContenedor: Contenedor, ignorarComponente: Componente.Postgres);
        var puerto = Cfg.PuertoEf;
        var motivo = mapa.Conflicto(puerto);
        if (motivo is null) return true;

        _s.Ui.Escribir($"El puerto {puerto} {motivo}.", Nivel.Aviso);
        var sugerido = mapa.Sugerir(puerto + 1);
        if (sugerido is null)
            return _s.Ui.Confirmar("No encontré un puerto libre cerca. ¿Intentar crear el contenedor de todos modos?", false);
        if (_s.Ui.Confirmar($"¿Usar el puerto {sugerido} en su lugar? (se guarda en la configuración)", true))
        {
            CambiarPuerto(_s, sugerido.Value);
            return true;
        }
        return _s.Ui.Confirmar($"¿Crearlo con el puerto {puerto} de todos modos?", false);
    }

    /// <summary>Crea el contenedor mostrando el progreso de la descarga de la imagen.</summary>
    public bool Crear()
    {
        if (!ResolverPuerto()) return false;
        var args = ArgsCrear();
        // la contraseña no debe quedar escrita en la salida
        _s.Ui.Escribir("docker " + System.Text.RegularExpressions.Regex.Replace(Args.Join(args), @"POSTGRES_PASSWORD=(""[^""]*""|'[^']*'|\S+)", "POSTGRES_PASSWORD=••••"), Nivel.Tenue);
        if (_s.Docker.RunVisible(args) != 0)
        {
            _s.Ui.Escribir("No se pudo crear el contenedor.", Nivel.Error);
            return false;
        }
        return true;
    }

    public bool AsegurarCorriendo()
    {
        if (!_s.AfirmarComp(Componente.Postgres)) return false;
        if (Estado().Corriendo) return true;
        if (!_s.Ui.Confirmar("Postgres no está corriendo. ¿Iniciarlo?", true)) return false;
        return Iniciar();
    }

    // ------------------------------------------------------------ psql y logs

    public void AbrirPsql()
    {
        if (!AsegurarCorriendo()) return;
        var cmd = $"{_s.Docker.ComandoPs} exec -it {Contenedor} psql -U {Cfg.UsuarioEf} -d {Cfg.BaseDatosEf}";
        if (_s.Ejecutor.EnVentana("psql", $"psql - {Cfg.BaseDatosEf}", _s.Raiz, cmd))
            _s.Ui.Escribir("psql abierto en otra ventana (\\q para salir).", Nivel.Ok);
    }

    public void AbrirLogs()
    {
        if (!_s.AfirmarComp(Componente.Postgres) || !_s.Docker.IniciarDesktop()) return;
        var cmd = $"{_s.Docker.ComandoPs} logs -f --tail 200 {Contenedor}";
        if (_s.Ejecutor.EnVentana("pglogs", "Logs Postgres", _s.Raiz, cmd, esperarEnter: false))
            _s.Ui.Escribir("Logs abiertos en otra ventana.", Nivel.Ok);
    }

    /// <summary>Las últimas líneas del log del contenedor (para verlas dentro de la herramienta).</summary>
    public List<string> UltimasLineasLog(int n = 15)
    {
        var r = _s.Docker.Run(new[] { "logs", "--tail", n.ToString(), Contenedor }, 8);
        return (r.Salida + "\n" + r.Error).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(n).ToList();
    }

    // ------------------------------------------------------------ Respaldo y restauración

    /// <summary>pg_dump con la salida escrita byte a byte a un .sql. Devuelve la ruta, o null si falló.</summary>
    public string? Respaldar()
    {
        if (!AsegurarCorriendo()) return null;
        var archivo = Path.Combine(_s.Estado.CarpetaRespaldos(_s.Clave), $"{Cfg.BaseDatosEf}-{DateTime.Now:yyyyMMdd-HHmmss}.sql");
        _s.Ui.Escribir($"Respaldando la base '{Cfg.BaseDatosEf}'...", Nivel.Info);
        var r = _s.Docker.RunConArchivos(
            new[] { "exec", Contenedor, "pg_dump", "-U", Cfg.UsuarioEf, "-d", Cfg.BaseDatosEf, "--clean", "--if-exists", "--no-owner" },
            null, archivo);
        if (r.Codigo == 0)
        {
            var kb = Math.Round(new FileInfo(archivo).Length / 1024.0, 1);
            _s.Ui.Escribir($"Respaldo guardado ({kb} KB): {archivo}", Nivel.Ok);
            return archivo;
        }
        try { File.Delete(archivo); } catch (IOException) { }
        _s.Ui.Escribir($"El respaldo falló: {r.Error.Trim()}", Nivel.Error);
        return null;
    }

    public List<FileInfo> ListarRespaldos(int max = 15)
    {
        var dir = _s.Estado.CarpetaRespaldos(_s.Clave);
        return new DirectoryInfo(dir).GetFiles("*.sql").OrderByDescending(f => f.LastWriteTime).Take(max).ToList();
    }

    /// <summary>Restaura un respaldo (o cualquier .sql). Pide escribir "si" para confirmar y corta al primer error.</summary>
    public bool Restaurar(string? archivo = null)
    {
        if (!AsegurarCorriendo()) return false;
        if (archivo is null)
        {
            var lista = ListarRespaldos();
            _s.Ui.Escribir($"\nRespaldos en {_s.Estado.CarpetaRespaldos(_s.Clave)}", Nivel.Info);
            if (lista.Count == 0) _s.Ui.Escribir("  (no hay respaldos todavía)", Nivel.Tenue);
            for (var i = 0; i < lista.Count; i++)
                _s.Ui.Escribir($"  [{i + 1,2}] {lista[i].Name}  ({Math.Round(lista[i].Length / 1024.0, 1)} KB)");
            var sel = _s.Ui.Preguntar("Número, o ruta de un archivo .sql (Enter = cancelar)")?.Trim().Trim('"');
            if (string.IsNullOrEmpty(sel)) return false;
            if (int.TryParse(sel, out var n) && n >= 1 && n <= lista.Count) archivo = lista[n - 1].FullName;
            else if (File.Exists(sel)) archivo = Path.GetFullPath(sel);
            else { _s.Ui.Escribir("Opción no válida.", Nivel.Error); return false; }
        }
        _s.Ui.Escribir($"Esto reemplaza los datos actuales de la base '{Cfg.BaseDatosEf}' con {Path.GetFileName(archivo)}.", Nivel.Aviso);
        if (!string.Equals(_s.Ui.Preguntar("Escribe 'si' para continuar"), "si", StringComparison.OrdinalIgnoreCase))
        {
            _s.Ui.Escribir("Cancelado.", Nivel.Tenue);
            return false;
        }
        _s.Ui.Escribir("Restaurando...", Nivel.Info);
        var r = _s.Docker.RunConArchivos(
            new[] { "exec", "-i", Contenedor, "psql", "-U", Cfg.UsuarioEf, "-d", Cfg.BaseDatosEf, "-q", "-v", "ON_ERROR_STOP=1" },
            archivo, null);
        if (r.Codigo == 0) { _s.Ui.Escribir("Base restaurada.", Nivel.Ok); return true; }
        _s.Ui.Escribir($"La restauración falló:\n{r.Error.Trim()}", Nivel.Error);
        return false;
    }

    // ------------------------------------------------------------ Recrear

    /// <summary>
    /// Borra el contenedor y lo crea de nuevo con la configuración actual. Sin volumen con nombre se pierden los datos:
    /// se ofrece un respaldo antes y se confirma escribiendo el nombre del contenedor.
    /// </summary>
    public bool Recrear()
    {
        if (!_s.AfirmarComp(Componente.Postgres) || !_s.Docker.IniciarDesktop()) return false;
        var vol = Cfg.VolumenEf;
        var e = Estado();

        _s.Ui.Escribir($"\nRecrear borra el contenedor '{Contenedor}' y lo crea de nuevo con la configuración actual.", Nivel.Aviso);
        if (vol.Length > 0) _s.Ui.Escribir($"Los datos están en el volumen '{vol}' y se conservan.", Nivel.Tenue);
        else _s.Ui.Escribir("OJO: no usa un volumen con nombre, así que se PIERDEN todos los datos.", Nivel.Error);

        string? respaldo = null;
        if (e.Codigo != CodigoPostgres.NoExiste && vol.Length == 0 && _s.Ui.Confirmar("¿Hacer un respaldo antes?", true))
        {
            if (!e.Corriendo) { _s.Docker.Run(new[] { "start", Contenedor }, 60); Esperar(); }
            respaldo = Respaldar();
            if (respaldo is null && !_s.Ui.Confirmar("El respaldo falló. ¿Continuar de todos modos?", false)) return false;
        }
        if (_s.Ui.Preguntar($"Escribe el nombre del contenedor ({Contenedor}) para confirmar") != Contenedor)
        {
            _s.Ui.Escribir("Cancelado.", Nivel.Tenue);
            return false;
        }
        if (e.Codigo != CodigoPostgres.NoExiste)
        {
            var r = _s.Docker.Run(new[] { "rm", "-f", Contenedor }, 60);
            if (!r.Ok) { _s.Ui.Escribir($"No se pudo borrar: {r.Texto.Trim()}", Nivel.Error); return false; }
        }
        if (!Crear() || !Esperar()) return false;
        if (respaldo is not null && _s.Ui.Confirmar("¿Restaurar el respaldo que acabo de hacer?", true)) Restaurar(respaldo);
        return true;
    }
}
