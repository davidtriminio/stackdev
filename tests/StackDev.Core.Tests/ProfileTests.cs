using StackDev.Core.Detection;
using StackDev.Core.Detection.Profiles;
using Xunit;

namespace StackDev.Core.Tests;

public class ProfileTests
{
    private static void W(TempFolder t, string rel, string content)
    {
        var path = t.In(rel.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>Only the profiles that ship with the application (no personal ones).</summary>
    private static ProfileCatalog BuiltIn(TempFolder t) => ProfileCatalog.Load(t.In("no-user-profiles"));

    [Fact]
    public void The_built_in_profiles_load_without_warnings()
    {
        using var t = new TempFolder();
        var c = BuiltIn(t);
        Assert.Empty(c.Warnings);
        Assert.Contains(c.Profiles, p => p.Id == "node-nest" && p.Role == "api");
        Assert.Contains(c.Profiles, p => p.Id == "python-fastapi");
        Assert.Contains(c.Profiles, p => p.Id == "web-vite-react" && p.Role == "web");
    }

    [Fact]
    public void A_Nest_api_uses_the_package_manager_of_its_lockfile()
    {
        using var t = new TempFolder();
        W(t, "backend/package.json", """{ "dependencies": { "@nestjs/core": "^10.0.0" } }""");
        W(t, "backend/pnpm-lock.yaml", "");
        var api = Detector.Detect(t.FilePath, BuiltIn(t)).Config.Api;
        Assert.NotNull(api);
        Assert.Equal("backend", api!.Folder);
        Assert.Equal("pnpm run start:dev", api.Start);
        Assert.Equal("pnpm test", api.Test);
        Assert.Equal("http://localhost:3000", api.Url);
    }

    [Fact]
    public void Express_is_not_taken_for_an_api_when_the_package_is_a_frontend()
    {
        using var t = new TempFolder();
        W(t, "package.json", """{ "dependencies": { "express": "^4", "react": "^18" } }""");
        Assert.Null(Detector.Detect(t.FilePath, BuiltIn(t)).Config.Api);
    }

    [Fact]
    public void A_Vite_React_web_app_reads_its_port_from_the_config_file()
    {
        using var t = new TempFolder();
        W(t, "web/package.json", """{ "devDependencies": { "vite": "^5", "react": "^18" } }""");
        W(t, "web/vite.config.ts", "export default { server: { port: 4321 } }");
        var web = Detector.Detect(t.FilePath, BuiltIn(t)).Config.Web;
        Assert.NotNull(web);
        Assert.Equal("http://localhost:4321", web!.Url);
        Assert.Equal("npm run dev", web.Start);
        Assert.Equal("npm install", web.Install);
    }

    [Fact]
    public void Next_wins_over_the_generic_Vite_profile_and_reads_its_port()
    {
        using var t = new TempFolder();
        W(t, "package.json", """{ "scripts": { "dev": "next dev -p 4000" }, "dependencies": { "next": "14", "vite": "5" } }""");
        var web = Detector.Detect(t.FilePath, BuiltIn(t)).Config.Web;
        Assert.Equal("http://localhost:4000", web!.Url);
    }

    [Fact]
    public void A_FastAPI_project_picks_the_start_command_by_where_main_py_is()
    {
        using var t = new TempFolder();
        W(t, "requirements.txt", "fastapi\nuvicorn\n");
        W(t, "app/main.py", "app = None");
        var d = Detector.Detect(t.FilePath, BuiltIn(t));
        Assert.Equal("uvicorn app.main:app --reload", d.Config.Api!.Start);
        Assert.Equal("/docs", d.Config.Api.Open);
        Assert.Equal("pytest", d.Config.Api.Test);
        Assert.Contains(d.Config.Tasks!, x => x.Name == "Instalar dependencias" && x.Command == "pip install -r requirements.txt");

        using var t2 = new TempFolder();
        W(t2, "pyproject.toml", "dependencies = [\"fastapi\"]");
        Assert.Equal("uvicorn main:app --reload", Detector.Detect(t2.FilePath, BuiltIn(t2)).Config.Api!.Start);
    }

    [Fact]
    public void Django_is_recognised_by_manage_py_and_offers_its_migrations()
    {
        using var t = new TempFolder();
        W(t, "manage.py", "os.environ.setdefault('DJANGO_SETTINGS_MODULE', 'site.settings')");
        W(t, "requirements.txt", "Django==5.0\n");
        var d = Detector.Detect(t.FilePath, BuiltIn(t));
        Assert.Equal("python manage.py runserver", d.Config.Api!.Start);
        Assert.Equal("http://localhost:8000", d.Config.Api.Url);
        Assert.Contains(d.Config.Tasks!, x => x.Command == "python manage.py migrate");
    }

    [Fact]
    public void Flask_is_not_chosen_when_the_project_is_Django_or_FastAPI()
    {
        using var t = new TempFolder();
        W(t, "requirements.txt", "flask\nfastapi\n");
        Assert.Equal("python-fastapi", ProfileDetector.Find(FileTree.Search(t.FilePath, new[] { "requirements.txt" }), BuiltIn(t), "api")!.Profile.Id);
    }

    [Fact]
    public void A_python_and_a_react_project_side_by_side_fill_both_slots()
    {
        using var t = new TempFolder();
        W(t, "api/requirements.txt", "fastapi\n");
        W(t, "web/package.json", """{ "dependencies": { "react": "18" }, "devDependencies": { "vite": "5" } }""");
        var d = Detector.Detect(t.FilePath, BuiltIn(t));
        Assert.Equal("api", d.Config.Api!.Folder);
        Assert.Equal("web", d.Config.Web!.Folder);
        Assert.Equal(new[] { "api", "web" }, d.Config.StartAll);
    }

    [Fact]
    public void A_dotnet_api_keeps_its_own_detection_and_profiles_do_not_replace_it()
    {
        using var t = new ShopTree();
        var d = Detector.Detect(t.Root, ProfileCatalog.FromJson("""
            { "profiles": [ { "id": "x", "title": "X", "role": "api", "match": { "files": ["*.csproj"], "contains": [".*"] }, "url": "http://localhost:1", "commands": { "start": "nope" } } ] }
            """));
        Assert.Equal("dotnet run --project src/Shop.Api", d.Config.Api!.Start);
    }

    [Fact]
    public void A_personal_profile_is_tried_before_the_built_in_ones()
    {
        using var t = new TempFolder();
        W(t, "profiles/mine.json", """
            { "profiles": [ {
                "id": "my-express", "title": "My Express", "role": "api",
                "match": { "files": ["package.json"], "contains": ["\"express\""] },
                "url": "http://localhost:9000",
                "commands": { "start": "{pm} run serve" } } ] }
            """);
        W(t, "package.json", """{ "dependencies": { "express": "4" } }""");
        var api = Detector.Detect(t.FilePath, ProfileCatalog.Load(t.In("profiles"))).Config.Api;
        Assert.Equal("npm run serve", api!.Start);
        Assert.Equal("http://localhost:9000", api.Url);
    }

    [Fact]
    public void A_broken_personal_profile_is_skipped_with_a_warning_and_the_rest_still_load()
    {
        using var t = new TempFolder();
        W(t, "profiles/broken.json", "{ not json");
        W(t, "profiles/bad-role.json", """{ "profiles": [ { "id": "z", "role": "desktop", "match": { "files": ["x"] } } ] }""");
        W(t, "profiles/bad-regex.json", """{ "profiles": [ { "id": "r", "role": "api", "match": { "files": ["x"], "contains": ["("] } } ] }""");
        var c = ProfileCatalog.Load(t.In("profiles"));
        Assert.Equal(3, c.Warnings.Count);
        Assert.Contains(c.Profiles, p => p.Id == "node-nest");
        Assert.DoesNotContain(c.Profiles, p => p.Id is "z" or "r");
    }

    [Fact]
    public void A_command_can_be_a_string_or_a_list_of_alternatives()
    {
        var c = ProfileCatalog.FromJson("""
            { "profiles": [ { "id": "a", "title": "A", "role": "api", "match": { "files": ["a.txt"] },
                "commands": { "start": [ { "when": "special.txt", "command": "run-special" }, { "command": "run-{pm}" } ], "test": "t" } } ] }
            """);
        using var t = new TempFolder();
        var p = c.Profiles.Single();
        Assert.Equal("run-yarn", p.Commands.Start!.Resolve(t.FilePath, "yarn"));
        File.WriteAllText(t.In("special.txt"), "");
        Assert.Equal("run-special", p.Commands.Start.Resolve(t.FilePath, "yarn"));
        Assert.Equal("t", p.Commands.Test!.Resolve(t.FilePath, "npm"));
    }
}

public class ProfileExampleTests
{
    [Fact]
    public void The_example_profile_in_the_repository_is_valid()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "stackdev.slnx"))) dir = dir.Parent;
        if (dir is null) return;   // not running from a checkout
        var c = ProfileCatalog.FromJson(File.ReadAllText(Path.Combine(dir.FullName, "examples", "profiles", "go.json")));
        Assert.Empty(c.Warnings);
        Assert.Equal("go-service", Assert.Single(c.Profiles).Id);
    }
}
