using StackDev.Core.Config;
using StackDev.Core.Detection;
using StackDev.Core.State;
using Xunit;

namespace StackDev.Core.Tests;

public sealed class TempFolder : IDisposable
{
    public string FilePath { get; } = Path.Combine(Path.GetTempPath(), "stackdev-t-" + Guid.NewGuid().ToString("N"));
    public TempFolder() => Directory.CreateDirectory(FilePath);
    public string In(params string[] p) => Path.Combine(new[] { FilePath }.Concat(p).ToArray());
    public void Dispose() { try { Directory.Delete(FilePath, true); } catch (IOException) { } }
}

public class StateStoreTests
{
    [Fact]
    public void TheKeyOfProjectIsStableYNoDistinguishesUppercase()
    {
        // La parte del hash sale de la ruta en minúsculas; el nombre conserva las mayúsculas de la carpeta.
        var a = StateStore.Key(Path.Combine(Path.GetTempPath(), "dev", "Pulperia"));
        var b = StateStore.Key(Path.Combine(Path.GetTempPath(), "DEV", "pulperia"));
        Assert.Equal(a[(a.LastIndexOf('-') + 1)..], b[(b.LastIndexOf('-') + 1)..]);
        Assert.NotEqual(a, StateStore.Key(Path.Combine(Path.GetTempPath(), "dev", "otro")));
        Assert.Matches(@"^Pulperia-[0-9a-f]{8}$", a);
    }

    [Fact]
    public void MigratesFromFlutterDevWithoutDeleteTheOriginNorOverwriteItThatAlreadyHas()
    {
        using var t = new TempFolder();
        var appData = t.FilePath;
        var old = StateStore.DirPrevious(appData);
        Directory.CreateDirectory(Path.Combine(old, "respaldos", "pulperia-abcd1234"));
        File.WriteAllText(Path.Combine(old, "ultimo-proyecto.txt"), @"V:\dev\pulperia");
        File.WriteAllText(Path.Combine(old, "ultimo-avd.txt"), "Resizable_Experimental");
        File.WriteAllText(Path.Combine(old, "respaldos", "pulperia-abcd1234", "pulperia-20260101-120000.sql"), "-- sql");

        var s = StateStore.Open(appData);

        Assert.Equal(@"V:\dev\pulperia", s.LastProject);
        Assert.Equal("Resizable_Experimental", s.GlobalLastAvd);
        Assert.True(File.Exists(Path.Combine(s.Dir, "respaldos", "pulperia-abcd1234", "pulperia-20260101-120000.sql")));
        Assert.True(File.Exists(Path.Combine(old, "ultimo-proyecto.txt")));   // el script viejo sigue funcionando
        Assert.EndsWith("stackdev", s.Dir);

        // segunda apertura: no vuelve a migrar ni pisa lo que el usuario ya cambió
        s.LastProject = @"V:\dev\otro";
        var s2 = StateStore.Open(appData);
        Assert.Equal(@"V:\dev\otro", s2.LastProject);
    }

    [Fact]
    public void WithoutFolderPreviousSimplyStartsEmpty()
    {
        using var t = new TempFolder();
        var s = StateStore.Open(t.FilePath);
        Assert.Null(s.LastProject);
        Assert.Null(s.GlobalLastAvd);
        Assert.Empty(s.ReadRecent("x"));
    }

