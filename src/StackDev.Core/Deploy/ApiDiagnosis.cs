using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using StackDev.Core.Servers;

namespace StackDev.Core.Deploy;

public enum IssueKind { ServiceDown, PortsBlocked, DnsProblem, NoSiteForDomain, CertificateExpired, NoAnswer }

/// <summary>One thing that is wrong with the public address of an API, in plain words, and whether StackDev can repair it by itself.</summary>
public sealed record DiagnosisIssue(IssueKind Kind, string Title, string Detail, bool Fixable, string? FixLabel = null);

/// <summary>What was checked (one line each, with what was found) and what is wrong.</summary>
public sealed record Diagnosis(IReadOnlyList<string> Facts, IReadOnlyList<DiagnosisIssue> Issues)
{
    public bool Healthy => Issues.Count == 0;
}

/// <summary>What was observed from here; <see cref="ApiDiagnosis.Decide"/> turns it into issues. Null means "could not be observed".</summary>
public sealed record DiagnosisInput(
    string Domain, string ServerHost, bool? ServiceActive, string? PortsProblem, string? DnsProblem,
    IReadOnlyList<string>? CertNames, DateTime? CertExpires, string? TlsError, int? HttpStatus);

/// <summary>
/// Looks at an API from the outside the way a visitor does (DNS, ports, certificate, answer) plus the service state,
/// and says what is wrong and what can be repaired with one click. Never changes anything by itself.
/// </summary>
public static class ApiDiagnosis
{
    /// <summary>Whether a certificate with these names (DNS names, possibly with a leading *.) is valid for <paramref name="domain"/>.</summary>
    public static bool CoversDomain(IEnumerable<string> names, string domain)
    {
        domain = domain.Trim().ToLowerInvariant();
        foreach (var raw in names)
        {
            var n = raw.Trim().ToLowerInvariant();
            if (n == domain) return true;
            if (n.StartsWith("*.", StringComparison.Ordinal))
            {
                var rest = n[1..];   // ".example.com"
                var dot = domain.IndexOf('.');
                if (dot > 0 && domain[dot..] == rest) return true;
            }
        }
        return false;
    }

    public static Diagnosis Decide(DiagnosisInput i)
    {
        var facts = new List<string>();
        var issues = new List<DiagnosisIssue>();

        if (i.ServiceActive is bool active)
        {
            facts.Add(active ? "✔ El servicio está activo en el servidor." : "✘ El servicio está detenido.");
            if (!active) issues.Add(new(IssueKind.ServiceDown, "El servicio está detenido", "La API no está corriendo en el servidor.", true, "Iniciar el servicio"));
        }
        else facts.Add("· No pude consultar el estado del servicio.");

        if (i.PortsProblem is not null)
        {
            facts.Add("✘ Los puertos 80/443 no responden desde internet.");
            issues.Add(new(IssueKind.PortsBlocked, "El servidor no es alcanzable desde internet", i.PortsProblem, false));
        }
        else facts.Add("✔ Los puertos 80 y 443 responden.");

        if (i.DnsProblem is not null)
        {
            facts.Add($"✘ El DNS de {i.Domain} no apunta a {i.ServerHost}.");
            issues.Add(new(IssueKind.DnsProblem, "El dominio no apunta al servidor", i.DnsProblem, false));
        }
        else facts.Add($"✔ {i.Domain} apunta a {i.ServerHost}.");

        var tlsOk = false;
        if (i.CertNames is { Count: > 0 })
        {
            var covers = CoversDomain(i.CertNames, i.Domain);
            var shown = string.Join(", ", i.CertNames);
            if (!covers)
            {
                facts.Add($"✘ El certificado que responde es para «{shown}», no para {i.Domain}.");
                issues.Add(new(IssueKind.NoSiteForDomain, "El servidor no tiene un sitio web para este dominio",
                    $"Al abrir {i.Domain} el servidor contesta con el sitio de «{shown}»" + (i.HttpStatus is int s ? $" (código {s})" : "")
                    + ". La API corre, pero ningún sitio web la publica con este nombre: hay que crear el sitio y su certificado.", true, "Crear el sitio y el certificado"));
            }
            else if (i.CertExpires is DateTime exp && exp < DateTime.UtcNow)
            {
                facts.Add($"✘ El certificado venció el {exp:yyyy-MM-dd}.");
                issues.Add(new(IssueKind.CertificateExpired, "El certificado HTTPS venció", "Hay que pedir uno nuevo.", true, "Renovar el certificado"));
            }
            else
            {
                tlsOk = true;
                facts.Add($"✔ Certificado válido para {i.Domain}" + (i.CertExpires is DateTime e ? $" (vence {e:yyyy-MM-dd})." : "."));
            }
        }
        else if (i.TlsError is not null)
        {
            facts.Add("✘ No se pudo abrir HTTPS: " + i.TlsError);
            if (i.PortsProblem is null && i.DnsProblem is null)
                issues.Add(new(IssueKind.NoSiteForDomain, "HTTPS no está disponible para este dominio", i.TlsError + ". Puede faltar el sitio web o el certificado.", true, "Crear el sitio y el certificado"));
        }

        if (i.HttpStatus is int code)
        {
            facts.Add(code is >= 200 and < 400 ? $"✔ La dirección responde ({code})."
                : code == 404 && tlsOk ? "✔ La dirección responde; «/» da 404, normal si la API no tiene página principal."
                : code is 401 or 403 ? $"✔ La dirección responde pero pide acceso ({code})."
                : $"⚠ La dirección responde con el código {code}.");
            if (code >= 500) issues.Add(new(IssueKind.NoAnswer, "La API responde con un error del servidor", $"Código {code}. Mira «Logs en vivo» del servicio.", false));
        }
        else if (issues.Count == 0)
            issues.Add(new(IssueKind.NoAnswer, "La dirección no responde", "Ni el DNS, ni los puertos, ni el certificado explican el fallo; revisa «Logs en vivo».", false));

        return new(facts, issues);
    }

