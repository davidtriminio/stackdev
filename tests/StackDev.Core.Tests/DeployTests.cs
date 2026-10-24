using System.Formats.Tar;
using System.IO.Compression;
using StackDev.Core.Deploy;
using Xunit;

namespace StackDev.Core.Tests;

public class ApiDetectorTests
{
    private const string WebProject = "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>";
    private const string LibraryProject = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>";

    private static void Write(TempFolder t, string rel, string content)
    {
        var p = t.In(rel.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    [Fact]
    public void FindsTheWebProjectAndSuggestsServiceAndDll()
    {
        using var t = new TempFolder();
        Write(t, "src/Vitrine.Api/Vitrine.Api.csproj", WebProject);
        Write(t, "src/Vitrine.Domain/Vitrine.Domain.csproj", LibraryProject);
        var found = ApiDetector.Find(t.FilePath);
        var api = Assert.Single(found);
        Assert.Equal("Vitrine.Api", api.ProjectName);
        Assert.Equal("Vitrine.Api.dll", api.Dll);
        Assert.Equal("vitrine-api", api.SuggestedService);
        Assert.Equal(9, api.RuntimeMajor);
    }

    [Fact]
    public void IgnoresTestProjectsAndBuildFolders()
    {
        using var t = new TempFolder();
        Write(t, "src/Shop.Api/Shop.Api.csproj", WebProject);
        Write(t, "tests/Shop.Api.Tests/Shop.Api.Tests.csproj", WebProject);
        Write(t, "src/Shop.Api/obj/Other.Api.csproj", WebProject);
        Write(t, "node_modules/x/Y.Api.csproj", WebProject);
        var found = ApiDetector.Find(t.FilePath);
        Assert.Single(found);
        Assert.Equal("Shop.Api", found[0].ProjectName);
    }

    [Fact]
    public void ApiNamedProjectComesBeforeOtherWebProjects()
    {
        using var t = new TempFolder();
        Write(t, "a/Admin/Admin.csproj", WebProject);
        Write(t, "b/Shop.Api/Shop.Api.csproj", WebProject);
        var found = ApiDetector.Find(t.FilePath);
        Assert.Equal(2, found.Count);
        Assert.Equal("Shop.Api", found[0].ProjectName);
    }

    [Fact]
    public void AssemblyNameOverridesTheFileName()
    {
        using var t = new TempFolder();
        Write(t, "Web/Web.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Acme.Backend</AssemblyName></PropertyGroup></Project>");
        var api = Assert.Single(ApiDetector.Find(t.FilePath));
        Assert.Equal("Acme.Backend.dll", api.Dll);
        Assert.Equal("acme-backend", api.SuggestedService);
    }

    [Fact]
    public void AcceptsTheProjectFileItself()
    {
        using var t = new TempFolder();
        Write(t, "Shop.Api.csproj", WebProject);
        Assert.Single(ApiDetector.Find(t.In("Shop.Api.csproj")));
    }

    [Theory]
    [InlineData("MiEmpresa.Api", "mi-empresa-api")]
    [InlineData("Vitrine.Api", "vitrine-api")]
    [InlineData("shop", "shop")]
    [InlineData("123.Api", "api-123-api")]
    [InlineData("...", "api")]
    public void KebabCaseIsAValidServiceName(string input, string expected)
    {
        var k = ApiDetector.ToKebab(input);
        Assert.Equal(expected, k);
        Assert.True(Sh.Service.IsMatch(k));
    }

    [Theory]
    [InlineData("net9.0", 9)]
    [InlineData("net10.0", 10)]
    [InlineData("net8.0-windows", 8)]
    public void RuntimeMajorComesFromTheFramework(string tfm, int expected) => Assert.Equal(expected, ApiDetector.RuntimeMajor(tfm));

    [Fact]
    public void OldFrameworksHaveNoRuntimeMajor()
    {
        Assert.Null(ApiDetector.RuntimeMajor("net472"));
        Assert.Null(ApiDetector.RuntimeMajor(null));
    }
}

public class SecretMaskTests
{
    [Theory]
    [InlineData("Password=hunter2;Host=db", "Password=******;Host=db")]
    [InlineData("Jwt__Key=abcdef123456", "Jwt__Key=******")]
    [InlineData("Admin__Password=\"p w d\"", "Admin__Password=\"******\"")]
    [InlineData("token: abc123xyz", "token: ******")]
    [InlineData("postgres://user:s3cret@host/db", "postgres://user:******@host/db")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.abc", "Authorization: Bearer ******")]
    public void HidesSecrets(string input, string expected) => Assert.Equal(expected, SecretMask.Mask(input));

    [Fact]
    public void LeavesOrdinaryTextAlone()
    {
        Assert.Equal("Restarting demo-api on port 5001", SecretMask.Mask("Restarting demo-api on port 5001"));
        Assert.Equal("", SecretMask.Mask(null));
    }

    [Fact]
    public void HidesPrivateKeyBlocks()
    {
        var text = "before\n-----BEGIN OPENSSH PRIVATE KEY-----\nAAAAB3Nza\n-----END OPENSSH PRIVATE KEY-----\nafter";
        var masked = SecretMask.Mask(text);
        Assert.DoesNotContain("AAAAB3Nza", masked);
        Assert.Contains("before", masked);
        Assert.Contains("after", masked);
    }

    [Fact]
    public void HidesKnownValuesEverywhere()
    {
        var masked = SecretMask.Mask("the value sup3rS3cr3t appears twice: sup3rS3cr3t", new[] { "sup3rS3cr3t" });
        Assert.DoesNotContain("sup3rS3cr3t", masked);
    }

    [Theory]
    [InlineData("Jwt__Key", true)]
    [InlineData("Admin__Password", true)]
    [InlineData("ConnectionStrings__Default", true)]
    [InlineData("API_TOKEN", true)]
    [InlineData("ASPNETCORE_URLS", false)]
    [InlineData("Cors__AllowedOrigins__0", false)]
    [InlineData("Storage__UploadsPath", false)]
    public void KnowsWhichNamesAreSecret(string name, bool secret) => Assert.Equal(secret, SecretMask.IsSecretName(name));
}

public class PublishGuardTests
{
    private static void Touch(TempFolder t, string rel, string content = "x")
    {
        var p = t.In(rel.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    [Fact]
    public void BlocksFilesThatMustNotBeUploaded()
    {
        using var t = new TempFolder();
        foreach (var f in new[] { ".env", ".env.production", "appsettings.Development.json", "App.pdb", "id_rsa", "server.pfx", "local.db", "secrets.json" })
            Touch(t, f);
        Touch(t, "App.dll");
        var blocked = PublishGuard.Scan(t.FilePath).Where(f => f.Level == GuardLevel.Block).Select(f => f.RelativePath).ToList();
        Assert.Equal(8, blocked.Count);
        Assert.True(!blocked.Contains("App.dll"));
    }

    [Fact]
    public void WarnsAboutLogsAndUploadedFiles()
    {
        using var t = new TempFolder();
        Touch(t, "debug.log");
        Touch(t, "wwwroot/uploads/a.png");
        var findings = PublishGuard.Scan(t.FilePath);
        Assert.Equal(2, findings.Count);
        Assert.True(findings.All(f => f.Level == GuardLevel.Warn));
    }

    [Fact]
    public void WarnsAboutRealLookingSecretsInSettings()
    {
        using var t = new TempFolder();
        Touch(t, "appsettings.json", "{ \"Jwt\": { \"Key\": \"kH8s7dfh2398sdhf\" }, \"ConnectionStrings\": { \"Default\": \"Host=db;Password=Sup3rS3cret\" } }");
        var findings = PublishGuard.Scan(t.FilePath);
        Assert.True(findings.Count >= 2);
        Assert.True(findings.All(f => f.Level == GuardLevel.Warn));
    }

    [Fact]
    public void IgnoresPlaceholdersInSettings()
    {
        using var t = new TempFolder();
        Touch(t, "appsettings.json", "{ \"Jwt\": { \"Key\": \"CHANGE_ME_IN_ENV\" }, \"ConnectionStrings\": { \"Default\": \"Host=db;Password=CHANGEME\" } }");
        Assert.Empty(PublishGuard.Scan(t.FilePath));
    }

    [Fact]
    public void RemovesOnlyTheBlockedFilesAndNeverLeavesTheFolder()
    {
        using var outer = new TempFolder();
        var pub = outer.In("pub");
        Directory.CreateDirectory(pub);
        File.WriteAllText(Path.Combine(pub, ".env"), "A=1");
        File.WriteAllText(Path.Combine(pub, "App.dll"), "x");
        File.WriteAllText(outer.In("victim.txt"), "keep");
        var findings = new[]
        {
            new GuardFinding(GuardLevel.Block, ".env", "test"),
            new GuardFinding(GuardLevel.Block, "../victim.txt", "escape attempt"),
            new GuardFinding(GuardLevel.Warn, "App.dll", "warn only"),
        };
        var removed = PublishGuard.RemoveBlocked(pub, findings);
        Assert.Equal(1, removed.Count);
        Assert.True(!File.Exists(Path.Combine(pub, ".env")));
        Assert.True(File.Exists(Path.Combine(pub, "App.dll")));
        Assert.True(File.Exists(outer.In("victim.txt")));
    }
}

public class EnvFileTests
{
    [Fact]
    public void RoundTripKeepsCommentsAndOrder()
    {
        var text = "# header\nASPNETCORE_ENVIRONMENT=Production\n\nJwt__Key=abc\n";
        Assert.Equal(text, EnvFile.Parse(text).ToText());
    }

    [Fact]
    public void ReadsQuotedValues()
    {
        var env = EnvFile.Parse("A=\"hello world\"\nB='single'\nC=plain\nD=\"say \\\"hi\\\"\"\n");
        Assert.Equal("hello world", env.Get("A"));
        Assert.Equal("single", env.Get("B"));
        Assert.Equal("plain", env.Get("C"));
        Assert.Equal("say \"hi\"", env.Get("D"));
    }

    [Fact]
    public void QuotesWhatNeedsQuotesWhenWriting()
    {
        var env = new EnvFile();
        env.Set("PLAIN", "abc123");
        env.Set("SPACED", "two words");
        env.Set("QUOTE", "say \"hi\"");
        env.Set("HASH", "a#b");
        var round = EnvFile.Parse(env.ToText());
        Assert.Equal("abc123", round.Get("PLAIN"));
        Assert.Equal("two words", round.Get("SPACED"));
        Assert.Equal("say \"hi\"", round.Get("QUOTE"));
        Assert.Equal("a#b", round.Get("HASH"));
    }

    [Fact]
    public void SetReplacesAddsAndRemoves()
    {
        var env = EnvFile.Parse("A=1\nB=2\n");
        env.Set("A", "10");
        env.Set("C", "3");
        Assert.True(env.Remove("B"));
        Assert.False(env.Remove("B"));
        Assert.Equal("A=10\nC=3\n", env.ToText());
    }

    [Fact]
    public void RejectsNamesAndValuesThatWouldCorruptTheFile()
    {
        var env = new EnvFile();
        Assert.Throws<ArgumentException>(() => env.Set("bad name", "x"));
        Assert.Throws<ArgumentException>(() => env.Set("1ABC", "x"));
        Assert.Throws<ArgumentException>(() => env.Set("OK", "line1\nline2"));
    }

    [Fact]
    public void ShowsSecretsMasked()
    {
        Assert.Equal(SecretMask.Hidden, EnvFile.Display("Jwt__Key", "abcdef"));
        Assert.Equal("Production", EnvFile.Display("ASPNETCORE_ENVIRONMENT", "Production"));
        Assert.Equal("", EnvFile.Display("Jwt__Key", ""));
    }

    [Fact]
    public void CollectsSecretValuesForTheLogMask()
    {
        var env = EnvFile.Parse("Jwt__Key=abcdef123\nASPNETCORE_URLS=http://127.0.0.1:5000\n");
        Assert.Equal(new[] { "abcdef123" }, env.SecretValues().ToArray());
    }
}

public class RemoteProtocolTests
{
    [Fact]
    public void ParsesEveryKindOfLine()
    {
        var step = RemoteProtocol.Parse("##STEP 3/6 Stopping demo");
        Assert.Equal(RemoteEventKind.Step, step.Kind);
        Assert.Equal(3, step.Step);
        Assert.Equal(6, step.Total);
        Assert.Equal("Stopping demo", step.Text);

        var fail = RemoteProtocol.Parse("##FAIL 20 It broke");
        Assert.Equal(RemoteEventKind.Fail, fail.Kind);
        Assert.Equal(20, fail.Code);
        Assert.Equal("It broke", fail.Text);

        var kv = RemoteProtocol.Parse("##KV port=5001");
        Assert.Equal("port", kv.Key);
        Assert.Equal("5001", kv.Text);

        Assert.Equal(RemoteEventKind.Info, RemoteProtocol.Parse("##INFO hello").Kind);
        Assert.Equal(RemoteEventKind.Warn, RemoteProtocol.Parse("##WARN careful").Kind);
        Assert.Equal(RemoteEventKind.Done, RemoteProtocol.Parse("##DONE").Kind);
        Assert.Equal(RemoteEventKind.EnvBegin, RemoteProtocol.Parse("##ENV-BEGIN").Kind);
        Assert.Equal(RemoteEventKind.Log, RemoteProtocol.Parse("plain output").Kind);
        Assert.Equal(RemoteEventKind.Log, RemoteProtocol.Parse("##STEP nonsense").Kind);
    }

    [Fact]
    public void ParsesApisAndBackups()
    {
        var api = RemoteApi.Parse("vitrine-api|Vitrine.Api.dll|/var/www/vitrine-api|vitrine-api|active");
        Assert.NotNull(api);
        Assert.Equal("Vitrine.Api.dll", api!.Dll);
        var b = RemoteBackup.Parse("20261022-101500|2026-10-22 10:15:00|2048");
        Assert.Equal("20261022-101500", b!.Id);
        Assert.Equal(2048L, b.SizeKb);
        Assert.Equal("", api.Domain);
        Assert.Equal("api.midominio.com", RemoteApi.Parse("a|A.dll|/var/www/a|a|active|api.midominio.com")!.Domain);
        Assert.Null(RemoteApi.Parse("broken"));
    }
}

public class ShellQuotingTests
{
    [Theory]
    [InlineData("simple", "'simple'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("a b; rm -rf /", "'a b; rm -rf /'")]
    [InlineData("$(whoami)", "'$(whoami)'")]
    public void QuotesForPosixShell(string input, string expected) => Assert.Equal(expected, Sh.Quote(input));

    [Theory]
    [InlineData("demo-api", true)]
    [InlineData("Demo", false)]
    [InlineData("a;b", false)]
    [InlineData("1abc", false)]
    [InlineData("../x", false)]
    public void ServiceNamesAreRestricted(string name, bool ok) => Assert.Equal(ok, Sh.Service.IsMatch(name));

    [Theory]
    [InlineData("Demo.Api.dll", true)]
    [InlineData("x.exe", false)]
    [InlineData("a b.dll", false)]
    public void DllNamesAreRestricted(string name, bool ok) => Assert.Equal(ok, Sh.Dll.IsMatch(name));

    [Theory]
    [InlineData("api.example.com", true)]
    [InlineData("example", false)]
    [InlineData("a_b.com", false)]
    [InlineData("https://x.com", false)]
    public void DomainsAreRestricted(string name, bool ok) => Assert.Equal(ok, Sh.Domain.IsMatch(name));
}

public class DeployPiecesTests
{
    [Fact]
    public void PackPutsTheFilesAtTheRootOfTheArchive()
    {
        using var t = new TempFolder();
        Directory.CreateDirectory(t.In("pub", "wwwroot"));
        File.WriteAllText(t.In("pub", "App.dll"), "dll");
        File.WriteAllText(t.In("pub", "wwwroot", "index.html"), "<html>");
        var bundle = t.In("bundle.tgz");
        DeployService.Pack(t.In("pub"), bundle);

        var extracted = t.In("out");
        Directory.CreateDirectory(extracted);
        using (var f = File.OpenRead(bundle))
        using (var gz = new GZipStream(f, CompressionMode.Decompress))
            TarFile.ExtractToDirectory(gz, extracted, overwriteFiles: true);
        Assert.Equal("dll", File.ReadAllText(Path.Combine(extracted, "App.dll")));
        Assert.Equal("<html>", File.ReadAllText(Path.Combine(extracted, "wwwroot", "index.html")));
    }

    [Fact]
    public void Sha256MatchesTheKnownValue()
    {
        using var t = new TempFolder();
        File.WriteAllText(t.In("abc.txt"), "abc");
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", DeployService.Sha256(t.In("abc.txt")));
    }

    [Fact]
    public void ReadsTheRuntimeFromRuntimeConfig()
    {
        using var t = new TempFolder();
        File.WriteAllText(t.In("Demo.Api.runtimeconfig.json"),
            "{ \"runtimeOptions\": { \"tfm\": \"net9.0\", \"frameworks\": [ { \"name\": \"Microsoft.NETCore.App\", \"version\": \"9.0.0\" }, { \"name\": \"Microsoft.AspNetCore.App\", \"version\": \"9.0.0\" } ] } }");
        Assert.Equal(9, DeployService.ReadRuntimeMajor(t.FilePath, "Demo.Api.dll"));
        Assert.Null(DeployService.ReadRuntimeMajor(t.FilePath, "Missing.dll"));
    }

    [Fact]
    public void TheServerScriptIsEmbeddedAndUsesUnixLineEndings()
    {
        var script = RemoteSession.LoadScript();
        Assert.StartsWith("#!/usr/bin/env bash", script);
        Assert.Contains("cmd_deploy", script);
        Assert.False(script.Contains('\r'));
    }
}

public class ProvisionTests
{
    private static ProvisionRequest Request(Action<ProvisionRequestOverrides>? tweak = null) => new()
    {
        Server = new StackDev.Core.Servers.Server { Name = "t", Host = "203.0.113.10" },
        Service = "demo-api",
        Dll = "Demo.Api.dll",
        Domain = "demo.example.com",
        Port = 5010,
        FrontendOrigin = "https://app.example.com/",
    };

    public sealed class ProvisionRequestOverrides { }

    [Fact]
    public void BuildsAnEnvWithFreshSecrets()
    {
        var (env, secrets) = ProvisionService.BuildEnv(Request());
        Assert.Equal("Production", env.Get("ASPNETCORE_ENVIRONMENT"));
        Assert.Equal("http://127.0.0.1:5010", env.Get("ASPNETCORE_URLS"));
        Assert.Equal("Data Source=/var/lib/demo-api/demo-api.db", env.Get("ConnectionStrings__Default"));
        Assert.Equal("/var/lib/demo-api/uploads", env.Get("Storage__UploadsPath"));
        Assert.Equal("https://app.example.com", env.Get("Cors__AllowedOrigins__0"));
        Assert.Equal(secrets.JwtKey, env.Get("Jwt__Key"));
        Assert.Equal(secrets.AdminPassword, env.Get("Admin__Password"));
        Assert.True(secrets.JwtKey.Length >= 48);
        Assert.True(secrets.AdminPassword.Length >= 16);
        var (_, other) = ProvisionService.BuildEnv(Request());
        Assert.NotEqual(secrets.JwtKey, other.JwtKey);
    }

    [Theory]
    [InlineData("Bad Name", "demo.example.com", 5010)]
    [InlineData("demo-api", "not a domain", 5010)]
    [InlineData("demo-api", "demo.example.com", 80)]
    public void RejectsInvalidInput(string service, string domain, int port)
    {
        var r = new ProvisionRequest
        {
            Server = new StackDev.Core.Servers.Server { Name = "t", Host = "203.0.113.10" },
            Service = service, Dll = "Demo.Api.dll", Domain = domain, Port = port,
        };
        Assert.NotNull(ProvisionService.Validate(r));
    }
}
