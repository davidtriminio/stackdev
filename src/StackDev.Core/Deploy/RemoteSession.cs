using System.Reflection;
using System.Text;
using StackDev.Core.Proc;
using StackDev.Core.Servers;

namespace StackDev.Core.Deploy;

/// <summary>
/// One working session with a server: a private folder on it (mode 700, in the user's home) where StackDev puts
/// its script and the files to install. The folder is removed when the session ends.
/// </summary>
public sealed class RemoteSession : IDisposable
{
    private const string ScriptResource = "StackDev.Core.Deploy.Remote.stackdev-remote.sh";
    private readonly Server _server;
    private readonly object _gate = new();

    /// <summary>Longest silence tolerated from a remote command (installs print as they go, so this is generous).</summary>
    public const int DefaultIdleSec = 300;

    public RemoteSession(Server server) => _server = server;

    public string? RunDir { get; private set; }

    /// <summary>The script that runs on the server (embedded in this assembly).</summary>
    public static string LoadScript()
    {
        using var s = typeof(RemoteSession).Assembly.GetManifestResourceStream(ScriptResource)
            ?? throw new InvalidOperationException("No se encontró el script remoto dentro de la aplicación.");
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd().Replace("\r\n", "\n");
    }

    /// <summary>Creates the folder and uploads the script. Null when everything went well, otherwise what failed.</summary>
    public string? Open(CancellationToken ct = default)
    {
        if (!HostKeys.IsTrusted(_server)) return "El servidor aún no está verificado: confirma su huella primero.";
        var home = Ssh.Run(_server, "printf %s \"$HOME\"", 20, ct);
        if (!home.Ok) return ExplainSsh(home);
        var h = home.Output.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(h, @"^/[A-Za-z0-9._/-]+$")) return "La carpeta personal del usuario en el servidor tiene caracteres que no se admiten: " + h;

        var dir = $"{h}/.stackdev/run-{Guid.NewGuid().ToString("N")[..12]}";
        var mk = Ssh.Run(_server, $"umask 077 && mkdir -p {Sh.Quote(dir)}", 20, ct);
        if (!mk.Ok) return ExplainSsh(mk);
        RunDir = dir;

