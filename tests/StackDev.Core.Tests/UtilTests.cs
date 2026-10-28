using StackDev.Core.Config;
using StackDev.Core.Util;
using Xunit;

namespace StackDev.Core.Tests;

public class ArgsTests
{
    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\dir", @"C:\dir")]
    [InlineData(@"C:\mi carpeta\", "\"C:\\mi carpeta\\\\\"")]      // barra final antes de la comilla de cierre: se duplica
    [InlineData("a\\\"b c", "\"a\\\\\\\"b c\"")]                    // barra + comilla dentro del texto
    public void QuoteEscapesAsCommandLineToArgvW(string input, string expected) =>
        Assert.Equal(expected, Args.Quote(input));

    [Fact]
    public void JoinJoinsWithSpacesYEscapesEachArgument()
    {
        Assert.Equal("exec -it shop-pg psql -U \"mi usuario\"",
            Args.Join(new[] { "exec", "-it", "shop-pg", "psql", "-U", "mi usuario" }));
    }

    [Fact]
    public void PsLiteralDuplicatesTheQuotesSimple()
    {
        Assert.Equal("'abc'", Args.PsLiteral("abc"));
        Assert.Equal("'it''s'", Args.PsLiteral("it's"));
        Assert.Equal(@"'C:\Users\o''brien'", Args.PsLiteral(@"C:\Users\o'brien"));
    }

    [Theory]
    [InlineData("dotnet run --project src/Shop.Api", new[] { "dotnet", "run", "--project", "src/Shop.Api" })]
    [InlineData("a  \"b c\"   d", new[] { "a", "b c", "d" })]
    [InlineData("x \"\" y", new[] { "x", "", "y" })]
    [InlineData("say \\\"hi\\\"", new[] { "say", "\"hi\"" })]
    public void SplitRespectsTheQuotes(string line, string[] expected) =>
        Assert.Equal(expected, Args.Split(line).ToArray());

    [Fact]
    public void QuoteYSplitAreInverse()
    {
        var original = new[] { "a b", "", "x\"y", @"C:\mi carpeta\", "plano", "dos  espacios" };
        Assert.Equal(original, Args.Split(Args.Join(original)).ToArray());
    }
}

public class PromptsTests
{
    [Fact]
    public void ExpandsEachQuestionWithTheResponse()
    {
        var done = new List<string>();
        var r = Prompts.Expand("dotnet ef migrations add {?Nombre de la migracion} --project x", t => { done.Add(t); return "AgregaClientes"; });

        Assert.Equal("dotnet ef migrations add AgregaClientes --project x", r);
        Assert.Equal(new[] { "Nombre de la migracion" }, done);
    }

    [Fact]
    public void AResponseEmptyCancelsTheTask()
    {
        Assert.Null(Prompts.Expand("echo {?Algo}", _ => "   "));
        Assert.Null(Prompts.Expand("echo {?Algo}", _ => null));
    }

    [Fact]
    public void SeveralQuestionsInOrderYTheRepeatedSelfQuestionASingleTime()
    {
        var done = new List<string>();
        var r = Prompts.Expand("a {?Uno} b {?Dos} c {?Uno}", t => { done.Add(t); return t == "Uno" ? "1" : "2"; });

        Assert.Equal("a 1 b 2 c 1", r);
        Assert.Equal(new[] { "Uno", "Dos" }, done);
    }