    /// <summary>Gathers the observations (a few seconds, every step has its own limit) and decides.</summary>
    public static Diagnosis Run(Server server, string domain, ApiStatus? status, CancellationToken ct = default)
    {
        string? ports = null, dns = null, tlsError = null;
        IReadOnlyList<string>? names = null; DateTime? expires = null; int? http = null;
        ct.ThrowIfCancellationRequested();
        ports = ProvisionService.CheckPorts(server.Host);
        dns = ProvisionService.CheckDns(domain, server.Host);
        ct.ThrowIfCancellationRequested();
        (names, expires, tlsError) = ReadCertificate(domain);
        http = Request(domain);
        return Decide(new(domain, server.Host, status is null || status.Problem is not null ? null : status.Active, ports, dns, names, expires, tlsError, http));
    }

    private static (IReadOnlyList<string>? Names, DateTime? Expires, string? Error) ReadCertificate(string domain)
    {
        try
        {
            using var tcp = new TcpClient();
            if (!tcp.ConnectAsync(domain, 443).Wait(5000)) return (null, null, "no hubo respuesta en el puerto 443 (5 s)");
            X509Certificate2? cert = null;
            using var ssl = new SslStream(tcp.GetStream(), false, (_, c, _, _) => { if (c is not null) cert = new X509Certificate2(c); return true; });
            ssl.ReadTimeout = ssl.WriteTimeout = 5000;
            ssl.AuthenticateAsClient(domain);
            if (cert is null) return (null, null, "el servidor no presentó certificado");
            return (CertificateNames(cert), cert.NotAfter.ToUniversalTime(), null);
        }
        catch (Exception ex) when (ex is AggregateException or SocketException or IOException or System.Security.Authentication.AuthenticationException or TimeoutException or ObjectDisposedException)
        {
            return (null, null, (ex.InnerException ?? ex).Message);
        }
    }

    private static List<string> CertificateNames(X509Certificate2 cert)
    {
        var names = new List<string>();
        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is not null) names.AddRange(san.EnumerateDnsNames());
        if (names.Count == 0 && cert.GetNameInfo(X509NameType.DnsName, false) is { Length: > 0 } cn) names.Add(cn);
        return names;
    }

    private static int? Request(string domain)
    {
        try
        {
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            using var r = http.GetAsync($"https://{domain}/").GetAwaiter().GetResult();
            return (int)r.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) { return null; }
    }
}
