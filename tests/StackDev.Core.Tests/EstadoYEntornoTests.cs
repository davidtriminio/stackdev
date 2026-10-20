using StackDev.Core.Config;
using StackDev.Core.Detection;
using StackDev.Core.State;
using Xunit;

namespace StackDev.Core.Tests;

public sealed class CarpetaTemporal : IDisposable
{
    public string Ruta { get; } = Path.Combine(Path.GetTempPath(), "stackdev-t-" + Guid.NewGuid().ToString("N"));
    public CarpetaTemporal() => Directory.CreateDirectory(Ruta);
    public string En(params string[] p) => Path.Combine(new[] { Ruta }.Concat(p).ToArray());
    public void Dispose() { try { Directory.Delete(Ruta, true); } catch (IOException) { } }
}

public class StateStoreTests
{
    [Fact]
    public void La_clave_del_proyecto_es_estable_y_no_distingue_mayusculas()
    {
        // La parte del hash sale de la ruta en minúsculas; el nombre conserva las mayúsculas de la carpeta.
        var a = StateStore.Clave(Path.Combine(Path.GetTempPath(), "dev", "Pulperia"));
        var b = StateStore.Clave(Path.Combine(Path.GetTempPath(), "DEV", "pulperia"));
        Assert.Equal(a[(a.LastIndexOf('-') + 1)..], b[(b.LastIndexOf('-') + 1)..]);
        Assert.NotEqual(a, StateStore.Clave(Path.Combine(Path.GetTempPath(), "dev", "otro")));
        Assert.Matches(@"^Pulperia-[0-9a-f]{8}$", a);
    }

    [Fact]
    public void Migra_desde_flutter_dev_sin_borrar_el_origen_ni_pisar_lo_que_ya_hay()
    {
        using var t = new CarpetaTemporal();
        var appData = t.Ruta;
        var viejo = StateStore.DirAnterior(appData);
        Directory.CreateDirectory(Path.Combine(viejo, "respaldos", "pulperia-abcd1234"));
        File.WriteAllText(Path.Combine(viejo, "ultimo-proyecto.txt"), @"V:\dev\pulperia");
        File.WriteAllText(Path.Combine(viejo, "ultimo-avd.txt"), "Resizable_Experimental");
        File.WriteAllText(Path.Combine(viejo, "respaldos", "pulperia-abcd1234", "pulperia-20260101-120000.sql"), "-- sql");

        var s = StateStore.Abrir(appData);

        Assert.Equal(@"V:\dev\pulperia", s.UltimoProyecto);
        Assert.Equal("Resizable_Experimental", s.UltimoAvdGlobal);
        Assert.True(File.Exists(Path.Combine(s.Dir, "respaldos", "pulperia-abcd1234", "pulperia-20260101-120000.sql")));
        Assert.True(File.Exists(Path.Combine(viejo, "ultimo-proyecto.txt")));   // el script viejo sigue funcionando
        Assert.EndsWith("stackdev", s.Dir);

        // segunda apertura: no vuelve a migrar ni pisa lo que el usuario ya cambió
        s.UltimoProyecto = @"V:\dev\otro";
        var s2 = StateStore.Abrir(appData);
        Assert.Equal(@"V:\dev\otro", s2.UltimoProyecto);
    }

    [Fact]
    public void Sin_carpeta_anterior_simplemente_empieza_vacio()
    {
        using var t = new CarpetaTemporal();
        var s = StateStore.Abrir(t.Ruta);
        Assert.Null(s.UltimoProyecto);
        Assert.Null(s.UltimoAvdGlobal);
        Assert.Empty(s.LeerRecientes("x"));
    }