    [Fact]
    public void ACommandWithoutQuestionsRemainsSameYNoQuestionNothing()
    {
        Assert.Equal("flutter test", Prompts.Expand("flutter test", _ => throw new InvalidOperationException()));
        Assert.False(Prompts.HasPrompts("flutter test"));
        Assert.True(Prompts.HasPrompts("x {?y}"));
    }
}

public class PathsTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "proyecto"));
    private static string P(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    [Fact]
    public void RelativeUsesBarsNormalYPointForTheSameFolder()
    {
        Assert.Equal(".", Paths.Relative(Root, Root));
        Assert.Equal(".", Paths.Relative(Root, Root + Path.DirectorySeparatorChar));
        Assert.Equal("api", Paths.Relative(Root, P("api")));
        Assert.Equal("api/src/Shop.Api", Paths.Relative(Root, P("api", "src", "Shop.Api")));
    }

    [Fact]
    public void RelativeReturnsThePathCompleteYesThisOutsideOfTheRoot()
    {
        var outside = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "otro"));
        Assert.Equal(outside, Paths.Relative(Root, outside));
        // "project2" is not inside "project" even though it starts the same
        var similar = Root + "2";
        Assert.Equal(similar, Paths.Relative(Root, similar));
    }

    [Fact]
    public void ResolveJoinsWithTheRootYAcceptsPathsAbsolute()
    {
        Assert.Equal(Root, Paths.Resolve(Root, null));
        Assert.Equal(Root, Paths.Resolve(Root, ""));
        Assert.Equal(Root, Paths.Resolve(Root, "."));
        Assert.Equal(P("api"), Paths.Resolve(Root, "api"));
        Assert.Equal(P("api", "src"), Paths.Resolve(Root, "api/src"));
        Assert.Equal(P("api", "src"), Paths.Resolve(Root, @"api\src"));
        var absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "x"));
        Assert.Equal(absolute, Paths.Resolve(Root, absolute));
    }

    [Fact]
    public void IsSameOSubfolderRespectsTheLimitOfTheFolder()
    {
        Assert.True(Paths.IsSameOrSubfolder(P("api"), P("api")));
        Assert.True(Paths.IsSameOrSubfolder(P("api"), P("api", "src", "x")));
        Assert.False(Paths.IsSameOrSubfolder(P("api"), P("api2")));      // the old behaviour said yes
        Assert.False(Paths.IsSameOrSubfolder(P("api"), P("web")));
        Assert.False(Paths.IsSameOrSubfolder(P("api"), Root));
    }

    [Fact]
    public void SlugLeavesLettersNumbersYHyphens()
    {
        Assert.Equal("mi-proyecto", Paths.Slug("Mi Proyecto"));
        Assert.Equal("shop", Paths.Slug("Shop"));
        Assert.Equal("app_v2-x", Paths.Slug("app_v2.x"));
    }
}

public class EnvPerFolderTests
{
    private static ProjectContext Ctx(string root) => new(root, ConfigJson.Read(ShopTree.CurrentConfig));

    [Fact]
    public void TheStringOfConnectionAppliesInTheFolderOfTheApiYInItsSubfolders()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "shop"));
        var ctx = Ctx(root);

        var inApi = ctx.EnvForFolder(Path.Combine(root, "api"));
        Assert.NotNull(inApi);
        Assert.True(inApi!.ContainsKey("ConnectionStrings__Shop"));
        Assert.NotNull(ctx.EnvForFolder(Path.Combine(root, "api", "src", "Shop.Api")));
    }

    [Fact]
    public void NoAppliesInOtherFoldersNorInAThatOnlyStartsSame()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "shop"));
        var ctx = Ctx(root);

        Assert.Null(ctx.EnvForFolder(root));
        Assert.Null(ctx.EnvForFolder(Path.Combine(root, "web")));
        Assert.Null(ctx.EnvForFolder(Path.Combine(root, "api2")));
    }

    [Fact]
    public void WithoutSectionApiNoHasVariables()
    {
        var ctx = new ProjectContext(Path.GetTempPath(), new ProjectConfig());
        Assert.Null(ctx.EnvForFolder(Path.GetTempPath()));
    }

    [Fact]
    public void FoldersPortsYUrlForOpenExitOfTheSettings()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "shop"));
        var ctx = Ctx(root);

        Assert.Equal(Path.Combine(root, "api"), ctx.Folder(ProjectComponent.Api));
        Assert.Equal(Path.Combine(root, "mobile"), ctx.Folder(ProjectComponent.Mobile));
        Assert.Equal(root, ctx.Folder(ProjectComponent.Postgres));
        Assert.Equal(5109, ctx.Port(ProjectComponent.Api));
        Assert.Equal(4200, ctx.Port(ProjectComponent.Web));
        Assert.Equal("http://localhost:5109/scalar", ctx.UrlToOpen(ProjectComponent.Api));
        Assert.Equal("http://localhost:4200", ctx.UrlToOpen(ProjectComponent.Web));
        Assert.Equal(0, ProjectContext.PortFromUrl(""));
        Assert.Equal(0, ProjectContext.PortFromUrl("esto no es una url"));
    }

    [Fact]
    public void ASectionMissingMeansThatThatPartNoExists()
    {
        var ctx = new ProjectContext(Path.GetTempPath(), ConfigJson.Read("{ \"api\": { \"folder\": \"api\" } }"));
        Assert.True(ctx.Has(ProjectComponent.Api));
        Assert.False(ctx.Has(ProjectComponent.Web));
        Assert.False(ctx.Has(ProjectComponent.Postgres));
        Assert.False(ctx.Has(ProjectComponent.Mobile));
        Assert.Equal(new[] { "postgres", "api", "web", "emulador" }, ctx.StartAll);
    }
}