    [Fact]
    public void MigrateForcedBringsOnlyItThatIsMoreNew()
    {
        using var t = new TempFolder();
        var old = t.In("viejo"); var fresh = new StateStore(t.In("nuevo"));
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "ultimo-avd.txt"), "A");
        fresh.Migrate(old);

        File.WriteAllText(Path.Combine(old, "ultimo-avd.txt"), "B");
        File.SetLastWriteTimeUtc(Path.Combine(old, "ultimo-avd.txt"), DateTime.UtcNow.AddMinutes(5));
        var r = fresh.Migrate(old, force: true);

        Assert.Equal(1, r.Copied);
        Assert.Equal("B", fresh.GlobalLastAvd);
    }

    [Fact]
    public void TheEmulatorByDefaultFollowsTheOrderParameterProjectConfigGeneral()
    {
        using var t = new TempFolder();
        var s = new StateStore(t.FilePath);
        Assert.Null(s.ResolveAvd("p-1", null, null));

        s.SaveLastAvd("otro-9", "Pixel_General");
        Assert.Equal("Pixel_General", s.ResolveAvd("p-1", null, null));            // el general
        Assert.Equal("Del_Config", s.ResolveAvd("p-1", null, "Del_Config"));       // movil.avd gana al general
        s.SaveLastAvd("p-1", "Pixel_Proyecto");
        Assert.Equal("Pixel_Proyecto", s.ResolveAvd("p-1", null, "Del_Config"));   // el del proyecto gana a la config
        Assert.Equal("Param", s.ResolveAvd("p-1", "Param", "Del_Config"));         // el parámetro gana a todos
    }

    [Fact]
    public void TheConditionsSelfSaveByAvdInTheOrderOfAlwaysYSelfDeletesTheFileToRemainEmpty()
    {
        using var t = new TempFolder();
        var s = new StateStore(t.FilePath);

        s.SaveConditions("Pixel", new Dictionary<string, string> { ["Tema"] = "Oscuro", ["Red"] = "Modo avion", ["Ubicacion"] = "14.07, -87.19" });
        var file = File.ReadAllLines(Path.Combine(t.FilePath, "condiciones-Pixel.txt"));
        Assert.Equal(new[] { "Red=Modo avion", "Tema=Oscuro", "Ubicacion=14.07, -87.19" }, file);

        var read = s.ReadConditions("Pixel");
        Assert.Equal("Oscuro", read["Tema"]);
        Assert.Empty(s.ReadConditions("OtroAvd"));

        s.SaveConditions("Pixel", new Dictionary<string, string>());
        Assert.False(File.Exists(Path.Combine(t.FilePath, "condiciones-Pixel.txt")));
    }

    [Fact]
    public void TheCommandsRecentSaveTheFolderWithoutRepeatYOnlyTheLast10()
    {
        using var t = new TempFolder();
        var s = new StateStore(t.FilePath);

        for (var i = 1; i <= 12; i++) s.AddRecent("p-1", "api", $"cmd {i}");
        s.AddRecent("p-1", "web", "pnpm lint");
        s.AddRecent("p-1", "api", "cmd 8");   // repetido: sube al primer lugar

        var l = s.ReadRecent("p-1");
        Assert.Equal(10, l.Count);
        Assert.Equal(new StateStore.RecentCommand("api", "cmd 8"), l[0]);
        Assert.Equal(new StateStore.RecentCommand("web", "pnpm lint"), l[1]);
        Assert.Single(l, c => c.Command == "cmd 8");
        Assert.Equal(new StateStore.RecentCommand("api", "cmd 12"), l[2]);
    }

    [Fact]
    public void ReadsTheFormatOldOfRecentWithoutFolder()
    {
        using var t = new TempFolder();
        File.WriteAllLines(Path.Combine(t.FilePath, "comandos-p-1.txt"), new[] { "flutter pub get", "api\tdotnet build" });
        var l = new StateStore(t.FilePath).ReadRecent("p-1");

        Assert.Equal(new StateStore.RecentCommand(".", "flutter pub get"), l[0]);
        Assert.Equal(new StateStore.RecentCommand("api", "dotnet build"), l[1]);
    }
}

public class PostgresDockerTests
{
    [Fact]
    public void FiltersOnlyTheContainersWithImagePostgresOPostgis()
    {
        var output = "pulperia-pg|postgres:17-alpine\nredis|redis:7\r\ngeo|postgis/postgis:16-3.4\nweb|nginx\n";
        var l = PostgresDocker.FilterContainers(output);

        Assert.Equal(new[] { "pulperia-pg", "geo" }, l.Select(c => c.Name).ToArray());
        Assert.Equal("postgis/postgis:16-3.4", l[1].Image);
    }

    private const string Inspect = """
        { "Name": "/pulperia-pg",
          "Config": { "Image": "postgres:17-alpine",
                      "Env": ["POSTGRES_USER=pulperia", "POSTGRES_PASSWORD=dev", "POSTGRES_DB=pulperia", "PATH=/usr/bin"] },
          "HostConfig": { "PortBindings": { "5432/tcp": [ { "HostIp": "", "HostPort": "5440" } ] } },
          "Mounts": [ { "Type": "volume", "Name": "VOLUMEN", "Destination": "/var/lib/postgresql/data" } ] }
        """;

    [Fact]
    public void ReadsImagePortUserPasswordBaseYVolumeWithName()
    {
        var pg = PostgresDocker.FromInspect("pulperia-pg", Inspect.Replace("VOLUMEN", "pulperia-data"))!;

        Assert.Equal("pulperia-pg", pg.Container);
        Assert.Equal("postgres:17-alpine", pg.Image);
        Assert.Equal(5440, pg.Port);
        Assert.Equal("pulperia", pg.User);
        Assert.Equal("dev", pg.Password);
        Assert.Equal("pulperia", pg.Database);
        Assert.Equal("pulperia-data", pg.Volume);
    }

    [Fact]
    public void AVolumeAnonymousCountAsWithoutVolume()
    {
        var pg = PostgresDocker.FromInspect("x", Inspect.Replace("VOLUMEN", new string('a', 64)))!;
        Assert.Equal("", pg.Volume);
    }

    [Fact]
    public void WithoutVariablesNorPortsUsesTheValuesOfPostgresByDefault()
    {
        var pg = PostgresDocker.FromInspect("x", """{ "Config": { "Image": "postgres:16", "Env": [] } }""")!;

        Assert.Equal(5432, pg.Port);
        Assert.Equal("postgres", pg.User);
        Assert.Equal("postgres", pg.Database);
        Assert.Equal("", pg.Password);
    }