    [Fact]
    public void Migrar_forzado_trae_solo_lo_que_es_mas_nuevo()
    {
        using var t = new CarpetaTemporal();
        var viejo = t.En("viejo"); var nuevo = new StateStore(t.En("nuevo"));
        Directory.CreateDirectory(viejo);
        File.WriteAllText(Path.Combine(viejo, "ultimo-avd.txt"), "A");
        nuevo.Migrar(viejo);

        File.WriteAllText(Path.Combine(viejo, "ultimo-avd.txt"), "B");
        File.SetLastWriteTimeUtc(Path.Combine(viejo, "ultimo-avd.txt"), DateTime.UtcNow.AddMinutes(5));
        var r = nuevo.Migrar(viejo, forzar: true);

        Assert.Equal(1, r.Copiados);
        Assert.Equal("B", nuevo.UltimoAvdGlobal);
    }

    [Fact]
    public void El_emulador_por_defecto_sigue_el_orden_parametro_proyecto_config_general()
    {
        using var t = new CarpetaTemporal();
        var s = new StateStore(t.Ruta);
        Assert.Null(s.ResolverAvd("p-1", null, null));

        s.GuardarUltimoAvd("otro-9", "Pixel_General");
        Assert.Equal("Pixel_General", s.ResolverAvd("p-1", null, null));            // el general
        Assert.Equal("Del_Config", s.ResolverAvd("p-1", null, "Del_Config"));       // movil.avd gana al general
        s.GuardarUltimoAvd("p-1", "Pixel_Proyecto");
        Assert.Equal("Pixel_Proyecto", s.ResolverAvd("p-1", null, "Del_Config"));   // el del proyecto gana a la config
        Assert.Equal("Param", s.ResolverAvd("p-1", "Param", "Del_Config"));         // el parámetro gana a todos
    }

    [Fact]
    public void Las_condiciones_se_guardan_por_AVD_en_el_orden_de_siempre_y_se_borra_el_archivo_al_quedar_vacias()
    {
        using var t = new CarpetaTemporal();
        var s = new StateStore(t.Ruta);

        s.GuardarCondiciones("Pixel", new Dictionary<string, string> { ["Tema"] = "Oscuro", ["Red"] = "Modo avion", ["Ubicacion"] = "14.07, -87.19" });
        var archivo = File.ReadAllLines(Path.Combine(t.Ruta, "condiciones-Pixel.txt"));
        Assert.Equal(new[] { "Red=Modo avion", "Tema=Oscuro", "Ubicacion=14.07, -87.19" }, archivo);

        var leidas = s.LeerCondiciones("Pixel");
        Assert.Equal("Oscuro", leidas["Tema"]);
        Assert.Empty(s.LeerCondiciones("OtroAvd"));

        s.GuardarCondiciones("Pixel", new Dictionary<string, string>());
        Assert.False(File.Exists(Path.Combine(t.Ruta, "condiciones-Pixel.txt")));
    }

    [Fact]
    public void Los_comandos_recientes_guardan_la_carpeta_sin_repetir_y_solo_los_ultimos_10()
    {
        using var t = new CarpetaTemporal();
        var s = new StateStore(t.Ruta);

        for (var i = 1; i <= 12; i++) s.AgregarReciente("p-1", "api", $"cmd {i}");
        s.AgregarReciente("p-1", "web", "pnpm lint");
        s.AgregarReciente("p-1", "api", "cmd 8");   // repetido: sube al primer lugar

        var l = s.LeerRecientes("p-1");
        Assert.Equal(10, l.Count);
        Assert.Equal(new StateStore.ComandoReciente("api", "cmd 8"), l[0]);
        Assert.Equal(new StateStore.ComandoReciente("web", "pnpm lint"), l[1]);
        Assert.Single(l, c => c.Comando == "cmd 8");
        Assert.Equal(new StateStore.ComandoReciente("api", "cmd 12"), l[2]);
    }

    [Fact]
    public void Lee_el_formato_viejo_de_recientes_sin_carpeta()
    {
        using var t = new CarpetaTemporal();
        File.WriteAllLines(Path.Combine(t.Ruta, "comandos-p-1.txt"), new[] { "flutter pub get", "api\tdotnet build" });
        var l = new StateStore(t.Ruta).LeerRecientes("p-1");

        Assert.Equal(new StateStore.ComandoReciente(".", "flutter pub get"), l[0]);
        Assert.Equal(new StateStore.ComandoReciente("api", "dotnet build"), l[1]);
    }
}

