using StackDev.Core.Config;

namespace StackDev.Core.Detection;

public enum OrigenProyecto
{
    /// <summary>Se pasó una ruta válida.</summary>
    Parametro,
    /// <summary>La carpeta actual parece un proyecto.</summary>
    CarpetaActual,
    /// <summary>Hay un último proyecto usado: hay que preguntar si se usa.</summary>
    UltimoUsado,
    /// <summary>No se pudo decidir: abrir el selector de carpeta.</summary>
    Selector,
}

public sealed record ResolucionProyecto(OrigenProyecto Origen, string? Ruta, bool ParametroInvalido = false);

/// <summary>Cómo se decide cuál es la raíz del proyecto.</summary>
public static class RaizProyecto
{
    private static readonly string[] Marcas = { ConfigStore.NombreArchivo, ".git", "pubspec.yaml", "package.json" };

    /// <summary>¿La carpeta parece la raíz de un proyecto? (tiene .git, dev-menu.json, *.sln, pubspec.yaml…)</summary>
    public static bool EsProyecto(string? ruta)
    {
        if (string.IsNullOrWhiteSpace(ruta) || !Directory.Exists(ruta)) return false;
        foreach (var m in Marcas)
        {
            var p = Path.Combine(ruta, m);
            if (File.Exists(p) || Directory.Exists(p)) return true;
        }
        try
        {
            return Directory.EnumerateFiles(ruta).Any(f =>
                f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool TieneRepoOConfig(string ruta) =>
        File.Exists(Path.Combine(ruta, ConfigStore.NombreArchivo)) ||
        Directory.Exists(Path.Combine(ruta, ".git")) || File.Exists(Path.Combine(ruta, ".git"));

    /// <summary>
    /// Si eligieron una subcarpeta (p. ej. mobile/) y la carpeta de arriba tiene .git o dev-menu.json,
    /// devuelve la de arriba para ofrecerla como raíz. Si no, null.
    /// </summary>
    public static string? SugerirPadre(string ruta)
    {
        var r = Util.Rutas.Normalizar(ruta);
        if (TieneRepoOConfig(r)) return null;
        var padre = Path.GetDirectoryName(r);
        return !string.IsNullOrEmpty(padre) && TieneRepoOConfig(padre) ? padre : null;
    }

    /// <summary>Parámetro → carpeta actual (si parece proyecto) → último proyecto usado (se pregunta) → selector.</summary>
    public static ResolucionProyecto Resolver(string? parametro, string carpetaActual, string? ultimo)
    {
        var invalido = false;
        if (!string.IsNullOrWhiteSpace(parametro))
        {
            if (Directory.Exists(parametro)) return new(OrigenProyecto.Parametro, Path.GetFullPath(parametro));
            invalido = true;
        }
        else
        {
            if (EsProyecto(carpetaActual)) return new(OrigenProyecto.CarpetaActual, Path.GetFullPath(carpetaActual));
            if (!string.IsNullOrWhiteSpace(ultimo) && Directory.Exists(ultimo))
                return new(OrigenProyecto.UltimoUsado, ultimo);
        }
        return new(OrigenProyecto.Selector, null, invalido);
    }
}