        var local = Path.Combine(Path.GetTempPath(), "stackdev-remote-" + Guid.NewGuid().ToString("N")[..8] + ".sh");
        try
        {
            File.WriteAllText(local, LoadScript(), new UTF8Encoding(false));
            var up = Ssh.Upload(_server, local, dir + "/stackdev-remote.sh", 60, ct);
            if (!up.Ok) return ExplainSsh(up);
        }
        finally { try { File.Delete(local); } catch (IOException) { } }
        return null;
    }

    /// <summary>Uploads a local file into the session folder.</summary>
    public string? Upload(string localFile, string remoteName, int timeoutSec = 1800, CancellationToken ct = default)
    {
        if (RunDir is null) return "La sesión con el servidor no está abierta.";
        var r = Ssh.Upload(_server, localFile, RunDir + "/" + remoteName, timeoutSec, ct);
        return r.Ok ? null : ExplainSsh(r);
    }

    /// <summary>
    /// Runs a subcommand of the script as root. Lines are interpreted and delivered to <paramref name="onEvent"/>
    /// (secrets masked); the env file of env-read is only collected, never delivered.
    /// </summary>
    public RemoteResult Execute(string subcommand, IEnumerable<string> args, Action<RemoteEvent>? onEvent, CancellationToken ct, IEnumerable<string>? secrets = null,
        int timeoutSec = 0, int idleSec = DefaultIdleSec)
    {
        var result = new RemoteResult();
        if (RunDir is null) { result.LaunchError = "La sesión con el servidor no está abierta."; return result; }
        onEvent?.Invoke(new RemoteEvent(RemoteEventKind.Info, $"Ejecutando «{subcommand}» en el servidor (límite: {(timeoutSec > 0 ? timeoutSec + " s" : idleSec + " s sin respuesta")})"));
        var lastSeen = "al empezar";

        var cmd = new StringBuilder($"sudo -n bash {Sh.Quote(RunDir + "/stackdev-remote.sh")} {subcommand}");
        foreach (var a in args) cmd.Append(' ').Append(Sh.Quote(a));
        var ssh = Ssh.Options(_server, interactive: false);
        ssh.Add(cmd.ToString());

        var known = secrets?.ToList();
        var env = new StringBuilder();
        var inEnv = false;
        var fail = (string?)null;
        var sudoAsksPassword = false;

        void Handle(string raw)
        {
            lock (_gate)
            {
                if (raw.Contains("a password is required", StringComparison.OrdinalIgnoreCase) || raw.Contains("no tty present", StringComparison.OrdinalIgnoreCase))
                    sudoAsksPassword = true;
                var e = RemoteProtocol.Parse(raw);
                if (e.Kind is RemoteEventKind.Step or RemoteEventKind.Info) lastSeen = "«" + RemoteText.Translate(e.Text) + "»";
                else if (e.Kind == RemoteEventKind.Kv && e.Key != "missing") lastSeen = "después de leer «" + e.Key + "»";
                switch (e.Kind)
                {
                    case RemoteEventKind.EnvBegin: inEnv = true; return;
                    case RemoteEventKind.EnvEnd: inEnv = false; result.EnvText = env.ToString(); return;
                    case RemoteEventKind.Log when inEnv: env.AppendLine(raw.TrimEnd('\r')); return;
                    case RemoteEventKind.Done: result.Done = true; break;
                    case RemoteEventKind.Fail: fail = RemoteText.Translate(e.Text); break;
                    case RemoteEventKind.Kv: result.Values[e.Key!] = e.Text; break;
                    case RemoteEventKind.Api: if (RemoteApi.Parse(e.Text) is { } api) result.Apis.Add(api); return;
                    case RemoteEventKind.Backup: if (RemoteBackup.Parse(e.Text) is { } b) result.Backups.Add(b); return;
                }
                onEvent?.Invoke(e with { Text = SecretMask.Mask(e.Kind is RemoteEventKind.Step or RemoteEventKind.Info or RemoteEventKind.Warn or RemoteEventKind.Fail ? RemoteText.Translate(e.Text) : e.Text, known) });
            }
        }

        var code = ProcessRunner.RunCapturing("ssh", ssh, null, null, ct, Handle, timeoutSec, idleSec, closeStdin: true);
        result.ExitCode = code;
        result.FailMessage = fail is null ? null : SecretMask.Mask(fail, known);
        if (code == ProcessRunner.TimedOut)
            result.LaunchError = timeoutSec > 0
                ? $"El servidor no respondió en {timeoutSec} s (se quedó parado {lastSeen})."
                : $"El servidor no dio señales en {idleSec} s (se quedó parado {lastSeen}).";
        else if (code == 130 && ct.IsCancellationRequested) result.LaunchError = "Cancelado.";
        else if (code == 127) result.LaunchError = "No encuentro ssh en este equipo (instala «Cliente OpenSSH» en las características de Windows).";
        else if (code == 255 && !result.Done) result.LaunchError = "Se perdió la conexión con el servidor.";
        else if (sudoAsksPassword) result.FailMessage = "El usuario necesita sudo sin contraseña (NOPASSWD) para que StackDev pueda trabajar en el servidor.";
        else if (code != 0 && fail is null && !result.Done) result.FailMessage = $"El servidor terminó con el código {code}.";
        return result;
    }

    private string ExplainSsh(ProcResult r)
    {
        if (r.ErrorHome is not null) return "No encuentro ssh/scp en este equipo (instala «Cliente OpenSSH» en las características de Windows).";
        if (r.Cancelled) return "Cancelado.";
        if (r.TimeExpired) return "El servidor no respondió a tiempo.";
        var e = r.Error.Trim();
        if (e.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase) || e.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase))
            return "La huella del servidor cambió: no se conectó por seguridad.";
        return string.IsNullOrWhiteSpace(e) ? $"Falló (código {r.Code})." : SecretMask.Mask(e.Split('\n')[0]);
    }

    public void Dispose()
    {
        if (RunDir is null) return;
        try { Ssh.Run(_server, $"rm -rf {Sh.Quote(RunDir)}", 20); } catch (Exception) { /* best effort: the folder is private and small */ }
        RunDir = null;
    }
}
