using System.Text.Json.Nodes;
using StackDev.Core.Config;
using Xunit;

namespace StackDev.Core.Tests;

public class ConfigTests
{
    [Fact]
    public void ReadsTheDevMenuJsonCurrentOfShop()
    {
        var c = ConfigJson.Read(ShopTree.CurrentConfig);

        Assert.Equal("pulperia", c.Name);
        Assert.Equal(5440, c.Postgres!.Port);
        Assert.Equal("pulperia-pg", c.Postgres.Container);
        Assert.Equal("", c.Postgres.Volume);
        Assert.Equal("/scalar", c.Api!.Open);
        Assert.Equal("Host=127.0.0.1;Port=5440;Database=pulperia;Username=pulperia;Password=dev", c.Api.Env!["ConnectionStrings__Pulperia"]);
        Assert.Equal("pnpm install", c.Web!.Install);
        Assert.Equal(@"V:\Programas\Android\Sdk", c.Mobile!.AndroidSdk);
        Assert.Equal(new[] { "postgres", "api", "web", "emulador" }, c.StartAll);
        Assert.Equal(3, c.Tasks!.Count);
    }

    [Fact]
    public void WriteYReturnAReadGivesTheSameContentJson()
    {
        var original = ConfigJson.Read(ShopTree.CurrentConfig);
        var text = ConfigJson.Write(original);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ShopTree.CurrentConfig), JsonNode.Parse(text)),
            "El archivo reescrito no es equivalente al original:\n" + text);
    }

    [Fact]
    public void OutputReadableWithoutEscapeQuotesSimpleNorAccentsYWithJumpsOfLineLf()
    {
        var c = new ProjectConfig
        {
            Name = "pulperia",
            Tasks = new() { new TaskConfig { Name = "Migración 'nueva'", Folder = "api", Command = "echo \"hola\" && dir" } },
        };
        var text = ConfigJson.Write(c);

        Assert.Contains("\"name\": \"pulperia\"", text);
        Assert.Contains("Migración 'nueva'", text);            // sin \u0027 ni \u00F3
        Assert.Contains("\\\"hola\\\"", text);                  // las comillas dobles sí se escapan
        Assert.DoesNotContain("\r", text);
        Assert.StartsWith("{\n  \"nombre\"", text);              // sangría de 2 espacios
        Assert.EndsWith("}\n", text);
        Assert.DoesNotContain("\"postgres\"", text);             // sección ausente: no se escribe
    }

    [Fact]
    public void KeepsTheFieldsNewThatThisVersionNoKnows()
    {
        const string json = """
            { "name": "x", "campoFuturo": { "a": 1 }, "api": { "folder": "api", "timeoutSeg": 30 } }
            """;
        var c = ConfigJson.Read(json);
        var other = JsonNode.Parse(ConfigJson.Write(c))!;

        Assert.Equal(1, (int)other["campoFuturo"]!["a"]!);
        Assert.Equal(30, (int)other["api"]!["timeoutSeg"]!);
    }

    [Fact]
    public void ToleratesCommentsCommasFinalYThePortWrittenAsText()
    {
        const string json = """
            {
              // el puerto del contenedor
              "postgres": { "port": "5440", },
            }
            """;
        Assert.Equal(5440, ConfigJson.Read(json).Postgres!.Port);
    }

    [Fact]
    public void ValuesEffectiveOfPostgresUseTheSameDefaultsThatTheScript()
    {
        var pg = new PostgresConfig { User = "pulperia" };
        Assert.Equal(5432, pg.EfPort);
        Assert.Equal("pulperia", pg.EfDatabase);          // sin baseDatos: el usuario
        Assert.Equal("postgres:17-alpine", pg.EfImage);
        Assert.Equal("postgres", new PostgresConfig().EfUser);
    }

    [Theory]
    [InlineData("postgres:17-alpine", "/var/lib/postgresql/data")]
    [InlineData("postgres:16", "/var/lib/postgresql/data")]
    [InlineData("postgres:18", "/var/lib/postgresql")]
    [InlineData("postgres:18-alpine", "/var/lib/postgresql")]
    [InlineData("postgres", "/var/lib/postgresql/data")]
    [InlineData("postgis/postgis:19-3.6", "/var/lib/postgresql")]
    public void PathOfDataPerTheVersionOfPostgres(string image, string expected) =>
        Assert.Equal(expected, PostgresConfig.DataPath(image));

    [Fact]
    public void ConfigStoreCreatesSavesReloadYLeavesCopyBak()
    {
        using var t = new ShopTree();
        var store = new ConfigStore(t.Root);
        Assert.False(store.Exists);

        store.Save(ConfigJson.Read(ShopTree.CurrentConfig));
        Assert.True(store.Exists);
        Assert.False(store.ReloadIfChanged());                 // nada cambió

        // el usuario edita el archivo
        File.WriteAllText(store.FilePath, ShopTree.CurrentConfig.Replace("5440", "5441"));
        File.SetLastWriteTimeUtc(store.FilePath, DateTime.UtcNow.AddMinutes(1));
        Assert.True(store.ReloadIfChanged());
        Assert.Equal(5441, store.Config.Postgres!.Port);
        Assert.False(store.ReloadIfChanged());

        store.Replace(new ProjectConfig { Name = "nuevo" });
        Assert.True(File.Exists(store.FilePath + ".bak"));
        Assert.Contains("5441", File.ReadAllText(store.FilePath + ".bak"));
        Assert.Equal("nuevo", ConfigJson.Read(File.ReadAllText(store.FilePath)).Name);
    }

    [Fact]
    public void ConfigStoreWithJsonBrokenLeavesTheMenuEmptyYWarnsWithoutLoseTheFile()
    {
        using var t = new ShopTree();
        var store = new ConfigStore(t.Root);
        File.WriteAllText(store.FilePath, "{ esto no es json");

        store.Load();

        Assert.NotNull(store.Error);
        Assert.Null(store.Config.Api);
        Assert.Equal("{ esto no es json", File.ReadAllText(store.FilePath));
        Assert.False(store.ReloadIfChanged());                 // no repite el error en cada vuelta
    }
}
