using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using StackDev.Core.Servers;
using StackDev.Core.Ui;

namespace StackDev.Core.Deploy;

public enum DatabaseKind { Sqlite, Postgres, None }

public sealed class ProvisionRequest
{
    public required Server Server { get; init; }
    public required string Service { get; init; }
    public required string Dll { get; init; }
    public required string Domain { get; init; }
    public required int Port { get; init; }
    public DatabaseKind Database { get; init; } = DatabaseKind.Sqlite;
    /// <summary>Connection string when <see cref="Database"/> is Postgres.</summary>
    public string? PostgresConnection { get; init; }
    /// <summary>Admin password; empty means generate a strong one.</summary>
    public string? AdminPassword { get; init; }
    /// <summary>Origin of the frontend (CORS), e.g. https://my-app.vercel.app. Empty to add it later.</summary>
    public string? FrontendOrigin { get; init; }
    public bool IssueCertificate { get; init; } = true;
    public string? CertificateEmail { get; init; }
    public int? RuntimeMajor { get; init; }
    /// <summary>Replace an existing service/site of the same name (the old files are backed up).</summary>
    public bool Overwrite { get; init; }
    /// <summary>Extra variables written to the .env.</summary>
    public IReadOnlyDictionary<string, string> ExtraEnv { get; init; } = new Dictionary<string, string>();
}

/// <summary>Secrets created for a new API. Shown once to the user and never logged.</summary>
public sealed record ProvisionSecrets(string AdminPassword, string JwtKey);

public sealed record ProvisionResult(bool Ok, string Message, ProvisionSecrets? Secrets = null, bool HttpsPending = false, string? DnsWarning = null);

