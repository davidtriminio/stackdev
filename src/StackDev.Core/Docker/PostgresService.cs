using StackDev.Core.Config;
using StackDev.Core.Detection;
using StackDev.Core.Proc;
using StackDev.Core.Ui;
using StackDev.Core.Util;

namespace StackDev.Core.Docker;

public enum PostgresCode { Running, Stopped, DoesNotExist, DockerOff, DockerNotResponding, NoDocker, NoConfig }

public sealed record PostgresState(PostgresCode Code, string Text)
{
    public bool Running => Code == PostgresCode.Running;
}

/// <summary>Postgres in a Docker container: start, create, wait, psql, logs, backup, restore and recreate.</summary>
public sealed class PostgresService
{
    private readonly Session _s;

    public PostgresService(Session s) { _s = s; }

    private PostgresConfig Cfg => _s.Ctx.Config.Postgres ?? new PostgresConfig();
    private string Container => Cfg.EfContainer;

    public PostgresState State()
    {
        if (!_s.Ctx.Has(ProjectComponent.Postgres)) return new(PostgresCode.NoConfig, "no configurado");
        if (!_s.Docker.Installed) return new(PostgresCode.NoDocker, "Docker no instalado");
        var r = _s.Docker.Run(new[] { "inspect", "-f", "{{.State.Status}}", Container }, 6);
        if (r.TimeExpired) return new(PostgresCode.DockerNotResponding, "DOCKER NO RESPONDE");
        if (r.Code == 0)
        {
            return r.Output.Trim() == "running"
                ? new(PostgresCode.Running, $"CORRIENDO  {Container}  localhost:{Cfg.EfPort}")
                : new(PostgresCode.Stopped, $"DETENIDO  {Container}");
        }
        if ((r.Error + r.Output).Contains("No such", StringComparison.OrdinalIgnoreCase))
            return new(PostgresCode.DoesNotExist, $"NO EXISTE el contenedor {Container}");
        return new(PostgresCode.DockerOff, "DOCKER APAGADO (se abre al iniciar)");
    }

    // ------------------------------------------------------------ Iniciar / detener

    public bool Start()
    {
        if (!_s.AssertComp(ProjectComponent.Postgres)) return false;
        if (!_s.Docker.StartDesktop()) return false;
        var e = State();
        if (e.Running) { _s.Ui.Write($"Postgres ya está corriendo ({Container}).", Level.Muted); return true; }
        if (e.Code == PostgresCode.DoesNotExist)
        {
            _s.Ui.Write($"El contenedor '{Container}' no existe.", Level.Warning);
            if (!_s.Ui.Confirm("¿Crearlo ahora con la configuración del proyecto?", true)) return false;
            if (!Create()) return false;
        }
        else
        {
            _s.Ui.Write($"docker start {Container}", Level.Muted);
            var r = _s.Docker.Run(new[] { "start", Container }, 60);
            if (!r.Ok) { _s.Ui.Write($"No se pudo iniciar: {r.Text.Trim()}", Level.Error); return false; }
        }
        return Wait();
    }

    public void Stop()
    {
        if (!_s.AssertComp(ProjectComponent.Postgres)) return;
        if (!State().Running) { _s.Ui.Write("Postgres no está corriendo.", Level.Muted); return; }
        _s.Ui.Write($"Deteniendo {Container}...", Level.Info);
        var r = _s.Docker.Run(new[] { "stop", Container }, 60);
        if (r.Ok) _s.Ui.Write("Postgres detenido.", Level.Ok);
        else _s.Ui.Write($"No se pudo detener: {r.Text.Trim()}", Level.Error);
    }

    /// <summary>Waits (up to 60 s) for the database to accept connections: <c>pg_isready</c> inside the container.</summary>
    public bool Wait()
    {
        _s.Ui.WriteInline("Esperando a Postgres");
        var limit = Environment.TickCount64 + 60_000;
        while (Environment.TickCount64 < limit)
        {
            var r = _s.Docker.Run(new[] { "exec", Container, "pg_isready", "-U", Cfg.EfUser, "-d", Cfg.EfDatabase, "-q" }, 8);
            if (r.Ok)
            {
                _s.Ui.Write("");
                _s.Ui.Write($"Postgres listo en localhost:{Cfg.EfPort}.", Level.Ok);
                return true;
            }
            if (_s.Ct.IsCancellationRequested || ConsoleKeys.PressCancel()) break;
            _s.Ui.WriteInline(".");
            _s.Ct.WaitHandle.WaitOne(1000);
        }
        _s.Ui.Write("");
        _s.Ui.Write("Postgres no respondió. Revisa sus logs.", Level.Error);
        return false;
    }

