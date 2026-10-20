using StackDev.Core.Util;

namespace StackDev.Core.Config;

public enum Componente { Postgres, Api, Web, Movil }

/// <summary>Raíz del proyecto + su configuración: carpetas, puertos y variables de entorno ya resueltos.</summary>
public sealed class ProyectoContexto
{
    public ProyectoContexto(string raiz, ProyectoConfig config)
    {
        Raiz = raiz;
        Config = config;
    }

    public string Raiz { get; }
    public ProyectoConfig Config { get; }

    public string Nombre => string.IsNullOrEmpty(Config.Nombre) ? Rutas.NombreCarpeta(Raiz) : Config.Nombre;

    /// <summary>Una sección ausente en el archivo significa que esa parte no existe en el proyecto.</summary>
    public bool Tiene(Componente c) => c switch
    {
        Componente.Postgres => Config.Postgres is not null,
        Componente.Api => Config.Api is not null,
        Componente.Web => Config.Web is not null,
        Componente.Movil => Config.Movil is not null,
        _ => false,
    };

    /// <summary>Carpeta (ruta completa) donde se ejecutan los comandos del componente.</summary>
    public string Carpeta(Componente c)
    {
        var rel = c switch
        {
            Componente.Api => Config.Api?.Carpeta,
            Componente.Web => Config.Web?.Carpeta,
            Componente.Movil => Config.Movil?.Carpeta,
            _ => null,
        };
        return Rutas.Resolver(Raiz, rel);
    }

    public string? Url(Componente c) => c switch
    {
        Componente.Api => Config.Api?.Url,
        Componente.Web => Config.Web?.Url,
        _ => null,
    };

    /// <summary>Puerto de la URL del componente; 0 si no hay una URL válida.</summary>
    public int Puerto(Componente c) => PuertoDeUrl(Url(c));

    public static int PuertoDeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Port : 0;

    /// <summary>
    /// Variables de entorno que aplican a una carpeta: <c>api.env</c> vale para la API y para
    /// cualquier comando dentro de la carpeta de la API (así <c>dotnet ef</c> funciona sin preparar nada).
    /// </summary>
    public IReadOnlyDictionary<string, string>? EnvParaCarpeta(string carpeta)
    {
        if (Config.Api is null) return null;
        return Rutas.EsIgualOSubcarpeta(Carpeta(Componente.Api), carpeta) ? Config.Api.Env : null;
    }

    /// <summary>URL que se abre en el navegador (la API suma <c>api.abrir</c>).</summary>
    public string? UrlParaAbrir(Componente c)
    {
        var url = Url(c);
        if (string.IsNullOrEmpty(url)) return null;
        return c == Componente.Api ? url.TrimEnd('/') + (Config.Api?.Abrir ?? "") : url;
    }

    /// <summary>Lo que hace "levantar todo"; si el archivo no lo define, el orden de siempre.</summary>
    public IReadOnlyList<string> LevantarTodo =>
        Config.LevantarTodo is { Count: > 0 } l ? l : new[] { "postgres", "api", "web", "emulador" };

    public IReadOnlyList<TareaConfig> Tareas => Config.Tareas ?? new List<TareaConfig>();
}