    [Fact]
    public void AJsonInvalidReturnsNull()
    {
        Assert.Null(PostgresDocker.FromInspect("x", "Error: No such object: x"));
    }
}

public class AndroidSdkTests
{
    [Fact]
    public void ReadsAndroidSdkOfFlutterSettingsYConvertsTheBars()
    {
        Assert.Equal(@"V:\Programas\Android\Sdk",
            AndroidSdkLocator.FromFlutterSettings("{ \"android-sdk\": \"V:\\\\Programas\\\\Android\\\\Sdk\" }"));
        Assert.Null(AndroidSdkLocator.FromFlutterSettings("{}"));
    }

    [Fact]
    public void RespectsTheOrderOfSearch()
    {
        var valid = new HashSet<string> { "ENV_ROOT", "ENV_HOME", "FLUTTER", Path.Combine("LOCAL", "Android", "Sdk"), "CFG" };
        string? Var(string n) => n == "ANDROID_SDK_ROOT" ? "ENV_ROOT" : n == "ANDROID_HOME" ? "ENV_HOME" : null;
        const string flutter = "{ \"android-sdk\": \"FLUTTER\" }";

        Assert.Equal("CFG", AndroidSdkLocator.Search("CFG", Var, flutter, "LOCAL", valid.Contains));
        Assert.Equal("ENV_ROOT", AndroidSdkLocator.Search(null, Var, flutter, "LOCAL", valid.Contains));
        Assert.Equal("ENV_HOME", AndroidSdkLocator.Search("no-existe", n => n == "ANDROID_HOME" ? "ENV_HOME" : null, flutter, "LOCAL", valid.Contains));
        Assert.Equal("FLUTTER", AndroidSdkLocator.Search(null, _ => null, flutter, "LOCAL", valid.Contains));
        Assert.Equal(Path.Combine("LOCAL", "Android", "Sdk"), AndroidSdkLocator.Search(null, _ => null, null, "LOCAL", valid.Contains));
        Assert.Null(AndroidSdkLocator.Search(null, _ => null, null, null, valid.Contains));
    }

    [Fact]
    public void TheVariablesOfSdkAreOnlyForTheProcessesLaunched()
    {
        var v = AndroidSdkLocator.ProcessVariables(@"V:\Programas\Android\Sdk");
        Assert.Equal(@"V:\Programas\Android\Sdk", v["ANDROID_SDK_ROOT"]);
        Assert.Equal(@"V:\Programas\Android\Sdk", v["ANDROID_HOME"]);
    }
}

public class ProjectRootTests
{
    [Fact]
    public void AFolderIsProjectYesHasGitConfigPubspecPackageOSolution()
    {
        using var t = new TempFolder();
        Assert.False(ProjectRoot.IsProject(t.FilePath));
        Assert.False(ProjectRoot.IsProject(t.In("no-existe")));

        foreach (var mark in new[] { "dev-menu.json", "pubspec.yaml", "package.json", "App.sln", "App.slnx", "App.csproj" })
        {
            using var u = new TempFolder();
            File.WriteAllText(u.In(mark), "");
            Assert.True(ProjectRoot.IsProject(u.FilePath), mark);
        }
        using var g = new TempFolder();
        Directory.CreateDirectory(g.In(".git"));
        Assert.True(ProjectRoot.IsProject(g.FilePath));
    }

    [Fact]
    public void YesChooseASubfolderSelfSuggestsTheRootOfRepo()
    {
        using var t = new ShopTree();
        Assert.Equal(t.Root, ProjectRoot.SuggestParent(t.FilePath("mobile")));
        Assert.Null(ProjectRoot.SuggestParent(t.Root));                     // ya es la raíz
        File.WriteAllText(t.FilePath("mobile/dev-menu.json"), "{}");
        Assert.Null(ProjectRoot.SuggestParent(t.FilePath("mobile")));           // tiene su propia configuración
    }

    [Fact]
    public void ResolveFollowsTheOrderParameterFolderCurrentLastSelector()
    {
        using var project = new ShopTree();
        using var empty = new TempFolder();
        using var last = new TempFolder();

        var r = ProjectRoot.Resolve(empty.FilePath, project.Root, last.FilePath);
        Assert.Equal(ProjectOrigin.Parameter, r.Origin);

        r = ProjectRoot.Resolve(null, project.Root, last.FilePath);
        Assert.Equal(ProjectOrigin.CurrentFolder, r.Origin);

        r = ProjectRoot.Resolve(null, empty.FilePath, last.FilePath);
        Assert.Equal(ProjectOrigin.LastUsed, r.Origin);
        Assert.Equal(last.FilePath, r.FilePath);

        r = ProjectRoot.Resolve(null, empty.FilePath, empty.In("borrada"));
        Assert.Equal(ProjectOrigin.Selector, r.Origin);

        r = ProjectRoot.Resolve(empty.In("no-existe"), project.Root, last.FilePath);   // parámetro malo: no cae a la carpeta actual
        Assert.Equal(ProjectOrigin.Selector, r.Origin);
        Assert.True(r.ParameterInvalid);
    }
}
