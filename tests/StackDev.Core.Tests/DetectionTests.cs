using StackDev.Core.Config;
using StackDev.Core.Detection;
using Xunit;

namespace StackDev.Core.Tests;

public class DetectionTests
{
    [Fact]
    public void DetectsTheApiWithItsCommandsUrlYDocumentation()
    {
        using var t = new ShopTree();
        var d = Detector.Detect(t.Root);

        var api = d.Config.Api;
        Assert.NotNull(api);
        Assert.Equal("api", api!.Folder);
        Assert.Equal("http://localhost:5109", api.Url);   // the first http://, not the https
        Assert.Equal("/scalar", api.Open);
        Assert.Equal("dotnet run --project src/Shop.Api", api.Start);
        Assert.Equal("dotnet test --artifacts-path .art", api.Test);
        Assert.Equal("dotnet build --artifacts-path .art", api.Build);
        Assert.Empty(api.Env!);
        Assert.True(d.ApiUsesNpgsql);
        Assert.Equal("Shop", d.ConnectionName);
    }

    [Fact]
    public void NoConfusesTheProjectOfTestsNorItThatHasInBin()
    {
        using var t = new ShopTree();
        var d = Detector.Detect(t.Root);
        Assert.Equal("dotnet run --project src/Shop.Api", d.Config.Api!.Start);
    }

    [Fact]
    public void CreatesTheTasksOfEfWithStartupProjectWhenTheMigrationsAreInOtherProject()
    {
        using var t = new ShopTree();
        var tasks = Detector.Detect(t.Root).Config.Tasks!;

        Assert.Equal("EF: aplicar migraciones", tasks[0].Name);
        Assert.Equal("api", tasks[0].Folder);
        Assert.Equal("dotnet ef database update --project src/Shop.Infrastructure --startup-project src/Shop.Api", tasks[0].Command);
        Assert.Equal("dotnet ef migrations add {?Nombre de la migracion} --project src/Shop.Infrastructure --startup-project src/Shop.Api", tasks[1].Command);
    }

    [Fact]
    public void WithoutSnapshotNoHasTasksOfEf()
    {
        using var t = new ShopTree(withSnapshotInOtherProject: false);
        var tasks = Detector.Detect(t.Root).Config.Tasks!;
        Assert.DoesNotContain(tasks, x => x.Name!.StartsWith("EF:"));
    }

    [Fact]
    public void DetectsTheWebAngularWithPnpmYPortByDefault()
    {
        using var t = new ShopTree();
        var web = Detector.Detect(t.Root).Config.Web;

        Assert.NotNull(web);
        Assert.Equal("web", web!.Folder);
        Assert.Equal("http://localhost:4200", web.Url);
        Assert.Equal("pnpm start", web.Start);
        Assert.Equal("pnpm test --watch=false", web.Test);
        Assert.Equal("pnpm build", web.Build);
        Assert.Equal("pnpm install", web.Install);
    }

    [Theory]
    [InlineData("yarn.lock", "yarn start", "yarn install")]
    [InlineData("package-lock.json", "npm start", "npm install")]
    public void TheManagerOfPackagesExitsOfLockfile(string lockfile, string start, string install)
    {
        using var t = new ShopTree(manager: lockfile);
        var web = Detector.Detect(t.Root).Config.Web!;
        Assert.Equal(start, web.Start);
        Assert.Equal(install, web.Install);
    }

    [Fact]
    public void ThePortOfTheWebExitsOfAngularJsonOOfScriptStart()
    {
        using var t = new ShopTree();
        t.Write("web/angular.json", "{ \"serve\": { \"options\": { \"port\": 4300 } } }");
        Assert.Equal("http://localhost:4300", Detector.Detect(t.Root).Config.Web!.Url);

        t.Write("web/package.json", "{ \"dependencies\": { \"@angular/core\": \"^20\" }, \"scripts\": { \"start\": \"ng serve --port 4400\" } }");
        Assert.Equal("http://localhost:4400", Detector.Detect(t.Root).Config.Web!.Url);
    }

    [Fact]
    public void TheManagerAlsoCanExitOfFieldPackageManager()
    {
        using var t = new ShopTree(manager: "otro.txt");
        t.Write("web/package.json", "{ \"packageManager\": \"yarn@4.0.0\", \"dependencies\": { \"@angular/core\": \"^20\" } }");
        Assert.Equal("yarn start", Detector.Detect(t.Root).Config.Web!.Start);
    }

