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
    public void Quote_escapa_como_CommandLineToArgvW(string entrada, string esperado) =>
        Assert.Equal(esperado, Args.Quote(entrada));

    [Fact]
    public void Join_une_con_espacios_y_escapa_cada_argumento()
    {
        Assert.Equal("exec -it shop-pg psql -U \"mi usuario\"",
            Args.Join(new[] { "exec", "-it", "shop-pg", "psql", "-U", "mi usuario" }));
    }

    [Fact]
    public void PsLiteral_duplica_las_comillas_simples()
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
    public void Dividir_respeta_las_comillas(string linea, string[] esperado) =>
        Assert.Equal(esperado, Args.Dividir(linea).ToArray());

    [Fact]
    public void Quote_y_Dividir_son_inversos()
    {
        var originales = new[] { "a b", "", "x\"y", @"C:\mi carpeta\", "plano", "dos  espacios" };
        Assert.Equal(originales, Args.Dividir(Args.Join(originales)).ToArray());
    }
}

public class PreguntasTests
{
    [Fact]
    public void Expande_cada_pregunta_con_la_respuesta()
    {
        var hechas = new List<string>();
        var r = Preguntas.Expandir("dotnet ef migrations add {?Nombre de la migracion} --project x", t => { hechas.Add(t); return "AgregaClientes"; });

        Assert.Equal("dotnet ef migrations add AgregaClientes --project x", r);
        Assert.Equal(new[] { "Nombre de la migracion" }, hechas);
    }

    [Fact]
    public void Una_respuesta_vacia_cancela_la_tarea()
    {
        Assert.Null(Preguntas.Expandir("echo {?Algo}", _ => "   "));
        Assert.Null(Preguntas.Expandir("echo {?Algo}", _ => null));
    }

    [Fact]
    public void Varias_preguntas_en_orden_y_la_repetida_se_pregunta_una_sola_vez()
    {
        var hechas = new List<string>();
        var r = Preguntas.Expandir("a {?Uno} b {?Dos} c {?Uno}", t => { hechas.Add(t); return t == "Uno" ? "1" : "2"; });

        Assert.Equal("a 1 b 2 c 1", r);
        Assert.Equal(new[] { "Uno", "Dos" }, hechas);
    }

    [Fact]
    public void Un_comando_sin_preguntas_queda_igual_y_no_pregunta_nada()
    {
        Assert.Equal("flutter test", Preguntas.Expandir("flutter test", _ => throw new InvalidOperationException()));
        Assert.False(Preguntas.TienePreguntas("flutter test"));
        Assert.True(Preguntas.TienePreguntas("x {?y}"));
    }
}

public class RutasTests
{
    private static readonly string Raiz = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "proyecto"));
    private static string P(params string[] partes) => Path.Combine(new[] { Raiz }.Concat(partes).ToArray());

    [Fact]
    public void Relativa_usa_barras_normales_y_punto_para_la_misma_carpeta()
    {
        Assert.Equal(".", Rutas.Relativa(Raiz, Raiz));
        Assert.Equal(".", Rutas.Relativa(Raiz, Raiz + Path.DirectorySeparatorChar));
        Assert.Equal("api", Rutas.Relativa(Raiz, P("api")));
        Assert.Equal("api/src/Shop.Api", Rutas.Relativa(Raiz, P("api", "src", "Shop.Api")));
    }

    [Fact]
    public void Relativa_devuelve_la_ruta_completa_si_esta_fuera_de_la_raiz()
    {
        var fuera = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "otro"));
        Assert.Equal(fuera, Rutas.Relativa(Raiz, fuera));
        // "proyecto2" no está dentro de "proyecto" aunque empiece igual
        var parecida = Raiz + "2";
        Assert.Equal(parecida, Rutas.Relativa(Raiz, parecida));
    }

    [Fact]
    public void Resolver_une_con_la_raiz_y_acepta_rutas_absolutas()
    {
        Assert.Equal(Raiz, Rutas.Resolver(Raiz, null));
        Assert.Equal(Raiz, Rutas.Resolver(Raiz, ""));
        Assert.Equal(Raiz, Rutas.Resolver(Raiz, "."));
        Assert.Equal(P("api"), Rutas.Resolver(Raiz, "api"));
        Assert.Equal(P("api", "src"), Rutas.Resolver(Raiz, "api/src"));
        Assert.Equal(P("api", "src"), Rutas.Resolver(Raiz, @"api\src"));
        var absoluta = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "x"));
        Assert.Equal(absoluta, Rutas.Resolver(Raiz, absoluta));
    }

    [Fact]
    public void EsIgualOSubcarpeta_respeta_el_limite_de_la_carpeta()
    {
        Assert.True(Rutas.EsIgualOSubcarpeta(P("api"), P("api")));
        Assert.True(Rutas.EsIgualOSubcarpeta(P("api"), P("api", "src", "x")));
        Assert.False(Rutas.EsIgualOSubcarpeta(P("api"), P("api2")));      // el script viejo decía que sí
        Assert.False(Rutas.EsIgualOSubcarpeta(P("api"), P("web")));
        Assert.False(Rutas.EsIgualOSubcarpeta(P("api"), Raiz));
    }

    [Fact]
    public void Slug_deja_letras_numeros_y_guiones()
    {
        Assert.Equal("mi-proyecto", Rutas.Slug("Mi Proyecto"));
        Assert.Equal("shop", Rutas.Slug("Shop"));
        Assert.Equal("app_v2-x", Rutas.Slug("app_v2.x"));
    }
}