/// <summary>Sets up a new API on a server: user, folders, secrets, systemd unit, Apache or Nginx site, firewall, HTTPS.</summary>
public static class ProvisionService
{
    /// <summary>The .env for a new API. Pure, so it can be tested.</summary>
    public static (EnvFile Env, ProvisionSecrets Secrets) BuildEnv(ProvisionRequest r)
    {
        var jwt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var admin = string.IsNullOrWhiteSpace(r.AdminPassword) ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', 'A').Replace('/', 'b').TrimEnd('=') : r.AdminPassword;
        var env = new EnvFile();
        env.Set("ASPNETCORE_ENVIRONMENT", "Production");
        env.Set("ASPNETCORE_URLS", $"http://127.0.0.1:{r.Port}");
        env.Set("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
        switch (r.Database)
        {
            case DatabaseKind.Sqlite: env.Set("ConnectionStrings__Default", $"Data Source=/var/lib/{r.Service}/{r.Service}.db"); break;
            case DatabaseKind.Postgres when !string.IsNullOrWhiteSpace(r.PostgresConnection): env.Set("ConnectionStrings__Default", r.PostgresConnection!); break;
        }
        env.Set("Jwt__Key", jwt);
        env.Set("Admin__Password", admin);
        env.Set("Storage__UploadsPath", $"/var/lib/{r.Service}/uploads");
        if (!string.IsNullOrWhiteSpace(r.FrontendOrigin)) env.Set("Cors__AllowedOrigins__0", r.FrontendOrigin!.Trim().TrimEnd('/'));
        foreach (var (k, v) in r.ExtraEnv) env.Set(k, v);
        return (env, new ProvisionSecrets(admin, jwt));
    }

    /// <summary>Everything that can be checked before touching the server.</summary>
    public static string? Validate(ProvisionRequest r)
    {
        if (!Sh.Service.IsMatch(r.Service)) return "El nombre del servicio no es válido (minúsculas, números y guiones, empezando con letra).";
        if (!Sh.Dll.IsMatch(r.Dll)) return "El nombre del .dll no es válido.";
        if (!Sh.Domain.IsMatch(r.Domain)) return "El dominio no es válido (por ejemplo: mi-api.midominio.com).";
        if (!Sh.ValidPort(r.Port)) return "El puerto debe estar entre 1024 y 65535.";
        if (r.IssueCertificate && !string.IsNullOrWhiteSpace(r.CertificateEmail) && !Sh.Email.IsMatch(r.CertificateEmail!)) return "El correo del certificado no es válido.";
        if (r.Database == DatabaseKind.Postgres && string.IsNullOrWhiteSpace(r.PostgresConnection)) return "Falta la cadena de conexión de PostgreSQL.";
        if (!string.IsNullOrWhiteSpace(r.FrontendOrigin) && !Uri.TryCreate(r.FrontendOrigin, UriKind.Absolute, out var u)) return "El origen del frontend debe ser una URL completa (https://...).";
        if (!HostKeys.IsTrusted(r.Server)) return "El servidor aún no está verificado: prueba la conexión y confirma su huella.";
        return null;
    }

    /// <summary>Warns when the domain does not point at the server yet (the certificate would fail).</summary>
    public static string? CheckDns(string domain, string serverHost)
    {
        try
        {
            var wanted = IPAddress.TryParse(serverHost, out var ip) ? new[] { ip } : Dns.GetHostAddresses(serverHost);
            var got = Dns.GetHostAddresses(domain);
            return got.Any(g => wanted.Any(w => w.Equals(g)))
                ? null
                : $"{domain} apunta a {string.Join(", ", got.Select(g => g.ToString()))}, no a {serverHost}. Crea el registro A (sin proxy/nube naranja si usas Cloudflare) antes de pedir el certificado.";
        }
        catch (System.Net.Sockets.SocketException)
        {
            return $"{domain} aún no existe en el DNS. Crea el registro A hacia {serverHost} (sin proxy si usas Cloudflare); sin eso el certificado HTTPS no se podrá emitir.";
        }
    }


    /// <summary>
    /// A free address that already points at the server, so no DNS record has to be created: service.1-2-3-4.sslip.io.
    /// Null when the server is a name instead of an IPv4 address (then the user types their own domain).
    /// </summary>
    public static string? AutoDomain(string service, string serverHost)
    {
        if (!Sh.Service.IsMatch(service) || !ServerValidation.ValidIpv4(serverHost)) return null;
        return $"{service}.{serverHost.Replace('.', '-')}.sslip.io";
    }

    /// <summary>
    /// Whether ports 80 and 443 answer from outside. A refused connection is fine (the network lets it through and nothing listens yet);
    /// a timeout means something drops it: the server firewall (creating the API opens it) or the cloud network rules.
    /// </summary>
    public static string? CheckPorts(string host, int timeoutMs = 3000)
    {
        var blocked = new List<int>();
        foreach (var port in new[] { 80, 443 })
        {
            try
            {
                using var client = new TcpClient();
                var connect = client.ConnectAsync(host, port);
                if (!connect.Wait(timeoutMs)) blocked.Add(port);
            }
            catch (AggregateException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }) { }
            catch (AggregateException) { }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) { }
            catch (SocketException) { }
        }
        return blocked.Count == 0 ? null
            : $"El puerto {string.Join(" y ", blocked)} no responde desde internet. Puede ser el firewall del servidor (al crear la API se abre) o las reglas de red de tu nube. "
              + "En Oracle Cloud: Redes › tu VCN › Lista de seguridad › Reglas de entrada: añade TCP 80 y TCP 443 desde 0.0.0.0/0. Sin eso el certificado HTTPS no se puede emitir.";
    }

    /// <summary>Asks for the certificate again for a site that already exists (after fixing DNS or the network).</summary>
    public static ProvisionResult RetryHttps(Server server, string domain, string? email, string web, Action<ProgressEvent> progress, CancellationToken ct)
    {
        if (!Sh.Domain.IsMatch(domain)) return new(false, "El dominio no es válido.");
        if (!HostKeys.IsTrusted(server)) return new(false, "El servidor aún no está verificado: prueba la conexión y confirma su huella.");
        progress(new ProgressEvent("HTTPS", "Conectando con el servidor…", Level.Title, 5));
        using var session = new RemoteSession(server);
        if (session.Open(ct) is { } openError) return new(false, openError);
        var args = new List<string> { "--domain", domain, "--web", string.Equals(web, "nginx", StringComparison.OrdinalIgnoreCase) ? "nginx" : "apache" };
        if (!string.IsNullOrWhiteSpace(email)) { args.Add("--email"); args.Add(email!.Trim()); }
        var result = session.Execute("https", args, e => DeployService.Relay(e, 10, 85, progress), CancellationToken.None);
        if (!result.Ok) return new(false, result.LaunchError ?? result.FailMessage ?? $"El servidor terminó con el código {result.ExitCode}.");
        var pending = result.Value("https") != "ok";
        return new(true, pending ? "El certificado sigue pendiente." : "Certificado HTTPS emitido.", null, pending);
    }

    public static ProvisionResult Provision(ProvisionRequest req, Action<ProgressEvent> progress, CancellationToken ct)
    {
        void P(string message, Level level = Level.Normal, int percent = -1) => progress(new ProgressEvent("Aprovisionar", message, level, percent));

        if (Validate(req) is { } problem) return new(false, problem);
        var (env, secrets) = BuildEnv(req);
        var dns = req.IssueCertificate ? CheckDns(req.Domain, req.Server.Host) : null;
        if (dns is not null) P("⚠ " + dns, Level.Warning);

        var local = Path.Combine(Path.GetTempPath(), "stackdev-env-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            P("Conectando con el servidor…", Level.Title, 5);
            using var session = new RemoteSession(req.Server);
            if (session.Open(ct) is { } openError) return new(false, openError);

            File.WriteAllText(local, env.ToText(), new UTF8Encoding(false));
            if (session.Upload(local, "initial.env", 60, ct) is { } upError) return new(false, upError);
            try { File.Delete(local); } catch (IOException) { }

            var args = new List<string>
            {
                "--svc", req.Service, "--dll", req.Dll, "--domain", req.Domain, "--port", req.Port.ToString(),
                "--web", string.Equals(req.Server.Web, "nginx", StringComparison.OrdinalIgnoreCase) ? "nginx" : "apache",
                "--run-dir", session.RunDir!, "--env-file", session.RunDir + "/initial.env",
                "--https", req.IssueCertificate ? "yes" : "no",
            };
            if (!string.IsNullOrWhiteSpace(req.CertificateEmail)) { args.Add("--email"); args.Add(req.CertificateEmail!); }
            if (req.RuntimeMajor is int m) { args.Add("--runtime"); args.Add(m.ToString()); }
            if (req.Overwrite) args.Add("--force");

            var result = session.Execute("provision", args, e => DeployService.Relay(e, 10, 85, progress), CancellationToken.None, env.SecretValues());
            if (!result.Ok)
                return new(false, result.LaunchError ?? result.FailMessage ?? $"El servidor terminó con el código {result.ExitCode}.");
            var pending = result.Value("https") == "pending";
            P("Listo.", Level.Ok, 100);
            return new(true, pending
                ? "API aprovisionada. El certificado HTTPS quedó pendiente (revisa el DNS y vuelve a intentarlo)."
                : "API aprovisionada.", secrets, pending, dns);
        }
        finally
        {
            try { File.Delete(local); } catch (IOException) { }
        }
    }

    /// <summary>Installs what the checklist found missing (web server, runtime, certbot, rsync) with the server's package manager.</summary>
    public static ProvisionResult Prepare(Server server, IReadOnlyCollection<CheckFix> fixes, string web, int? runtimeMajor, Action<ProgressEvent> progress, CancellationToken ct)
    {
        var list = ServerReadiness.InstallList(fixes);
        if (list.Length == 0) return new(true, "No hay nada que instalar.");
        if (!HostKeys.IsTrusted(server)) return new(false, "El servidor aún no está verificado: prueba la conexión y confirma su huella.");
        if (fixes.Contains(CheckFix.Runtime) && runtimeMajor is null) return new(false, "No sé qué versión de ASP.NET Core instalar.");

        progress(new ProgressEvent("Preparar", "Conectando con el servidor…", Level.Title, 5));
        using var session = new RemoteSession(server);
        if (session.Open(ct) is { } openError) return new(false, openError);
        var args = new List<string> { "--install", list, "--web", web };
        if (runtimeMajor is int m) { args.Add("--runtime"); args.Add(m.ToString()); }
        var result = session.Execute("prepare", args, e => DeployService.Relay(e, 10, 85, progress), CancellationToken.None);
        if (result.LaunchError is not null) return new(false, result.LaunchError);
        if (!result.Ok)
        {
            var failed = result.Value("failed");
            var hint = failed.Contains("runtime") ? " Si tu distribución no trae ese runtime, instálalo con las instrucciones de https://learn.microsoft.com/dotnet/core/install/linux y vuelve a revisar." : "";
            return new(false, (result.FailMessage ?? $"El servidor terminó con el código {result.ExitCode}.") + hint);
        }
        progress(new ProgressEvent("Preparar", "Listo.", Level.Ok, 100));
        return new(true, "Servidor preparado.");
    }
}
