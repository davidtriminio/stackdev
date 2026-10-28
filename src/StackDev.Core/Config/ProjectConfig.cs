using System.Text.Json;
using System.Text.Json.Serialization;

namespace StackDev.Core.Config;

/// <summary>
/// Contents of dev-menu.json. The JSON property names are the file's keys;
/// unknown fields are kept when saving (<see cref="Extra"/>).
/// </summary>
public sealed class ProjectConfig : ConfigSection
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    /// <summary>Browser used to open this project (known id or path to an .exe); empty = the one set for all projects.</summary>
    [JsonPropertyName("browser")] public string? Browser { get; set; }
    [JsonPropertyName("postgres")] public PostgresConfig? Postgres { get; set; }
    [JsonPropertyName("api")] public ApiConfig? Api { get; set; }
    [JsonPropertyName("web")] public WebConfig? Web { get; set; }
    [JsonPropertyName("mobile")] public MobileConfig? Mobile { get; set; }
    /// <summary>Where interactive sessions open (flutter run, psql...): "auto", "wt" (Windows Terminal) or "ventana" (a separate window).</summary>
    [JsonPropertyName("terminal")] public string? Terminal { get; set; }
    [JsonPropertyName("startAll")] public List<string>? StartAll { get; set; }
    [JsonPropertyName("tasks")] public List<TaskConfig>? Tasks { get; set; }
    /// <summary>Which server and service this project is uploaded to.</summary>
    [JsonPropertyName("deployment")] public DeploymentConfig? Deployment { get; set; }
}

/// <summary>Where this project's API lives on a server (the server is chosen from the global list).</summary>
public sealed class DeploymentConfig : ConfigSection
{
    /// <summary>Id of the server in the global list.</summary>
    [JsonPropertyName("server")] public string? Server { get; set; }
    /// <summary>Name of the systemd service (and of the folder in /var/www).</summary>
    [JsonPropertyName("service")] public string? Service { get; set; }
    [JsonPropertyName("dll")] public string? Dll { get; set; }
    [JsonPropertyName("domain")] public string? Domain { get; set; }
    /// <summary>The API's .csproj, relative to the project folder (found automatically the first time).</summary>
    [JsonPropertyName("project")] public string? Project { get; set; }
}

/// <summary>Base of all sections: keeps the fields this version does not know.</summary>
public abstract class ConfigSection
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>An empty text is the same as "not defined".</summary>
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

    /// <summary>Postgres 18+ stores data in /var/lib/postgresql; before that in /var/lib/postgresql/data.</summary>
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
