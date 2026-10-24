using System.Text.Json;
using StackDev.Core.Config;
using StackDev.Core.Credentials;
using StackDev.Core.Servers;
using Xunit;

namespace StackDev.Core.Tests;

public class LegacyKeysTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-legacy-" + Guid.NewGuid().ToString("N")[..8]);

    public LegacyKeysTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Project_config_with_spanish_keys_is_read()
    {
        var cfg = ConfigJson.Read("""
            { "nombre": "demo", "navegador": "edge", "despliegue": { "servidor": "ab12", "servicio": "demo-api", "dominio": "api.demo.com" },
              "api": { "carpeta": "backend", "iniciar": "dotnet run", "env": { "nombre": "x" } },
              "tareas": [ { "nombre": "t", "carpeta": ".", "comando": "ls" } ] }
            """);
        Assert.Equal("demo", cfg.Name);
        Assert.Equal("edge", cfg.Browser);
        Assert.Equal("demo-api", cfg.Deployment?.Service);
        Assert.Equal("ab12", cfg.Deployment?.Server);
        Assert.Equal("api.demo.com", cfg.Deployment?.Domain);
        Assert.Equal("backend", cfg.Api?.Folder);
        Assert.Equal("dotnet run", cfg.Api?.Start);
        Assert.Equal("x", cfg.Api?.Env?["nombre"]);   // environment variable names are never renamed
        Assert.Equal("ls", cfg.Tasks?[0].Command);
    }

    [Fact]
    public void Current_project_config_is_unchanged()
    {
        var json = """{ "name": "demo" }""";
        Assert.Equal(json, LegacyKeys.Upgrade(json, LegacyKeys.Project, out var changed));
        Assert.False(changed);
    }

    [Fact]
    public void Invalid_json_is_returned_untouched()
    {
        Assert.Equal("{ nope", LegacyKeys.Upgrade("{ nope", LegacyKeys.Project, out var changed));
        Assert.False(changed);
    }

    [Fact]
    public void Existing_current_key_wins_over_the_old_one()
    {
        var text = LegacyKeys.Upgrade("""{ "name": "new", "nombre": "old" }""", LegacyKeys.Project, out _);
        Assert.Equal("new", JsonDocument.Parse(text).RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void Servers_saved_with_spanish_keys_are_read_and_rewritten()
    {
        var path = Path.Combine(_dir, "servidores.json");
        File.WriteAllText(path, """[ { "Id": "ab12", "Nombre": "Oracle", "Host": "1.2.3.4", "Usuario": "opc", "Puerto": 2222, "Clave": "C:\\k\\id", "Web": "nginx" } ]""");
        var store = new ServerStore(_dir);
        var list = store.Read();
        var s = Assert.Single(list);
        Assert.Equal(("ab12", "Oracle", "1.2.3.4", "opc", 2222, "C:\\k\\id", "nginx"), (s.Id, s.Name, s.Host, s.User, s.Port, s.Key, s.Web));
        var again = Assert.Single(new ServerStore(_dir).Read());   // second read comes from the rewritten file
        Assert.Equal("Oracle", again.Name);
        Assert.DoesNotContain("Nombre", File.ReadAllText(path));
    }

    [Fact]
    public void Credentials_saved_with_spanish_keys_are_read()
    {
        File.WriteAllText(Path.Combine(_dir, "credenciales.json"), """[ { "nombre": "admin", "usuario": "root", "clave": "s3cret", "nota": "db" } ]""");
        var c = Assert.Single(new CredentialsStore(_dir).Read());
        Assert.Equal(("admin", "root", "s3cret", "db"), (c.Name, c.User, c.Key, c.Note));
    }
}

public class ServerValidationTests
{
    [Theory]
    [InlineData("132.145.10.20", true)]
    [InlineData("api.midominio.com", true)]
    [InlineData("mi-servidor", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("300.1.1.1", false)]
    [InlineData("1.2.3", false)]
    [InlineData("", false)]
    [InlineData("-oProxyCommand=x", false)]
    [InlineData("opc@1.2.3.4", false)]
    [InlineData("host:22", false)]
    [InlineData("a b", false)]
    [InlineData("exa_mple.com", false)]
    public void Host(string host, bool ok) => Assert.Equal(ok, ServerValidation.CheckHost(host) is null);

    [Theory]
    [InlineData("22", true, 22)]
    [InlineData("65535", true, 65535)]
    [InlineData("0", false, 0)]
    [InlineData("65536", false, 0)]
    [InlineData("99999999999", false, 0)]
    [InlineData("2a", false, 0)]
    [InlineData("-1", false, 0)]
    [InlineData("", false, 0)]
    public void Port(string text, bool ok, int value)
    {
        Assert.Equal(ok, ServerValidation.CheckPort(text, out var port) is null);
        if (ok) Assert.Equal(value, port);
    }

    [Fact]
    public void Clean_host_removes_scheme_user_input_and_path()
    {
        Assert.Equal("1.2.3.4", ServerValidation.CleanHost("  ssh://1.2.3.4/ "));
        Assert.Equal("api.x.com", ServerValidation.CleanHost("https://api.x.com/path?q=1"));
    }

    [Theory]
    [InlineData("opc", true)]
    [InlineData("ec2-user", true)]
    [InlineData("-oProxy", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    public void User(string user, bool ok) =>
        Assert.Equal(ok, ServerValidation.CheckServer("n", "1.2.3.4", user, "22", "", out _) is null);

    [Fact]
    public void Server_form_reports_the_first_wrong_field()
    {
        Assert.Equal("name", ServerValidation.CheckServer("", "1.2.3.4", "opc", "22", "", out _)?.Field);
        Assert.Equal("host", ServerValidation.CheckServer("n", "", "opc", "22", "", out _)?.Field);
        Assert.Equal("port", ServerValidation.CheckServer("n", "1.2.3.4", "opc", "70000", "", out _)?.Field);
        Assert.Equal("key", ServerValidation.CheckServer("n", "1.2.3.4", "opc", "22", "id.pub", out _)?.Field);
        Assert.Equal("key", ServerValidation.CheckServer("n", "1.2.3.4", "opc", "22", "/no/such/key", out _)?.Field);
        Assert.Equal("name", ServerValidation.CheckServer(new string('x', 41), "1.2.3.4", "opc", "22", "", out _)?.Field);
        Assert.Null(ServerValidation.CheckServer("n", "1.2.3.4", "opc", "22", "", out var port));
        Assert.Equal(22, port);
    }

    [Theory]
    [InlineData("mi-api", "Mi.Api.dll", "api.x.com", null)]
    [InlineData("mi-api", "", "", null)]
    [InlineData("Mi-api", "a.dll", "", "service")]
    [InlineData("", "a.dll", "", "service")]
    [InlineData("a-very-long-name-a-very-long-name-a-very-long-name", "a.dll", "", "service")]
    [InlineData("mi-api", "app.exe", "", "dll")]
    [InlineData("mi-api", "a.dll", "not a domain", "domain")]
    [InlineData("mi-api", "a.dll", "https://api.x.com/", null)]
    public void Deployment(string service, string dll, string domain, string? field) =>
        Assert.Equal(field, ServerValidation.CheckDeployment(service, dll, domain)?.Field);
}
