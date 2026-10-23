using StackDev.Core.Deploy;
using Xunit;

namespace StackDev.Core.Tests;

public class ReadinessTests
{
    private static Dictionary<string, string> Facts(string web = "apache", string runtimes = "10", string certbot = "yes", string rsync = "yes", string free = "5000", string exists = "no") => new()
    {
        ["os"] = "Oracle Linux 9", ["web"] = web, ["runtimes"] = runtimes, ["certbot"] = certbot, ["rsync"] = rsync, ["free_mb"] = free, ["service_exists"] = exists,
    };

    private static List<CheckItem> Eval(Dictionary<string, string> f, string wanted = "apache", int? runtime = 10, string? domain = "api.x.com", string? dns = null, bool https = true) =>
        ServerReadiness.Evaluate(new(f, wanted, runtime, domain, dns, https));

    private static CheckItem Item(List<CheckItem> l, string id) => l.Single(c => c.Id == id);

    [Fact]
    public void A_ready_server_has_nothing_to_fix()
    {
        var l = Eval(Facts());
        Assert.True(ServerReadiness.CanContinue(l));
        Assert.Empty(ServerReadiness.Fixable(l));
        foreach (var c in l) Assert.Equal(CheckLevel.Ok, c.Level);
    }

    [Fact]
    public void Missing_web_server_is_an_error_that_can_be_installed()
    {
        var l = Eval(Facts(web: "none"), wanted: "nginx");
        Assert.False(ServerReadiness.CanContinue(l));
        Assert.Equal(CheckFix.WebNginx, Item(l, "web").Fix);
        Assert.Equal(CheckLevel.Error, Item(l, "web").Level);
    }

    [Fact]
    public void The_installed_web_server_wins_over_the_chosen_one()
    {
        Assert.Equal("nginx", ServerReadiness.RecommendedWeb(Facts(web: "nginx"), "apache"));
        Assert.Equal("apache", ServerReadiness.RecommendedWeb(Facts(web: "none"), "apache"));
        Assert.Equal("nginx", ServerReadiness.RecommendedWeb(Facts(web: "none"), "nginx"));
        Assert.Contains("se usará Nginx", Item(Eval(Facts(web: "nginx"), wanted: "apache"), "web").Detail);
    }

    [Fact]
    public void Missing_runtime_names_the_version_needed()
    {
        var c = Item(Eval(Facts(runtimes: "8,9"), runtime: 10), "runtime");
        Assert.Equal(CheckLevel.Error, c.Level);
        Assert.Equal(CheckFix.Runtime, c.Fix);
        Assert.Contains("10", c.Detail);
        Assert.Contains("8, 9", c.Detail);
    }

    [Fact]
    public void Unknown_runtime_need_is_only_a_warning_when_none_is_installed()
    {
        Assert.Equal(CheckLevel.Warn, Item(Eval(Facts(runtimes: ""), runtime: null), "runtime").Level);
        Assert.Equal(CheckLevel.Ok, Item(Eval(Facts(runtimes: "9"), runtime: null), "runtime").Level);
    }

    [Fact]
    public void Certbot_is_only_checked_when_https_is_wanted()
    {
        Assert.DoesNotContain(Eval(Facts(certbot: "no"), https: false), c => c.Id == "certbot");
        var c = Item(Eval(Facts(certbot: "no")), "certbot");
        Assert.Equal(CheckLevel.Warn, c.Level);
        Assert.Equal(CheckFix.Certbot, c.Fix);
    }

    [Fact]
    public void Dns_and_domain_are_warnings_never_blockers()
    {
        Assert.Equal(CheckLevel.Warn, Item(Eval(Facts(), domain: null), "dns").Level);
        Assert.Equal(CheckLevel.Warn, Item(Eval(Facts(), dns: "apunta a otra IP"), "dns").Level);
        Assert.True(ServerReadiness.CanContinue(Eval(Facts(), domain: null)));
    }

    [Theory]
    [InlineData("100", CheckLevel.Error)]
    [InlineData("500", CheckLevel.Warn)]
    [InlineData("4000", CheckLevel.Ok)]
    public void Disk_space_levels(string free, CheckLevel level) => Assert.Equal(level, Item(Eval(Facts(free: free)), "disk").Level);

    [Fact]
    public void Existing_service_name_is_flagged()
    {
        Assert.Equal(CheckLevel.Warn, Item(Eval(Facts(exists: "yes")), "exists").Level);
    }

    [Fact]
    public void Install_list_has_each_fix_once_in_script_names()
    {
        var l = Eval(Facts(web: "none", runtimes: "", certbot: "no", rsync: "no"));
        var fixes = ServerReadiness.Fixable(l);
        Assert.Equal("web-apache,runtime,certbot,rsync", ServerReadiness.InstallList(fixes));
        Assert.Equal("web-apache,runtime", ServerReadiness.InstallList(ServerReadiness.Fixable(l, errorsOnly: true)));
    }

    [Theory]
    [InlineData("appsettings.Development.json", true)]
    [InlineData("sub/appsettings.development.json", true)]
    [InlineData("Api.pdb", true)]
    [InlineData(".env", false)]
    [InlineData("id_rsa", false)]
    [InlineData("data.db", false)]
    [InlineData("appsettings.json", false)]
    public void Routine_publish_files(string path, bool routine) => Assert.Equal(routine, PublishGuard.IsRoutine(path));
}

