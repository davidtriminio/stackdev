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