    /// <summary><c>docker run</c> arguments with the project's configuration (and the volume, if any).</summary>
    public List<string> CreateArgs()
    {
        var a = new List<string>
        {
            "run", "-d", "--name", Container,
            "-e", $"POSTGRES_USER={Cfg.EfUser}",
            "-e", $"POSTGRES_PASSWORD={Cfg.EfPassword}",
            "-e", $"POSTGRES_DB={Cfg.EfDatabase}",
            "-p", $"{Cfg.EfPort}:5432",
        };
        if (!string.IsNullOrEmpty(Cfg.EfVolume)) { a.Add("-v"); a.Add($"{Cfg.EfVolume}:{PostgresConfig.DataPath(Cfg.EfImage)}"); }
        a.Add(Cfg.EfImage);
        return a;
    }

    /// <summary>Saves another port for Postgres in the project configuration.</summary>
    public static void ChangePort(Session s, int port)
    {
        var copy = ConfigJson.Read(ConfigJson.Write(s.Ctx.Config));
        (copy.Postgres ??= new PostgresConfig()).Port = port;
        s.ApplyConfig(copy);
        s.Ui.Write($"Postgres ahora usa el puerto {port}.", Level.Ok);
    }

    /// <summary>If the configured port clashes with something, proposes the next free one. false = the user prefers not to go on.</summary>
    private bool ResolvePort()
    {
        var map = _s.Free.Take(ignoreContainer: Container, ignoreComponent: ProjectComponent.Postgres);
        var port = Cfg.EfPort;
        var reason = map.Conflict(port);
        if (reason is null) return true;

        _s.Ui.Write($"El puerto {port} {reason}.", Level.Warning);
        var suggested = map.Suggest(port + 1);
        if (suggested is null)
            return _s.Ui.Confirm("No encontré un puerto libre cerca. ¿Intentar crear el contenedor de todos modos?", false);
        if (_s.Ui.Confirm($"¿Usar el puerto {suggested} en su lugar? (se guarda en la configuración)", true))
        {
            ChangePort(_s, suggested.Value);
            return true;
        }
        return _s.Ui.Confirm($"¿Crearlo con el puerto {port} de todos modos?", false);
    }

    /// <summary>Creates the container showing the progress of the image download.</summary>
    public bool Create()
    {
        if (!ResolvePort()) return false;
        var args = CreateArgs();
        // the password must not be written to the output
        _s.Ui.Write("docker " + System.Text.RegularExpressions.Regex.Replace(Args.Join(args), @"POSTGRES_PASSWORD=(""[^""]*""|'[^']*'|\S+)", "POSTGRES_PASSWORD=••••"), Level.Muted);
        if (_s.Docker.RunVisible(args) != 0)
        {
            _s.Ui.Write("No se pudo crear el contenedor.", Level.Error);
            return false;
        }
        return true;
    }

    public bool EnsureRunning()
    {
        if (!_s.AssertComp(ProjectComponent.Postgres)) return false;
        if (State().Running) return true;
        if (!_s.Ui.Confirm("Postgres no está corriendo. ¿Iniciarlo?", true)) return false;
        return Start();
    }

    // ------------------------------------------------------------ psql and logs

    public void OpenPsql()
    {
        if (!EnsureRunning()) return;
        var cmd = $"{_s.Docker.PsCommand} exec -it {Container} psql -U {Cfg.EfUser} -d {Cfg.EfDatabase}";
        if (_s.Executor.InWindow("psql", $"psql - {Cfg.EfDatabase}", _s.Root, cmd))
            _s.Ui.Write("psql abierto en otra ventana (\\q para salir).", Level.Ok);
    }

    public void OpenLogs()
    {
        if (!_s.AssertComp(ProjectComponent.Postgres) || !_s.Docker.StartDesktop()) return;
        var cmd = $"{_s.Docker.PsCommand} logs -f --tail 200 {Container}";
        if (_s.Executor.InWindow("pglogs", "Logs Postgres", _s.Root, cmd, waitEnter: false))
            _s.Ui.Write("Logs abiertos en otra ventana.", Level.Ok);
    }

    /// <summary>The last lines of the container log (to view them inside the tool).</summary>
    public List<string> LastLogLines(int n = 15)
    {
        var r = _s.Docker.Run(new[] { "logs", "--tail", n.ToString(), Container }, 8);
        return (r.Output + "\n" + r.Error).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(n).ToList();
    }

    // ------------------------------------------------------------ Backup and restore

    /// <summary>pg_dump with the output written byte by byte to a .sql file. Returns the path, or null if it failed.</summary>
    public string? Back()
    {
        if (!EnsureRunning()) return null;
        var file = Path.Combine(_s.State.BackupsFolder(_s.Key), $"{Cfg.EfDatabase}-{DateTime.Now:yyyyMMdd-HHmmss}.sql");
        _s.Ui.Write($"Respaldando la base '{Cfg.EfDatabase}'...", Level.Info);
        var r = _s.Docker.RunWithFiles(
            new[] { "exec", Container, "pg_dump", "-U", Cfg.EfUser, "-d", Cfg.EfDatabase, "--clean", "--if-exists", "--no-owner" },
            null, file);
        if (r.Code == 0)
        {
            var kb = Math.Round(new FileInfo(file).Length / 1024.0, 1);
            _s.Ui.Write($"Respaldo guardado ({kb} KB): {file}", Level.Ok);
            return file;
        }
        try { File.Delete(file); } catch (IOException) { }
        _s.Ui.Write($"El respaldo falló: {r.Error.Trim()}", Level.Error);
        return null;
    }