public class RemoteTextTests
{
    [Theory]
    [InlineData("Installing web-apache", "Instalando Apache")]
    [InlineData("web-nginx installed", "Nginx instalado")]
    [InlineData("Stopping demo-api", "Deteniendo demo-api")]
    [InlineData("Checksum OK", "Suma de verificación correcta")]
    [InlineData("Port 5050 is already in use or assigned to another site", "El puerto 5050 ya está en uso o asignado a otro sitio")]
    [InlineData("Not enough disk space (need about 300 MB, 120 MB free)", "No hay espacio suficiente en disco (hacen falta unos 300 MB y hay 120 MB libres)")]
    [InlineData("Could not install: runtime,rsync", "No se pudo instalar: runtime,rsync")]
    public void Known_messages_are_translated(string english, string spanish) => Assert.Equal(spanish, RemoteText.Translate(english));

    [Fact]
    public void Unknown_messages_are_left_as_they_are() => Assert.Equal("something new", RemoteText.Translate("something new"));

    [Fact]
    public void Every_message_of_the_script_has_a_translation()
    {
        var script = RemoteSession.LoadScript();
        var found = System.Text.RegularExpressions.Regex.Matches(script, "(?:step|info|warn|fail [0-9]+) +\"([^\"]*)\"");
        Assert.True(found.Count > 80);
        var missing = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in found)
        {
            var sample = m.Groups[1].Value.Replace("$item", "rsync");
            sample = System.Text.RegularExpressions.Regex.Replace(sample, @"\$\{[^}]*\}|\$\([^)]*\)?|\$[A-Za-z_]+", "1");
            sample = sample.Replace("$1", "1");
            if (sample.StartsWith("--") || RemoteText.Translate(sample) != sample) continue;
            if (sample == "Done" || sample == "Checksum OK") continue;
            missing.Add(sample);
        }
        Assert.Empty(missing);
    }
}

public class AutomaticSetupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-hints-" + Guid.NewGuid().ToString("N")[..8]);
    public AutomaticSetupTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Project(string csprojBody, string? settings = null)
    {
        var path = Path.Combine(_dir, "Api.csproj");
        File.WriteAllText(path, $"<Project Sdk=\"Microsoft.NET.Sdk.Web\"><ItemGroup>{csprojBody}</ItemGroup></Project>");
        if (settings is not null) File.WriteAllText(Path.Combine(_dir, "appsettings.json"), settings);
        return path;
    }

    [Fact]
    public void Auto_domain_points_at_the_server_ip()
    {
        Assert.Equal("shop-api.203-0-113-10.sslip.io", ProvisionService.AutoDomain("shop-api", "203.0.113.10"));
        Assert.Null(ProvisionService.AutoDomain("api", "servidor.midominio.com"));
        Assert.Null(ProvisionService.AutoDomain("Bad_Name", "1.2.3.4"));
        Assert.Matches(Sh.Domain.ToString(), ProvisionService.AutoDomain("shop", "10.0.0.5")!);
    }

    [Fact]
    public void Postgres_package_selects_postgres() =>
        Assert.Equal(DatabaseKind.Postgres, ProjectHints.DetectDatabase(Project("<PackageReference Include=\"Npgsql.EntityFrameworkCore.PostgreSQL\" Version=\"9.0.0\" />")).Kind);

    [Fact]
    public void Sqlite_package_selects_sqlite() =>
        Assert.Equal(DatabaseKind.Sqlite, ProjectHints.DetectDatabase(Project("<PackageReference Include=\"Microsoft.EntityFrameworkCore.Sqlite\" Version=\"9.0.0\" />")).Kind);

    [Fact]
    public void Other_engines_are_reported_as_none_with_the_reason()
    {
        var hint = ProjectHints.DetectDatabase(Project("<PackageReference Include=\"Microsoft.EntityFrameworkCore.SqlServer\" Version=\"9.0.0\" />"));
        Assert.Equal(DatabaseKind.None, hint.Kind);
        Assert.Contains("Variables", hint.Reason);
    }

    [Fact]
    public void Connection_string_is_used_when_packages_say_nothing()
    {
        Assert.Equal(DatabaseKind.Postgres, ProjectHints.DetectDatabase(Project("", "{\"ConnectionStrings\":{\"Default\":\"Host=localhost;Database=x;Username=u\"}}")).Kind);
        Assert.Equal(DatabaseKind.Sqlite, ProjectHints.DetectDatabase(Project("", "{\"ConnectionStrings\":{\"Default\":\"Data Source=app.db\"}}")).Kind);
        Assert.Equal(DatabaseKind.Sqlite, ProjectHints.DetectDatabase(Project("")).Kind);
    }

    [Fact]
    public void Port_problem_is_a_warning_only_when_https_is_wanted()
    {
        var facts = new Dictionary<string, string> { ["web"] = "apache", ["runtimes"] = "10", ["certbot"] = "yes", ["rsync"] = "yes", ["free_mb"] = "5000" };
        var withHttps = ServerReadiness.Evaluate(new(facts, "apache", 10, "a.b.com", null, true, "puertos cerrados"));
        Assert.Equal(CheckLevel.Warn, withHttps.Single(c => c.Id == "ports").Level);
        Assert.True(ServerReadiness.CanContinue(withHttps));
        Assert.DoesNotContain(ServerReadiness.Evaluate(new(facts, "apache", 10, "a.b.com", null, false, "puertos cerrados")), c => c.Id == "ports");
        Assert.DoesNotContain(ServerReadiness.Evaluate(new(facts, "apache", 10, "a.b.com", null, true, null)), c => c.Id == "ports");
    }
}