public class PostgresDockerTests
{
    [Fact]
    public void Filtra_solo_los_contenedores_con_imagen_postgres_o_postgis()
    {
        var salida = "pulperia-pg|postgres:17-alpine\nredis|redis:7\r\ngeo|postgis/postgis:16-3.4\nweb|nginx\n";
        var l = PostgresDocker.FiltrarContenedores(salida);

        Assert.Equal(new[] { "pulperia-pg", "geo" }, l.Select(c => c.Nombre).ToArray());
        Assert.Equal("postgis/postgis:16-3.4", l[1].Imagen);
    }

    private const string Inspect = """
        { "Name": "/pulperia-pg",
          "Config": { "Image": "postgres:17-alpine",
                      "Env": ["POSTGRES_USER=pulperia", "POSTGRES_PASSWORD=dev", "POSTGRES_DB=pulperia", "PATH=/usr/bin"] },
          "HostConfig": { "PortBindings": { "5432/tcp": [ { "HostIp": "", "HostPort": "5440" } ] } },
          "Mounts": [ { "Type": "volume", "Name": "VOLUMEN", "Destination": "/var/lib/postgresql/data" } ] }
        """;

    [Fact]
    public void Lee_imagen_puerto_usuario_password_base_y_volumen_con_nombre()
    {
        var pg = PostgresDocker.DesdeInspect("pulperia-pg", Inspect.Replace("VOLUMEN", "pulperia-data"))!;

        Assert.Equal("pulperia-pg", pg.Contenedor);
        Assert.Equal("postgres:17-alpine", pg.Imagen);
        Assert.Equal(5440, pg.Puerto);
        Assert.Equal("pulperia", pg.Usuario);
        Assert.Equal("dev", pg.Password);
        Assert.Equal("pulperia", pg.BaseDatos);
        Assert.Equal("pulperia-data", pg.Volumen);
    }

    [Fact]
    public void Un_volumen_anonimo_cuenta_como_sin_volumen()
    {
        var pg = PostgresDocker.DesdeInspect("x", Inspect.Replace("VOLUMEN", new string('a', 64)))!;
        Assert.Equal("", pg.Volumen);
    }

    [Fact]
    public void Sin_variables_ni_puertos_usa_los_valores_de_postgres_por_defecto()
    {
        var pg = PostgresDocker.DesdeInspect("x", """{ "Config": { "Image": "postgres:16", "Env": [] } }""")!;

        Assert.Equal(5432, pg.Puerto);
        Assert.Equal("postgres", pg.Usuario);
        Assert.Equal("postgres", pg.BaseDatos);
        Assert.Equal("", pg.Password);
    }

    [Fact]
    public void Un_json_invalido_devuelve_null()
    {
        Assert.Null(PostgresDocker.DesdeInspect("x", "Error: No such object: x"));
    }
}

public class AndroidSdkTests
{
    [Fact]
    public void Lee_android_sdk_de_flutter_settings_y_convierte_las_barras()
    {
        Assert.Equal(@"V:\Programas\Android\Sdk",
            AndroidSdkLocator.DesdeFlutterSettings("{ \"android-sdk\": \"V:\\\\Programas\\\\Android\\\\Sdk\" }"));
        Assert.Null(AndroidSdkLocator.DesdeFlutterSettings("{}"));
    }

