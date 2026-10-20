using System.Text.Json.Nodes;
using StackDev.Core.Config;
using Xunit;

namespace StackDev.Core.Tests;

public class ConfigTests
{
    [Fact]
    public void Lee_el_dev_menu_json_actual_de_shop()
    {
        var c = ConfigJson.Leer(ShopTree.ConfigActual);

        Assert.Equal("shop", c.Nombre);
        Assert.Equal(5440, c.Postgres!.Puerto);
        Assert.Equal("shop-pg", c.Postgres.Contenedor);
        Assert.Equal("", c.Postgres.Volumen);
        Assert.Equal("/scalar", c.Api!.Abrir);
        Assert.Equal("Host=127.0.0.1;Port=5440;Database=shop;Username=shop;Password=dev", c.Api.Env!["ConnectionStrings__Shop"]);
        Assert.Equal("pnpm install", c.Web!.Instalar);
        Assert.Equal(@"C:\Programas\Android\Sdk", c.Movil!.AndroidSdk);
        Assert.Equal(new[] { "postgres", "api", "web", "emulador" }, c.LevantarTodo);
        Assert.Equal(3, c.Tareas!.Count);
    }

    [Fact]
    public void Escribir_y_volver_a_leer_da_el_mismo_contenido_JSON()
    {
        var original = ConfigJson.Leer(ShopTree.ConfigActual);
        var texto = ConfigJson.Escribir(original);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ShopTree.ConfigActual), JsonNode.Parse(texto)),
            "El archivo reescrito no es equivalente al original:\n" + texto);
    }

    [Fact]
    public void Salida_legible_sin_escapar_comillas_simples_ni_tildes_y_con_saltos_de_linea_LF()
    {
        var c = new ProyectoConfig
        {
            Nombre = "shop",
            Tareas = new() { new TareaConfig { Nombre = "Migración 'nueva'", Carpeta = "api", Comando = "echo \"hola\" && dir" } },
        };
        var texto = ConfigJson.Escribir(c);

        Assert.Contains("\"nombre\": \"shop\"", texto);
        Assert.Contains("Migración 'nueva'", texto);            // sin \u0027 ni \u00F3
        Assert.Contains("\\\"hola\\\"", texto);                  // las comillas dobles sí se escapan
        Assert.DoesNotContain("\r", texto);
        Assert.StartsWith("{\n  \"nombre\"", texto);              // sangría de 2 espacios
        Assert.EndsWith("}\n", texto);
        Assert.DoesNotContain("\"postgres\"", texto);             // sección ausente: no se escribe
    }

    [Fact]
    public void Conserva_los_campos_nuevos_que_esta_version_no_conoce()
    {
        const string json = """
            { "nombre": "x", "campoFuturo": { "a": 1 }, "api": { "carpeta": "api", "timeoutSeg": 30 } }
            """;
        var c = ConfigJson.Leer(json);
        var otra = JsonNode.Parse(ConfigJson.Escribir(c))!;

        Assert.Equal(1, (int)otra["campoFuturo"]!["a"]!);
        Assert.Equal(30, (int)otra["api"]!["timeoutSeg"]!);
    }

    [Fact]
    public void Tolera_comentarios_comas_finales_y_el_puerto_escrito_como_texto()
    {
        const string json = """
            {
              // el puerto del contenedor
              "postgres": { "puerto": "5440", },
            }
            """;
        Assert.Equal(5440, ConfigJson.Leer(json).Postgres!.Puerto);
    }

    [Fact]
    public void Valores_efectivos_de_postgres_usan_los_mismos_defectos_que_el_script()
    {
        var pg = new PostgresConfig { Usuario = "shop" };
        Assert.Equal(5432, pg.PuertoEf);
        Assert.Equal("shop", pg.BaseDatosEf);          // sin baseDatos: el usuario
        Assert.Equal("postgres:17-alpine", pg.ImagenEf);
        Assert.Equal("postgres", new PostgresConfig().UsuarioEf);
    }

    [Theory]
    [InlineData("postgres:17-alpine", "/var/lib/postgresql/data")]
    [InlineData("postgres:16", "/var/lib/postgresql/data")]
    [InlineData("postgres:18", "/var/lib/postgresql")]
    [InlineData("postgres:18-alpine", "/var/lib/postgresql")]
    [InlineData("postgres", "/var/lib/postgresql/data")]
    [InlineData("postgis/postgis:19-3.6", "/var/lib/postgresql")]
    public void Ruta_de_datos_segun_la_version_de_postgres(string imagen, string esperado) =>
        Assert.Equal(esperado, PostgresConfig.RutaDatos(imagen));

    [Fact]
    public void ConfigStore_crea_guarda_recarga_y_deja_copia_bak()
    {
        using var t = new ShopTree();
        var store = new ConfigStore(t.Raiz);
        Assert.False(store.Existe);

        store.Guardar(ConfigJson.Leer(ShopTree.ConfigActual));
        Assert.True(store.Existe);
        Assert.False(store.RecargarSiCambio());                 // nada cambió

        // el usuario edita el archivo
        File.WriteAllText(store.Ruta, ShopTree.ConfigActual.Replace("5440", "5441"));
        File.SetLastWriteTimeUtc(store.Ruta, DateTime.UtcNow.AddMinutes(1));
        Assert.True(store.RecargarSiCambio());
        Assert.Equal(5441, store.Config.Postgres!.Puerto);
        Assert.False(store.RecargarSiCambio());

        store.Reemplazar(new ProyectoConfig { Nombre = "nuevo" });
        Assert.True(File.Exists(store.Ruta + ".bak"));
        Assert.Contains("5441", File.ReadAllText(store.Ruta + ".bak"));
        Assert.Equal("nuevo", ConfigJson.Leer(File.ReadAllText(store.Ruta)).Nombre);
    }

    [Fact]
    public void ConfigStore_con_json_roto_deja_el_menu_vacio_y_avisa_sin_perder_el_archivo()
    {
        using var t = new ShopTree();
        var store = new ConfigStore(t.Raiz);
        File.WriteAllText(store.Ruta, "{ esto no es json");

        store.Cargar();

        Assert.NotNull(store.Error);
        Assert.Null(store.Config.Api);
        Assert.Equal("{ esto no es json", File.ReadAllText(store.Ruta));
        Assert.False(store.RecargarSiCambio());                 // no repite el error en cada vuelta
    }
}
