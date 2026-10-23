using System.Text;
using StackDev.Core.Servers;
using StackDev.Core.Ui;

namespace StackDev.Core.Deploy;

public sealed record EnvReadResult(bool Ok, string Message, EnvFile? File = null);

/// <summary>Reads and changes the .env of an API on the server (secrets, CORS...), keeping a backup and undoing the change if the service stops working.</summary>
public static class EnvService
{
    public static EnvReadResult Read(Server server, string service, CancellationToken ct = default)
    {
        if (!Sh.Service.IsMatch(service)) return new(false, "Nombre de servicio no válido.");
        if (!HostKeys.IsTrusted(server)) return new(false, "El servidor aún no está verificado.");
        using var session = new RemoteSession(server);
        if (session.Open(ct) is { } e) return new(false, e);
        var r = session.Execute("env-read", new[] { "--svc", service }, null, ct);
        if (!r.Ok || r.EnvText is null) return new(false, r.LaunchError ?? r.FailMessage ?? "No se pudo leer el archivo.");
        return new(true, "", EnvFile.Parse(r.EnvText));
    }

    /// <summary>Installs <paramref name="env"/> on the server. With <paramref name="restart"/> the service is restarted and, if it does not come up, the previous file comes back.</summary>
    public static DeployResult Write(Server server, string service, EnvFile env, bool restart, Action<ProgressEvent> progress, CancellationToken ct = default)
    {
        if (!Sh.Service.IsMatch(service)) return new(DeployOutcome.Failed, "Nombre de servicio no válido.");
        if (!HostKeys.IsTrusted(server)) return new(DeployOutcome.Failed, "El servidor aún no está verificado.");
        var local = Path.Combine(Path.GetTempPath(), "stackdev-env-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var session = new RemoteSession(server);
            if (session.Open(ct) is { } openError) return new(DeployOutcome.Failed, openError);
            File.WriteAllText(local, env.ToText(), new UTF8Encoding(false));
            if (session.Upload(local, "new.env", 60, ct) is { } upError) return new(DeployOutcome.Failed, upError);
            try { File.Delete(local); } catch (IOException) { }

            var args = new List<string> { "--svc", service, "--run-dir", session.RunDir!, "--env-file", session.RunDir + "/new.env", "--wait", "30" };
            if (restart) args.Add("--restart");
            var r = session.Execute("env-write", args, e => DeployService.Relay(e, 10, 85, progress), CancellationToken.None, env.SecretValues());
            if (r.Ok) return new(DeployOutcome.Success, restart ? "Variables guardadas y servicio reiniciado." : "Variables guardadas. Reinicia el servicio para aplicarlas.");
            var missing = MissingSettings.Parse(r.Value("missing"));
            return r.ExitCode switch
            {
                23 => new(DeployOutcome.StartFailed, r.FailMessage ?? "Se guardaron los valores, pero el servicio todavía no arranca.", Missing: missing),
                20 => new(DeployOutcome.RolledBack, r.FailMessage ?? "Los valores nuevos rompieron el servicio; volvió el archivo anterior.", Missing: missing),
                21 => new(DeployOutcome.RollbackFailed, r.FailMessage ?? "Los valores nuevos rompieron el servicio y no se pudo recuperar."),
                _ => new(DeployOutcome.Failed, r.LaunchError ?? r.FailMessage ?? $"El servidor terminó con el código {r.ExitCode}."),
            };
        }
        finally { try { File.Delete(local); } catch (IOException) { } }
    }
}
