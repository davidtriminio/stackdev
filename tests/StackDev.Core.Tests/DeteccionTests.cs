using StackDev.Core.Config;
using StackDev.Core.Detection;
using Xunit;

namespace StackDev.Core.Tests;

public class DeteccionTests
{
    [Fact]
    public void Detecta_la_api_con_sus_comandos_url_y_documentacion()
    {
        using var t = new ShopTree();
        var d = Detector.Detectar(t.Raiz);

        var api = d.Config.Api;
        Assert.NotNull(api);
        Assert.Equal("api", api!.Carpeta);
        Assert.Equal("http://localhost:5109", api.Url);   // el primer http://, no el https
        Assert.Equal("/scalar", api.Abrir);
        Assert.Equal("dotnet run --project src/Shop.Api", api.Iniciar);
        Assert.Equal("dotnet test --artifacts-path .art", api.Test);
        Assert.Equal("dotnet build --artifacts-path .art", api.Build);
        Assert.Empty(api.Env!);
        Assert.True(d.ApiUsaNpgsql);
        Assert.Equal("Shop", d.NombreConexion);
    }

    [Fact]
    public void No_confunde_el_proyecto_de_tests_ni_lo_que_hay_en_bin()
    {
        using var t = new ShopTree();
        var d = Detector.Detectar(t.Raiz);
        Assert.Equal("dotnet run --project src/Shop.Api", d.Config.Api!.Iniciar);
    }

    [Fact]
    public void Crea_las_tareas_de_EF_con_startup_project_cuando_las_migraciones_estan_en_otro_proyecto()
    {
        using var t = new ShopTree();
        var tareas = Detector.Detectar(t.Raiz).Config.Tareas!;

        Assert.Equal("EF: aplicar migraciones", tareas[0].Nombre);
        Assert.Equal("api", tareas[0].Carpeta);
        Assert.Equal("dotnet ef database update --project src/Shop.Infrastructure --startup-project src/Shop.Api", tareas[0].Comando);
        Assert.Equal("dotnet ef migrations add {?Nombre de la migracion} --project src/Shop.Infrastructure --startup-project src/Shop.Api", tareas[1].Comando);
    }

    [Fact]
    public void Sin_snapshot_no_hay_tareas_de_EF()
    {
        using var t = new ShopTree(conSnapshotEnOtroProyecto: false);
        var tareas = Detector.Detectar(t.Raiz).Config.Tareas!;
        Assert.DoesNotContain(tareas, x => x.Nombre!.StartsWith("EF:"));
    }

    [Fact]
    public void Detecta_la_web_angular_con_pnpm_y_puerto_por_defecto()
    {
        using var t = new ShopTree();
        var web = Detector.Detectar(t.Raiz).Config.Web;

        Assert.NotNull(web);
        Assert.Equal("web", web!.Carpeta);
        Assert.Equal("http://localhost:4200", web.Url);
        Assert.Equal("pnpm start", web.Iniciar);
        Assert.Equal("pnpm test --watch=false", web.Test);
        Assert.Equal("pnpm build", web.Build);
        Assert.Equal("pnpm install", web.Instalar);
    }

    [Theory]
    [InlineData("yarn.lock", "yarn start", "yarn install")]
    [InlineData("package-lock.json", "npm start", "npm install")]
    public void El_gestor_de_paquetes_sale_del_lockfile(string lockfile, string iniciar, string instalar)
    {
        using var t = new ShopTree(gestor: lockfile);
        var web = Detector.Detectar(t.Raiz).Config.Web!;
        Assert.Equal(iniciar, web.Iniciar);
        Assert.Equal(instalar, web.Instalar);
    }

    [Fact]
    public void El_puerto_de_la_web_sale_de_angular_json_o_del_script_start()
    {
        using var t = new ShopTree();
        t.Escribir("web/angular.json", "{ \"serve\": { \"options\": { \"port\": 4300 } } }");
        Assert.Equal("http://localhost:4300", Detector.Detectar(t.Raiz).Config.Web!.Url);

        t.Escribir("web/package.json", "{ \"dependencies\": { \"@angular/core\": \"^20\" }, \"scripts\": { \"start\": \"ng serve --port 4400\" } }");
        Assert.Equal("http://localhost:4400", Detector.Detectar(t.Raiz).Config.Web!.Url);
    }

    [Fact]
    public void El_gestor_tambien_puede_salir_del_campo_packageManager()
    {
        using var t = new ShopTree(gestor: "otro.txt");
        t.Escribir("web/package.json", "{ \"packageManager\": \"yarn@4.0.0\", \"dependencies\": { \"@angular/core\": \"^20\" } }");
        Assert.Equal("yarn start", Detector.Detectar(t.Raiz).Config.Web!.Iniciar);
    }

