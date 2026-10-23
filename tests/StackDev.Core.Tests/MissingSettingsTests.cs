using StackDev.Core.Deploy;
using Xunit;

namespace StackDev.Core.Tests;

public class MissingSettingsTests
{
    [Fact]
    public void Parse_keeps_only_valid_unique_names()
    {
        var names = MissingSettings.Parse("Identity__Jwt__SigningKey, Payments__WebhookSecret,Identity__Jwt__SigningKey,bad name,x;y");
        Assert.Equal(new[] { "Identity__Jwt__SigningKey", "Payments__WebhookSecret" }, names);
        Assert.Empty(MissingSettings.Parse(""));
        Assert.Empty(MissingSettings.Parse(null));
    }

    [Fact]
    public void Fill_generates_secrets_and_leaves_the_rest_to_the_user()
    {
        var env = EnvFile.Parse("ASPNETCORE_URLS=http://127.0.0.1:5001\nPayments__WebhookSecret=real\n");
        var (generated, manual) = MissingSettings.Fill(env, new[] { "Identity__Jwt__SigningKey", "Payments__WebhookSecret", "Smtp__Host" });
        Assert.Equal(new[] { "Identity__Jwt__SigningKey" }, generated);
        Assert.Equal(new[] { "Smtp__Host" }, manual);
        Assert.True(env.Get("Identity__Jwt__SigningKey")!.Length >= 48);
        Assert.Equal("real", env.Get("Payments__WebhookSecret"));
        Assert.False(env.Has("Smtp__Host"));
    }

    [Fact]
    public void Random_secrets_differ_and_are_safe_in_an_env_file()
    {
        var a = MissingSettings.RandomSecret();
        Assert.NotEqual(a, MissingSettings.RandomSecret());
        foreach (var c in a) Assert.True(char.IsLetterOrDigit(c) || c == '-' || c == '_');
    }

    [Fact]
    public void First_upload_failure_is_translated_with_its_reason()
    {
        var es = RemoteText.Translate("First upload: the files are on the server but the service did not start (the service is not healthy after 30s). It is stopped; there was no previous version to go back to.");
        Assert.Contains("primera subida", es);
        Assert.Contains("el servicio no responde bien tras 30 s", es);
        Assert.Equal("La subida falló: el servicio no arrancó", RemoteText.Translate("Deployment failed: the service did not start"));
    }
}

public class DatabaseAndCredentialTests
{
    [Fact]
    public void Database_password_is_long_and_safe_for_sql()
    {
        var a = DatabaseService.NewPassword();
        Assert.True(a.Length >= 24);
        foreach (var c in a) Assert.True(char.IsLetterOrDigit(c));
        Assert.NotEqual(a, DatabaseService.NewPassword());
    }

    [Fact]
    public void Connection_string_has_every_part()
    {
        Assert.Equal("Host=127.0.0.1;Port=5432;Database=shop_api;Username=shop_api_app;Password=pw1", DatabaseService.Connection("shop_api", "shop_api_app", "pw1"));
    }

    [Fact]
    public void Guide_names_each_setting_and_the_steps()
    {
        var text = MissingSettings.Guide("shop-api", new[] { "Smtp__Host", "Stripe__Key" });
        Assert.Contains("Smtp__Host", text);
        Assert.Contains("proveedor de correo", text);
        Assert.Contains("proveedor de pagos", text);
        Assert.Contains("shop-api", text);
        Assert.Contains("Variables (.env)", text);
    }

    [Fact]
    public void Upsert_replaces_by_name_and_keeps_the_rest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sd-cred-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new StackDev.Core.Credentials.CredentialsStore(dir);
            store.Upsert(new[] { new StackDev.Core.Credentials.Credential { Name = "a", User = "u", Key = "1" }, new StackDev.Core.Credentials.Credential { Name = "b", Key = "2" } });
            store.Upsert(new[] { new StackDev.Core.Credentials.Credential { Name = "A", User = "u", Key = "3" } });
            var all = store.Read();
            Assert.Equal(2, all.Count);
            Assert.Equal("3", all.Single(c => c.Name.ToLower() == "a").Key);
            Assert.Equal("2", all.Single(c => c.Name == "b").Key);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Create_postgres_is_chosen_only_when_no_connection_is_given()
    {
        var srv = new StackDev.Core.Servers.Server { Id = "1", Name = "S", Host = "1.2.3.4", User = "u" };
        ProvisionRequest R(string? cn) => new() { Server = srv, Service = "shop-api", Dll = "A.dll", Domain = "a.b.com", Port = 5001, Database = DatabaseKind.Postgres, PostgresConnection = cn };
        Assert.True(R("").CreatePostgres);
        Assert.False(R("Host=x").CreatePostgres);
    }
}

