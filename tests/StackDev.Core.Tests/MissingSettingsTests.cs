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
