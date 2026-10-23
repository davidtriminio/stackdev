using StackDev.Core.Deploy;
using Xunit;

namespace StackDev.Core.Tests;

public class ApiDiagnosisTests
{
    private const string Domain = "commerce-hub-api.example.dev";

    private static DiagnosisInput Input(bool? active = true, string? ports = null, string? dns = null, string[]? names = null,
        DateTime? expires = null, string? tlsError = null, int? http = 200)
        => new(Domain, "129.213.10.144", active, ports, dns, names, expires ?? DateTime.UtcNow.AddDays(60), tlsError, http);

    [Theory]
    [InlineData("api.example.com", "api.example.com", true)]
    [InlineData("API.Example.com", "api.example.com", true)]
    [InlineData("*.example.com", "api.example.com", true)]
    [InlineData("*.example.com", "a.b.example.com", false)]
    [InlineData("*.example.com", "example.com", false)]
    [InlineData("vitrine-api.example.dev", "commerce-hub-api.example.dev", false)]
    public void CoversDomain_follows_certificate_name_rules(string name, string domain, bool expected)
        => Assert.Equal(expected, ApiDiagnosis.CoversDomain(new[] { name }, domain));

    [Fact]
    public void Everything_fine_has_no_issues()
        => Assert.True(ApiDiagnosis.Decide(Input(names: new[] { Domain })).Healthy);

    [Fact]
    public void A_404_at_the_root_with_a_valid_certificate_is_not_a_problem()
    {
        var d = ApiDiagnosis.Decide(Input(names: new[] { Domain }, http: 404));
        Assert.True(d.Healthy);
        Assert.Contains(d.Facts, f => f.Contains("404") && f.Contains("normal"));
    }

    [Fact]
    public void A_certificate_for_another_name_means_no_site_and_can_be_repaired()
    {
        // what the real server did: DNS fine, ports fine, but the certificate belonged to another API and every path gave 404
        var d = ApiDiagnosis.Decide(Input(names: new[] { "vitrine-api.example.dev" }, http: 404));
        var issue = Assert.Single(d.Issues);
        Assert.Equal(IssueKind.NoSiteForDomain, issue.Kind);
        Assert.True(issue.Fixable);
        Assert.Contains("vitrine-api.example.dev", issue.Detail);
    }

    [Fact]
    public void A_stopped_service_is_reported_first_and_can_be_started()
    {
        var d = ApiDiagnosis.Decide(Input(active: false, names: new[] { Domain }, http: 502));
        Assert.Equal(IssueKind.ServiceDown, d.Issues[0].Kind);
        Assert.True(d.Issues[0].Fixable);
    }

    [Fact]
    public void Dns_and_ports_problems_are_reported_but_not_repaired_here()
    {
        var d = ApiDiagnosis.Decide(Input(ports: "El puerto 443 no responde", dns: "no apunta", names: null, tlsError: "timeout", http: null));
        Assert.Contains(d.Issues, i => i.Kind == IssueKind.PortsBlocked && !i.Fixable);
        Assert.Contains(d.Issues, i => i.Kind == IssueKind.DnsProblem && !i.Fixable);
        Assert.DoesNotContain(d.Issues, i => i.Kind == IssueKind.NoSiteForDomain);   // the cause is upstream, so no site repair is offered
    }

    [Fact]
    public void An_expired_certificate_can_be_renewed()
    {
        var d = ApiDiagnosis.Decide(Input(names: new[] { Domain }, expires: DateTime.UtcNow.AddDays(-2)));
        Assert.Equal(IssueKind.CertificateExpired, Assert.Single(d.Issues).Kind);
    }

    [Fact]
    public void A_server_error_is_reported_as_not_repairable()
    {
        var d = ApiDiagnosis.Decide(Input(names: new[] { Domain }, http: 500));
        Assert.False(Assert.Single(d.Issues).Fixable);
    }
}