    public List<FileInfo> ListBackups(int max = 15)
    {
        var dir = _s.State.BackupsFolder(_s.Key);
        return new DirectoryInfo(dir).GetFiles("*.sql").OrderByDescending(f => f.LastWriteTime).Take(max).ToList();
    }

    /// <summary>Restores a backup (or any .sql). Asks to type "si" to confirm and stops at the first error.</summary>
    public bool Restore(string? file = null)
    {
        if (!EnsureRunning()) return false;
        if (file is null)
        {
            var list = ListBackups();
            _s.Ui.Write($"\nRespaldos en {_s.State.BackupsFolder(_s.Key)}", Level.Info);
            if (list.Count == 0) _s.Ui.Write("  (no hay respaldos todavía)", Level.Muted);
            for (var i = 0; i < list.Count; i++)
                _s.Ui.Write($"  [{i + 1,2}] {list[i].Name}  ({Math.Round(list[i].Length / 1024.0, 1)} KB)");
            var sel = _s.Ui.Ask("Número, o ruta de un archivo .sql (Enter = cancelar)")?.Trim().Trim('"');
            if (string.IsNullOrEmpty(sel)) return false;
            if (int.TryParse(sel, out var n) && n >= 1 && n <= list.Count) file = list[n - 1].FullName;
            else if (File.Exists(sel)) file = Path.GetFullPath(sel);
            else { _s.Ui.Write("Opción no válida.", Level.Error); return false; }
        }
        _s.Ui.Write($"Esto reemplaza los datos actuales de la base '{Cfg.EfDatabase}' con {Path.GetFileName(file)}.", Level.Warning);
        if (!string.Equals(_s.Ui.Ask("Escribe 'si' para continuar"), "si", StringComparison.OrdinalIgnoreCase))
        {
            _s.Ui.Write("Cancelado.", Level.Muted);
            return false;
        }
        _s.Ui.Write("Restaurando...", Level.Info);
        var r = _s.Docker.RunWithFiles(
            new[] { "exec", "-i", Container, "psql", "-U", Cfg.EfUser, "-d", Cfg.EfDatabase, "-q", "-v", "ON_ERROR_STOP=1" },
            file, null);
        if (r.Code == 0) { _s.Ui.Write("Base restaurada.", Level.Ok); return true; }
        _s.Ui.Write($"La restauración falló:\n{r.Error.Trim()}", Level.Error);
        return false;
    }

    // ------------------------------------------------------------ Recrear

    /// <summary>
    /// Deletes the container and creates it again with the current configuration. Without a named volume the data is lost:
    /// a backup is offered first and the user confirms by typing the container name.
    /// </summary>
    public bool Recreate()
    {
        if (!_s.AssertComp(ProjectComponent.Postgres) || !_s.Docker.StartDesktop()) return false;
        var vol = Cfg.EfVolume;
        var e = State();

        _s.Ui.Write($"\nRecrear borra el contenedor '{Container}' y lo crea de nuevo con la configuración actual.", Level.Warning);
        if (vol.Length > 0) _s.Ui.Write($"Los datos están en el volumen '{vol}' y se conservan.", Level.Muted);
        else _s.Ui.Write("OJO: no usa un volumen con nombre, así que se PIERDEN todos los datos.", Level.Error);

        string? backup = null;
        if (e.Code != PostgresCode.DoesNotExist && vol.Length == 0 && _s.Ui.Confirm("¿Hacer un respaldo antes?", true))
        {
            if (!e.Running) { _s.Docker.Run(new[] { "start", Container }, 60); Wait(); }
            backup = Back();
            if (backup is null && !_s.Ui.Confirm("El respaldo falló. ¿Continuar de todos modos?", false)) return false;
        }
        if (_s.Ui.Ask($"Escribe el nombre del contenedor ({Container}) para confirmar") != Container)
        {
            _s.Ui.Write("Cancelado.", Level.Muted);
            return false;
        }
        if (e.Code != PostgresCode.DoesNotExist)
        {
            var r = _s.Docker.Run(new[] { "rm", "-f", Container }, 60);
            if (!r.Ok) { _s.Ui.Write($"No se pudo borrar: {r.Text.Trim()}", Level.Error); return false; }
        }
        if (!Create() || !Wait()) return false;
        if (backup is not null && _s.Ui.Confirm("¿Restaurar el respaldo que acabo de hacer?", true)) Restore(backup);
        return true;
    }
}