public class ConnectionStringHintTests
{
    [Fact]
    public void Postgres_connection_strings_can_be_created_by_the_app()
    {
        Assert.True(MissingSettings.CanCreateDatabase("ConnectionStrings__Postgres", false));
        Assert.True(MissingSettings.CanCreateDatabase("ConnectionStrings__Default", true));
        Assert.False(MissingSettings.CanCreateDatabase("ConnectionStrings__Default", false));
        Assert.False(MissingSettings.CanCreateDatabase("Smtp__Host", true));
    }
}

public class ApiEndpointsTests
{
    private static async Task<IReadOnlyList<ApiEndpoint>> Probe(Func<string, int> status)
    {
        int port;
        using (var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)) { l.Start(); port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; }
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var cts = new CancellationTokenSource();
        var loop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                System.Net.HttpListenerContext c;
                try { c = await listener.GetContextAsync(); } catch (Exception) { return; }
                c.Response.StatusCode = status(c.Request.Url!.AbsolutePath);
                c.Response.Close();
            }
        });
        try { return await ApiEndpoints.ProbeAsync($"http://127.0.0.1:{port}"); }
        finally { cts.Cancel(); listener.Stop(); }
    }

    [Fact]
    public async Task Lists_only_what_answers()
    {
        var found = await Probe(p => p is "/" or "/health/ready" or "/health/live" ? 200 : 404);
        Assert.Equal(new[] { "/", "/health/ready", "/health/live" }, found.Select(f => f.Path).ToArray());
        Assert.DoesNotContain(found, f => f.Kind == EndpointKind.Docs);
    }

    [Fact]
    public async Task Finds_documentation_when_it_is_published()
    {
        var found = await Probe(p => p is "/" or "/swagger" or "/swagger/index.html" or "/openapi/v1.json" ? 200 : 404);
        Assert.Contains(found, f => f.Path == "/swagger" && f.Kind == EndpointKind.Docs);
        Assert.Equal(1, found.Count(f => f.Label == "Documentación (Swagger)"));
        Assert.Contains(found, f => f.Path == "/openapi/v1.json");
    }

    [Fact]
    public async Task A_web_app_that_answers_everything_only_lists_the_home_page()
    {
        var found = await Probe(_ => 200);
        Assert.Equal(new[] { "/" }, found.Select(f => f.Path).ToArray());
    }

    [Fact]
    public async Task Protected_pages_count_as_existing()
    {
        var found = await Probe(p => p == "/" ? 200 : p == "/swagger" ? 401 : 404);
        Assert.True(found.Single(f => f.Path == "/swagger").Protected);
    }
}

public class ApiCatalogTests
{
    [Fact]
    public void Server_entry_uses_the_domain_and_status()
    {
        var ctx = new StackDev.Core.Config.ProjectContext("x", new StackDev.Core.Config.ProjectConfig());
        var sv = new StackDev.Core.Servers.Server { Id = "1", Name = "VPS", Host = "1.2.3.4", User = "u" };
        var depl = new StackDev.Core.Config.DeploymentConfig { Server = "1", Service = "shop-api", Domain = "shop.1-2-3-4.sslip.io" };
        var list = ApiCatalog.Build(ctx, sv, depl, new StackDev.Core.Servers.ApiStatus(true, "active", "", "", 200, null, null));
        var e = Assert.Single(list);
        Assert.Equal(ApiScope.Server, e.Scope);
        Assert.Equal("https://shop.1-2-3-4.sslip.io", e.Url);
        Assert.True(e.Running == true);
        Assert.Contains("VPS", e.Detail);
        Assert.Contains("HTTPS 200", e.Detail);
    }

    [Fact]
    public void Describe_marks_each_page_and_says_when_there_is_no_documentation()
    {
        var lines = ApiCatalog.Describe(new[] { new ApiEndpoint(EndpointKind.Home, "Página principal", "/", 200, 40), new ApiEndpoint(EndpointKind.Health, "Estado de salud", "/health", 503, 12) });
        Assert.Contains("✔ /", lines[0]);
        Assert.Contains("⚠ /health", lines[1]);
        Assert.Contains("Documentación", lines[2]);
        Assert.Contains("No responde", ApiCatalog.Describe(Array.Empty<ApiEndpoint>())[0]);
    }
}