public class EnvPorCarpetaTests
{
    private static ProyectoContexto Ctx(string raiz) => new(raiz, ConfigJson.Leer(ShopTree.ConfigActual));

    [Fact]
    public void La_cadena_de_conexion_aplica_en_la_carpeta_de_la_api_y_en_sus_subcarpetas()
    {
        var raiz = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "shop"));
        var ctx = Ctx(raiz);

        var enApi = ctx.EnvParaCarpeta(Path.Combine(raiz, "api"));
        Assert.NotNull(enApi);
        Assert.True(enApi!.ContainsKey("ConnectionStrings__Shop"));
        Assert.NotNull(ctx.EnvParaCarpeta(Path.Combine(raiz, "api", "src", "Shop.Api")));
    }

    [Fact]
    public void No_aplica_en_otras_carpetas_ni_en_una_que_solo_empieza_igual()
    {
        var raiz = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "shop"));
        var ctx = Ctx(raiz);

        Assert.Null(ctx.EnvParaCarpeta(raiz));
        Assert.Null(ctx.EnvParaCarpeta(Path.Combine(raiz, "web")));
        Assert.Null(ctx.EnvParaCarpeta(Path.Combine(raiz, "api2")));
    }

    [Fact]
    public void Sin_seccion_api_no_hay_variables()
    {
        var ctx = new ProyectoContexto(Path.GetTempPath(), new ProyectoConfig());
        Assert.Null(ctx.EnvParaCarpeta(Path.GetTempPath()));
    }

    [Fact]
    public void Carpetas_puertos_y_url_para_abrir_salen_de_la_configuracion()
    {
        var raiz = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "shop"));
        var ctx = Ctx(raiz);

        Assert.Equal(Path.Combine(raiz, "api"), ctx.Carpeta(Componente.Api));
        Assert.Equal(Path.Combine(raiz, "mobile"), ctx.Carpeta(Componente.Movil));
        Assert.Equal(raiz, ctx.Carpeta(Componente.Postgres));
        Assert.Equal(5109, ctx.Puerto(Componente.Api));
        Assert.Equal(4200, ctx.Puerto(Componente.Web));
        Assert.Equal("http://localhost:5109/scalar", ctx.UrlParaAbrir(Componente.Api));
        Assert.Equal("http://localhost:4200", ctx.UrlParaAbrir(Componente.Web));
        Assert.Equal(0, ProyectoContexto.PuertoDeUrl(""));
        Assert.Equal(0, ProyectoContexto.PuertoDeUrl("esto no es una url"));
    }

    [Fact]
    public void Una_seccion_ausente_significa_que_esa_parte_no_existe()
    {
        var ctx = new ProyectoContexto(Path.GetTempPath(), ConfigJson.Leer("{ \"api\": { \"carpeta\": \"api\" } }"));
        Assert.True(ctx.Tiene(Componente.Api));
        Assert.False(ctx.Tiene(Componente.Web));
        Assert.False(ctx.Tiene(Componente.Postgres));
        Assert.False(ctx.Tiene(Componente.Movil));
        Assert.Equal(new[] { "postgres", "api", "web", "emulador" }, ctx.LevantarTodo);
    }
}
