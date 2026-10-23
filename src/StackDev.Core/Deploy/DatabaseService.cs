using System.Security.Cryptography;
using System.Text;
using StackDev.Core.Servers;
using StackDev.Core.Ui;

namespace StackDev.Core.Deploy;

public sealed record DatabaseResult(bool Ok, string Message, string? Connection = null, string? Database = null, string? User = null, string? Password = null);

/// <summary>Creates, in the PostgreSQL of the server, a user and a database that belong to one API only (installing PostgreSQL if needed).</summary>
public static class DatabaseService
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    /// <summary>A random password of letters and digits only, so it is safe inside a SQL script and a connection string.</summary>
    public static string NewPassword(int length = 28)
    {
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++) sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        return sb.ToString();
    }

    public static string Connection(string database, string user, string password, string host = "127.0.0.1", int port = 5432) =>
        $"Host={host};Port={port};Database={database};Username={user};Password={password}";

    public static DatabaseResult Create(Server server, string service, Action<ProgressEvent> progress, CancellationToken ct)
    {
        if (!Sh.Service.IsMatch(service)) return new(false, "Nombre de servicio no válido.");
        if (!HostKeys.IsTrusted(server)) return new(false, "El servidor aún no está verificado.");
        var password = NewPassword();
        var local = Path.Combine(Path.GetTempPath(), "stackdev-pw-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var session = new RemoteSession(server);
            if (session.Open(ct) is { } openError) return new(false, openError);
            File.WriteAllText(local, password + "\n", new UTF8Encoding(false));
            if (session.Upload(local, "db.pw", 60, ct) is { } upError) return new(false, upError);
            try { File.Delete(local); } catch (IOException) { }
            var args = new List<string> { "--svc", service, "--run-dir", session.RunDir!, "--pw-file", session.RunDir + "/db.pw" };
            var r = session.Execute("db-create", args, e => DeployService.Relay(e, 3, 20, progress), CancellationToken.None, new[] { password });
            if (!r.Ok) return new(false, r.LaunchError ?? r.FailMessage ?? $"El servidor terminó con el código {r.ExitCode}.");
            var db = r.Value("db_name"); var user = r.Value("db_user");
            if (db.Length == 0 || user.Length == 0) return new(false, "El servidor no dijo cómo se llama la base creada.");
            return new(true, "Base de datos creada.", Connection(db, user, password, r.Value("db_host") is { Length: > 0 } h ? h : "127.0.0.1"), db, user, password);
        }
        finally { try { File.Delete(local); } catch (IOException) { } }
    }
}