    [Fact]
    public void DetectsFlutterWithBuildRunnerYFolderOfTestYSelfSkipsAndroid()
    {
        using var t = new ShopTree();
        var mobile = Detector.Detect(t.Root).Config.Mobile;

        Assert.NotNull(mobile);
        Assert.Equal("mobile", mobile!.Folder);
        Assert.Equal("dart format lib test", mobile.Format);
        Assert.Equal("dart run build_runner build --delete-conflicting-outputs", mobile.Generate);
        Assert.Equal("flutter analyze", mobile.Parse);
    }

    [Fact]
    public void WithoutBuildRunnerNorFolderTestNoHasGenerateNorTestInFormat()
    {
        using var t = new ShopTree();
        t.Write("mobile/pubspec.yaml", "dependencies:\n  flutter:\n    sdk: flutter\n");
        Directory.Delete(t.FilePath("mobile/test"));
        var mobile = Detector.Detect(t.Root).Config.Mobile!;

        Assert.Equal("", mobile.Generate);
        Assert.Equal("dart format lib", mobile.Format);
    }

    [Fact]
    public void StartAllIncludesOnlyItThatExistsYTheTaskOfApk()
    {
        using var t = new ShopTree();
        var cfg = Detector.Detect(t.Root).Config;

        Assert.Equal(new[] { "api", "web", "emulador" }, cfg.StartAll);
        Assert.Contains(cfg.Tasks!, x => x.Name == "Flutter: compilar APK (release)" && x.Folder == "mobile");
        Assert.Equal("shop", cfg.Name);
    }

    [Fact]
    public void ApplyPostgresAddsTheStringOfConnectionATheApiYPutsPostgresFirst()
    {
        using var t = new ShopTree();
        var d = Detector.Detect(t.Root);
        var pg = new PostgresConfig
        {
            Container = "shop-pg", Image = "postgres:17-alpine", Port = 5440,
            User = "shop", Password = "dev", Database = "shop", Volume = "",
        };

        Detector.ApplyPostgres(d.Config, pg, d.ConnectionName!);

        Assert.Equal("Host=127.0.0.1;Port=5440;Database=shop;Username=shop;Password=dev",
            d.Config.Api!.Env!["ConnectionStrings__Shop"]);
        Assert.Equal(new[] { "postgres", "api", "web", "emulador" }, d.Config.StartAll);
    }

    [Fact]
    public void ARepoWithoutNothingKnownReturnsASettingsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stackdev-vacio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var d = Detector.Detect(dir);
            Assert.False(d.FoundAnything);
            Assert.Empty(d.Config.StartAll!);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TheFolderOfTheSolutionIsTheFirstTowardAboveWithSln()
    {
        using var t = new ShopTree();
        var dirApi = t.FilePath("api/src/Shop.Api");
        Assert.Equal(t.FilePath("api"), Detector.SolutionFolder(t.Root, dirApi));

        File.Delete(t.FilePath("api/Shop.sln"));   // without .sln: the first folder under the root
        Assert.Equal(t.FilePath("api"), Detector.SolutionFolder(t.Root, dirApi));
    }

    [Fact]
    public void SwaggerYesTheApiUsesSwashbuckleYLocalhost5000YesNoHasLaunchSettings()
    {
        using var t = new ShopTree();
        t.Write("api/src/Shop.Api/Shop.Api.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><ItemGroup><PackageReference Include=\"Swashbuckle.AspNetCore\" /></ItemGroup></Project>");
        File.Delete(t.FilePath("api/src/Shop.Api/Properties/launchSettings.json"));

        var api = Detector.Detect(t.Root).Config.Api!;
        Assert.Equal("/swagger", api.Open);
        Assert.Equal("http://localhost:5000", api.Url);
    }

    [Fact]
    public void TheNameOfTheConnectionExitsOfAppsettingsBeforeThatOfCode()
    {
        using var t = new ShopTree();
        t.Write("api/src/Shop.Api/appsettings.json", "{ \"ConnectionStrings\": { \"Principal\": \"\" } }");
        Assert.Equal("Principal", Detector.Detect(t.Root).ConnectionName);
    }
}