    [Fact]
    public void Respeta_el_orden_de_busqueda()
    {
        var validos = new HashSet<string> { "ENV_ROOT", "ENV_HOME", "FLUTTER", Path.Combine("LOCAL", "Android", "Sdk"), "CFG" };
        string? Var(string n) => n == "ANDROID_SDK_ROOT" ? "ENV_ROOT" : n == "ANDROID_HOME" ? "ENV_HOME" : null;
        const string flutter = "{ \"android-sdk\": \"FLUTTER\" }";

        Assert.Equal("CFG", AndroidSdkLocator.Buscar("CFG", Var, flutter, "LOCAL", validos.Contains));
        Assert.Equal("ENV_ROOT", AndroidSdkLocator.Buscar(null, Var, flutter, "LOCAL", validos.Contains));
        Assert.Equal("ENV_HOME", AndroidSdkLocator.Buscar("no-existe", n => n == "ANDROID_HOME" ? "ENV_HOME" : null, flutter, "LOCAL", validos.Contains));
        Assert.Equal("FLUTTER", AndroidSdkLocator.Buscar(null, _ => null, flutter, "LOCAL", validos.Contains));
        Assert.Equal(Path.Combine("LOCAL", "Android", "Sdk"), AndroidSdkLocator.Buscar(null, _ => null, null, "LOCAL", validos.Contains));
        Assert.Null(AndroidSdkLocator.Buscar(null, _ => null, null, null, validos.Contains));
    }

    [Fact]
    public void Las_variables_del_sdk_son_solo_para_los_procesos_lanzados()
    {
        var v = AndroidSdkLocator.VariablesDeProceso(@"V:\Programas\Android\Sdk");
        Assert.Equal(@"V:\Programas\Android\Sdk", v["ANDROID_SDK_ROOT"]);
        Assert.Equal(@"V:\Programas\Android\Sdk", v["ANDROID_HOME"]);
    }
}

public class RaizProyectoTests
{
    [Fact]
    public void Una_carpeta_es_proyecto_si_tiene_git_config_pubspec_package_o_solucion()
    {
        using var t = new CarpetaTemporal();
        Assert.False(RaizProyecto.EsProyecto(t.Ruta));
        Assert.False(RaizProyecto.EsProyecto(t.En("no-existe")));

        foreach (var marca in new[] { "dev-menu.json", "pubspec.yaml", "package.json", "App.sln", "App.slnx", "App.csproj" })
        {
            using var u = new CarpetaTemporal();
            File.WriteAllText(u.En(marca), "");
            Assert.True(RaizProyecto.EsProyecto(u.Ruta), marca);
        }
        using var g = new CarpetaTemporal();
        Directory.CreateDirectory(g.En(".git"));
        Assert.True(RaizProyecto.EsProyecto(g.Ruta));
    }

    [Fact]
    public void Si_eligen_una_subcarpeta_se_sugiere_la_raiz_del_repo()
    {
        using var t = new ArbolPulperia();
        Assert.Equal(t.Raiz, RaizProyecto.SugerirPadre(t.Ruta("mobile")));
        Assert.Null(RaizProyecto.SugerirPadre(t.Raiz));                     // ya es la raíz
        File.WriteAllText(t.Ruta("mobile/dev-menu.json"), "{}");
        Assert.Null(RaizProyecto.SugerirPadre(t.Ruta("mobile")));           // tiene su propia configuración
    }

    [Fact]
    public void Resolver_sigue_el_orden_parametro_carpeta_actual_ultimo_selector()
    {
        using var proyecto = new ArbolPulperia();
        using var vacia = new CarpetaTemporal();
        using var ultimo = new CarpetaTemporal();

        var r = RaizProyecto.Resolver(vacia.Ruta, proyecto.Raiz, ultimo.Ruta);
        Assert.Equal(OrigenProyecto.Parametro, r.Origen);

        r = RaizProyecto.Resolver(null, proyecto.Raiz, ultimo.Ruta);
        Assert.Equal(OrigenProyecto.CarpetaActual, r.Origen);

        r = RaizProyecto.Resolver(null, vacia.Ruta, ultimo.Ruta);
        Assert.Equal(OrigenProyecto.UltimoUsado, r.Origen);
        Assert.Equal(ultimo.Ruta, r.Ruta);

        r = RaizProyecto.Resolver(null, vacia.Ruta, vacia.En("borrada"));
        Assert.Equal(OrigenProyecto.Selector, r.Origen);

        r = RaizProyecto.Resolver(vacia.En("no-existe"), proyecto.Raiz, ultimo.Ruta);   // parámetro malo: no cae a la carpeta actual
        Assert.Equal(OrigenProyecto.Selector, r.Origen);
        Assert.True(r.ParametroInvalido);
    }
}