    [Fact]
    public void Detecta_flutter_con_build_runner_y_carpeta_de_test_y_se_salta_android()
    {
        using var t = new ShopTree();
        var movil = Detector.Detectar(t.Raiz).Config.Movil;

        Assert.NotNull(movil);
        Assert.Equal("mobile", movil!.Carpeta);
        Assert.Equal("dart format lib test", movil.Formato);
        Assert.Equal("dart run build_runner build --delete-conflicting-outputs", movil.Generar);
        Assert.Equal("flutter analyze", movil.Analizar);
    }

    [Fact]
    public void Sin_build_runner_ni_carpeta_test_no_hay_generar_ni_test_en_formato()
    {
        using var t = new ShopTree();
        t.Escribir("mobile/pubspec.yaml", "dependencies:\n  flutter:\n    sdk: flutter\n");
        Directory.Delete(t.Ruta("mobile/test"));
        var movil = Detector.Detectar(t.Raiz).Config.Movil!;

        Assert.Equal("", movil.Generar);
        Assert.Equal("dart format lib", movil.Formato);
    }

    [Fact]
    public void LevantarTodo_incluye_solo_lo_que_existe_y_la_tarea_del_apk()
    {
        using var t = new ShopTree();
        var cfg = Detector.Detectar(t.Raiz).Config;

        Assert.Equal(new[] { "api", "web", "emulador" }, cfg.LevantarTodo);
        Assert.Contains(cfg.Tareas!, x => x.Nombre == "Flutter: compilar APK (release)" && x.Carpeta == "mobile");
        Assert.Equal("shop", cfg.Nombre);
    }

    [Fact]
    public void AplicarPostgres_agrega_la_cadena_de_conexion_a_la_api_y_pone_postgres_primero()
    {
        using var t = new ShopTree();
        var d = Detector.Detectar(t.Raiz);
        var pg = new PostgresConfig
        {
            Contenedor = "shop-pg", Imagen = "postgres:17-alpine", Puerto = 5440,
            Usuario = "shop", Password = "dev", BaseDatos = "shop", Volumen = "",
        };

        Detector.AplicarPostgres(d.Config, pg, d.NombreConexion!);

        Assert.Equal("Host=127.0.0.1;Port=5440;Database=shop;Username=shop;Password=dev",
            d.Config.Api!.Env!["ConnectionStrings__Shop"]);
        Assert.Equal(new[] { "postgres", "api", "web", "emulador" }, d.Config.LevantarTodo);
    }

    [Fact]
    public void Un_repo_sin_nada_conocido_devuelve_una_configuracion_vacia()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stackdev-vacio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var d = Detector.Detectar(dir);
            Assert.False(d.EncontroAlgo);
            Assert.Empty(d.Config.LevantarTodo!);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void La_carpeta_de_la_solucion_es_la_primera_hacia_arriba_con_sln()
    {
        using var t = new ShopTree();
        var dirApi = t.Ruta("api/src/Shop.Api");
        Assert.Equal(t.Ruta("api"), Detector.CarpetaSolucion(t.Raiz, dirApi));

        File.Delete(t.Ruta("api/Shop.sln"));   // sin .sln: la primera carpeta bajo la raíz
        Assert.Equal(t.Ruta("api"), Detector.CarpetaSolucion(t.Raiz, dirApi));
    }

    [Fact]
    public void Swagger_si_la_api_usa_Swashbuckle_y_localhost_5000_si_no_hay_launchSettings()
    {
        using var t = new ShopTree();
        t.Escribir("api/src/Shop.Api/Shop.Api.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><ItemGroup><PackageReference Include=\"Swashbuckle.AspNetCore\" /></ItemGroup></Project>");
        File.Delete(t.Ruta("api/src/Shop.Api/Properties/launchSettings.json"));

        var api = Detector.Detectar(t.Raiz).Config.Api!;
        Assert.Equal("/swagger", api.Abrir);
        Assert.Equal("http://localhost:5000", api.Url);
    }

    [Fact]
    public void El_nombre_de_la_conexion_sale_de_appsettings_antes_que_del_codigo()
    {
        using var t = new ShopTree();
        t.Escribir("api/src/Shop.Api/appsettings.json", "{ \"ConnectionStrings\": { \"Principal\": \"\" } }");
        Assert.Equal("Principal", Detector.Detectar(t.Raiz).NombreConexion);
    }
}
