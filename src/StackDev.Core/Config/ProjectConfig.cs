using System.Text.Json;
using System.Text.Json.Serialization;

namespace StackDev.Core.Config;

/// <summary>
/// Contenido de dev-menu.json. Los nombres de las propiedades JSON son los del archivo que ya existe
/// (en español); los campos desconocidos se conservan al guardar (<see cref="Extra"/>).
/// </summary>
public sealed class ProjectConfig : ConfigSection
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    /// <summary>Navegador para abrir este proyecto (id conocido o ruta de un .exe); vacío = el que se definió para todos.</summary>
    [JsonPropertyName("browser")] public string? Browser { get; set; }
    [JsonPropertyName("postgres")] public PostgresConfig? Postgres { get; set; }
    [JsonPropertyName("api")] public ApiConfig? Api { get; set; }
    [JsonPropertyName("web")] public WebConfig? Web { get; set; }
    [JsonPropertyName("mobile")] public MobileConfig? Mobile { get; set; }
    /// <summary>Dónde se abren las sesiones interactivas (flutter run, psql...): "auto", "wt" (Windows Terminal) o "ventana".</summary>
    [JsonPropertyName("terminal")] public string? Terminal { get; set; }
    [JsonPropertyName("startAll")] public List<string>? StartAll { get; set; }
    [JsonPropertyName("tasks")] public List<TaskConfig>? Tasks { get; set; }
    /// <summary>A qué servidor y servicio se sube este proyecto.</summary>
    [JsonPropertyName("deployment")] public DeploymentConfig? Deployment { get; set; }
}

/// <summary>Dónde vive la API de este proyecto en un servidor (el servidor se elige de la lista global).</summary>
public sealed class DeploymentConfig : ConfigSection
{
    /// <summary>Id del servidor en la lista global.</summary>
    [JsonPropertyName("server")] public string? Server { get; set; }
    /// <summary>Nombre del servicio systemd (y de la carpeta en /var/www).</summary>
    [JsonPropertyName("service")] public string? Service { get; set; }
    [JsonPropertyName("dll")] public string? Dll { get; set; }
    [JsonPropertyName("domain")] public string? Domain { get; set; }
    /// <summary>The API's .csproj, relative to the project folder (found automatically the first time).</summary>
    [JsonPropertyName("project")] public string? Project { get; set; }
}

/// <summary>Base de todas las secciones: guarda los campos que esta versión no conoce.</summary>
public abstract class ConfigSection
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Como Get-Cfg del script: un texto vacío equivale a "no definido".</summary>
    internal static string Or(string? val, string fallback) => string.IsNullOrEmpty(val) ? fallback : val;
}

public sealed class PostgresConfig : ConfigSection
{
    [JsonPropertyName("container")] public string? Container { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("port")] public int? Port { get; set; }
    [JsonPropertyName("user")] public string? User { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("database")] public string? Database { get; set; }
    [JsonPropertyName("volume")] public string? Volume { get; set; }

    [JsonIgnore] public string EfContainer => Or(Container, "");
    [JsonIgnore] public string EfImage => Or(Image, "postgres:17-alpine");
    [JsonIgnore] public int EfPort => Port is > 0 ? Port.Value : 5432;
    [JsonIgnore] public string EfUser => Or(User, "postgres");
    [JsonIgnore] public string EfPassword => Password ?? "";
    [JsonIgnore] public string EfDatabase => Or(Database, EfUser);
    [JsonIgnore] public string EfVolume => Volume ?? "";

    /// <summary>Postgres 18+ guarda los datos en /var/lib/postgresql; antes en /var/lib/postgresql/data.</summary>
    public static string DataPath(string image)
    {
        var m = System.Text.RegularExpressions.Regex.Match(image, @":(\d+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var version) && version >= 18)
            return "/var/lib/postgresql";
        return "/var/lib/postgresql/data";
    }
}

public sealed class ApiConfig : ConfigSection
{
    [JsonPropertyName("folder")] public string? Folder { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("open")] public string? Open { get; set; }
    [JsonPropertyName("env")] public Dictionary<string, string>? Env { get; set; }
    [JsonPropertyName("start")] public string? Start { get; set; }
    [JsonPropertyName("test")] public string? Test { get; set; }
    [JsonPropertyName("build")] public string? Build { get; set; }
}

public sealed class WebConfig : ConfigSection
{
    [JsonPropertyName("folder")] public string? Folder { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("start")] public string? Start { get; set; }
    [JsonPropertyName("test")] public string? Test { get; set; }
    [JsonPropertyName("build")] public string? Build { get; set; }
    [JsonPropertyName("install")] public string? Install { get; set; }
}

public sealed class MobileConfig : ConfigSection
{
    [JsonPropertyName("folder")] public string? Folder { get; set; }
    [JsonPropertyName("androidSdk")] public string? AndroidSdk { get; set; }
    [JsonPropertyName("avd")] public string? Avd { get; set; }
    [JsonPropertyName("argsRun")] public string? ArgsRun { get; set; }
    [JsonPropertyName("parse")] public string? Parse { get; set; }
    [JsonPropertyName("test")] public string? Test { get; set; }
    [JsonPropertyName("format")] public string? Format { get; set; }
    [JsonPropertyName("generate")] public string? Generate { get; set; }
}

public sealed class TaskConfig : ConfigSection
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("folder")] public string? Folder { get; set; }
    [JsonPropertyName("command")] public string? Command { get; set; }
}
