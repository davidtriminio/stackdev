using StackDev.Core.Util;

namespace StackDev.Core.Config;

public enum ProjectComponent { Postgres, Api, Web, Mobile }

/// <summary>Project root + its configuration: folders, ports and environment variables already resolved.</summary>
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

    /// <summary>A section missing from the file means that part does not exist in the project.</summary>
    public bool Has(ProjectComponent c) => c switch
    {
        ProjectComponent.Postgres => Config.Postgres is not null,
        ProjectComponent.Api => Config.Api is not null,
        ProjectComponent.Web => Config.Web is not null,
        ProjectComponent.Mobile => Config.Mobile is not null,
        _ => false,
    };

    /// <summary>Folder (full path) where the component's commands run.</summary>
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

    /// <summary>Port of the component's URL; 0 if there is no valid URL.</summary>
    public int Port(ProjectComponent c) => PortFromUrl(Url(c));

    public static int PortFromUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Port : 0;

    /// <summary>
    /// Environment variables that apply to a folder: <c>api.env</c> applies to the API and to
    /// any command inside the API folder (so <c>dotnet ef</c> works without preparing anything).
    /// </summary>
    public IReadOnlyDictionary<string, string>? EnvForFolder(string folder)
    {
        if (Config.Api is null) return null;
        return Paths.IsSameOrSubfolder(Folder(ProjectComponent.Api), folder) ? Config.Api.Env : null;
    }

    /// <summary>URL opened in the browser (the API adds <c>api.open</c>).</summary>
    public string? UrlToOpen(ProjectComponent c)
    {
        var url = Url(c);
        if (string.IsNullOrEmpty(url)) return null;
        return c == ProjectComponent.Api ? url.TrimEnd('/') + (Config.Api?.Open ?? "") : url;
    }

    /// <summary>What "start all" does; if the file does not define it, the usual order.</summary>
    public IReadOnlyList<string> StartAll =>
        Config.StartAll is { Count: > 0 } l ? l : new[] { "postgres", "api", "web", "emulador" };

    public IReadOnlyList<TaskConfig> Tasks => Config.Tasks ?? new List<TaskConfig>();
}
