using System.Text.Json;
using System.Text.Json.Serialization;

namespace StackDev.Core.Config;

/// <summary>
/// Contenido de dev-menu.json. Los nombres de las propiedades JSON son los del archivo que ya existe
/// (en español); los campos desconocidos se conservan al guardar (<see cref="Extra"/>).
/// </summary>
public sealed class ProyectoConfig : ConfigSeccion
{
    [JsonPropertyName("nombre")] public string? Nombre { get; set; }
    /// <summary>Navegador para abrir este proyecto (id conocido o ruta de un .exe); vacío = el que se definió para todos.</summary>
    [JsonPropertyName("navegador")] public string? Navegador { get; set; }
    [JsonPropertyName("postgres")] public PostgresConfig? Postgres { get; set; }
    [JsonPropertyName("api")] public ApiConfig? Api { get; set; }
    [JsonPropertyName("web")] public WebConfig? Web { get; set; }
    [JsonPropertyName("movil")] public MovilConfig? Movil { get; set; }
    /// <summary>Dónde se abren las sesiones interactivas (flutter run, psql...): "auto", "wt" (Windows Terminal) o "ventana".</summary>
    [JsonPropertyName("terminal")] public string? Terminal { get; set; }
    [JsonPropertyName("levantarTodo")] public List<string>? LevantarTodo { get; set; }
    [JsonPropertyName("tareas")] public List<TareaConfig>? Tareas { get; set; }
    /// <summary>A qué servidor y servicio se sube este proyecto.</summary>
    [JsonPropertyName("despliegue")] public DespliegueConfig? Despliegue { get; set; }
}

/// <summary>Dónde vive la API de este proyecto en un servidor (el servidor se elige de la lista global).</summary>
public sealed class DespliegueConfig : ConfigSeccion
{
    /// <summary>Id del servidor en la lista global.</summary>
    [JsonPropertyName("servidor")] public string? Servidor { get; set; }
    /// <summary>Nombre del servicio systemd (y de la carpeta en /var/www).</summary>
    [JsonPropertyName("servicio")] public string? Servicio { get; set; }
    [JsonPropertyName("dll")] public string? Dll { get; set; }
    [JsonPropertyName("dominio")] public string? Dominio { get; set; }
}

/// <summary>Base de todas las secciones: guarda los campos que esta versión no conoce.</summary>
public abstract class ConfigSeccion
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Como Get-Cfg del script: un texto vacío equivale a "no definido".</summary>
    internal static string Or(string? valor, string defecto) => string.IsNullOrEmpty(valor) ? defecto : valor;
}

public sealed class PostgresConfig : ConfigSeccion
{
    [JsonPropertyName("contenedor")] public string? Contenedor { get; set; }
    [JsonPropertyName("imagen")] public string? Imagen { get; set; }
    [JsonPropertyName("puerto")] public int? Puerto { get; set; }
    [JsonPropertyName("usuario")] public string? Usuario { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("baseDatos")] public string? BaseDatos { get; set; }
    [JsonPropertyName("volumen")] public string? Volumen { get; set; }

    [JsonIgnore] public string ContenedorEf => Or(Contenedor, "");
    [JsonIgnore] public string ImagenEf => Or(Imagen, "postgres:17-alpine");
    [JsonIgnore] public int PuertoEf => Puerto is > 0 ? Puerto.Value : 5432;
    [JsonIgnore] public string UsuarioEf => Or(Usuario, "postgres");
    [JsonIgnore] public string PasswordEf => Password ?? "";
    [JsonIgnore] public string BaseDatosEf => Or(BaseDatos, UsuarioEf);
    [JsonIgnore] public string VolumenEf => Volumen ?? "";

    /// <summary>Postgres 18+ guarda los datos en /var/lib/postgresql; antes en /var/lib/postgresql/data.</summary>
    public static string RutaDatos(string imagen)
    {
        var m = System.Text.RegularExpressions.Regex.Match(imagen, @":(\d+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var version) && version >= 18)
            return "/var/lib/postgresql";
        return "/var/lib/postgresql/data";
    }
}

public sealed class ApiConfig : ConfigSeccion
{
    [JsonPropertyName("carpeta")] public string? Carpeta { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("abrir")] public string? Abrir { get; set; }
    [JsonPropertyName("env")] public Dictionary<string, string>? Env { get; set; }
    [JsonPropertyName("iniciar")] public string? Iniciar { get; set; }
    [JsonPropertyName("test")] public string? Test { get; set; }
    [JsonPropertyName("build")] public string? Build { get; set; }
}

public sealed class WebConfig : ConfigSeccion
{
    [JsonPropertyName("carpeta")] public string? Carpeta { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("iniciar")] public string? Iniciar { get; set; }
    [JsonPropertyName("test")] public string? Test { get; set; }
    [JsonPropertyName("build")] public string? Build { get; set; }
    [JsonPropertyName("instalar")] public string? Instalar { get; set; }
}

public sealed class MovilConfig : ConfigSeccion
{
    [JsonPropertyName("carpeta")] public string? Carpeta { get; set; }
    [JsonPropertyName("androidSdk")] public string? AndroidSdk { get; set; }
    [JsonPropertyName("avd")] public string? Avd { get; set; }
    [JsonPropertyName("argsRun")] public string? ArgsRun { get; set; }
    [JsonPropertyName("analizar")] public string? Analizar { get; set; }
    [JsonPropertyName("test")] public string? Test { get; set; }
    [JsonPropertyName("formato")] public string? Formato { get; set; }
    [JsonPropertyName("generar")] public string? Generar { get; set; }
}

public sealed class TareaConfig : ConfigSeccion
{
    [JsonPropertyName("nombre")] public string? Nombre { get; set; }
    [JsonPropertyName("carpeta")] public string? Carpeta { get; set; }
    [JsonPropertyName("comando")] public string? Comando { get; set; }
}
