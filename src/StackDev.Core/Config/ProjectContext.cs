using StackDev.Core.Util;

namespace StackDev.Core.Config;

public enum ProjectComponent { Postgres, Api, Web, Mobile }

/// <summary>Raíz del proyecto + su configuración: carpetas, puertos y variables de entorno ya resueltos.</summary>
public sealed class ProjectContext
{
    public ProjectContext(string root, ProjectConfig config)
    {
        Root = root;
        Config = config;
    }

    public string Root { get; }
    public ProjectConfig Config { get; }

    public string Name => string.IsNullOrEmpty(Config.Name) ? Paths.FolderName(Root) : Config.Name;

    /// <summary>Una sección ausente en el archivo significa que esa parte no existe en el proyecto.</summary>
    public bool Has(ProjectComponent c) => c switch
    {
        ProjectComponent.Postgres => Config.Postgres is not null,
        ProjectComponent.Api => Config.Api is not null,
        ProjectComponent.Web => Config.Web is not null,
        ProjectComponent.Mobile => Config.Mobile is not null,
        _ => false,
    };

    /// <summary>Carpeta (ruta completa) donde se ejecutan los comandos del componente.</summary>
    public string Folder(ProjectComponent c)
    {
        var rel = c switch
        {
            ProjectComponent.Api => Config.Api?.Folder,
            ProjectComponent.Web => Config.Web?.Folder,
            ProjectComponent.Mobile => Config.Mobile?.Folder,
            _ => null,
        };
        return Paths.Resolve(Root, rel);
    }

    public string? Url(ProjectComponent c) => c switch
    {
        ProjectComponent.Api => Config.Api?.Url,
        ProjectComponent.Web => Config.Web?.Url,
        _ => null,
    };

    /// <summary>Puerto de la URL del componente; 0 si no hay una URL válida.</summary>
    public int Port(ProjectComponent c) => PortFromUrl(Url(c));

    public static int PortFromUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Port : 0;

    /// <summary>
    /// Variables de entorno que aplican a una carpeta: <c>api.env</c> vale para la API y para
    /// cualquier comando dentro de la carpeta de la API (así <c>dotnet ef</c> funciona sin preparar nada).
    /// </summary>
    public IReadOnlyDictionary<string, string>? EnvForFolder(string folder)
    {
        if (Config.Api is null) return null;
        return Paths.IsSameOrSubfolder(Folder(ProjectComponent.Api), folder) ? Config.Api.Env : null;
    }

    /// <summary>URL que se abre en el navegador (la API suma <c>api.abrir</c>).</summary>
    public string? UrlToOpen(ProjectComponent c)
    {
        var url = Url(c);
        if (string.IsNullOrEmpty(url)) return null;
        return c == ProjectComponent.Api ? url.TrimEnd('/') + (Config.Api?.Open ?? "") : url;
    }

    /// <summary>Lo que hace "levantar todo"; si el archivo no lo define, el orden de siempre.</summary>
    public IReadOnlyList<string> StartAll =>
        Config.StartAll is { Count: > 0 } l ? l : new[] { "postgres", "api", "web", "emulador" };

    public IReadOnlyList<TaskConfig> Tasks => Config.Tasks ?? new List<TaskConfig>();
}
