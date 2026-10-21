using StackDev.Core.Config;

namespace StackDev.Core.Detection;

public enum ProjectOrigin
{
    /// <summary>Se pasó una ruta válida.</summary>
    Parameter,
    /// <summary>La carpeta actual parece un proyecto.</summary>
    CurrentFolder,
    /// <summary>Hay un último proyecto usado: hay que preguntar si se usa.</summary>
    LastUsed,
    /// <summary>No se pudo decidir: abrir el selector de carpeta.</summary>
    Selector,
}

public sealed record ProjectResolution(ProjectOrigin Origin, string? FilePath, bool ParameterInvalid = false);

/// <summary>Cómo se decide cuál es la raíz del proyecto.</summary>
public static class ProjectRoot
{
    private static readonly string[] Marks = { ConfigStore.FileName, ".git", "pubspec.yaml", "package.json" };

    /// <summary>¿La carpeta parece la raíz de un proyecto? (tiene .git, dev-menu.json, *.sln, pubspec.yaml…)</summary>
    public static bool IsProject(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
        foreach (var m in Marks)
        {
            var p = Path.Combine(path, m);
            if (File.Exists(p) || Directory.Exists(p)) return true;
        }
        try
        {
            return Directory.EnumerateFiles(path).Any(f =>
                f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool HasRepoOrConfig(string path) =>
        File.Exists(Path.Combine(path, ConfigStore.FileName)) ||
        Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));

    /// <summary>
    /// Si eligieron una subcarpeta (p. ej. mobile/) y la carpeta de arriba tiene .git o dev-menu.json,
    /// devuelve la de arriba para ofrecerla como raíz. Si no, null.
    /// </summary>
    public static string? SuggestParent(string path)
    {
        var r = Util.Paths.Normalize(path);
        if (HasRepoOrConfig(r)) return null;
        var parent = Path.GetDirectoryName(r);
        return !string.IsNullOrEmpty(parent) && HasRepoOrConfig(parent) ? parent : null;
    }

    /// <summary>Parámetro → carpeta actual (si parece proyecto) → último proyecto usado (se pregunta) → selector.</summary>
    public static ProjectResolution Resolve(string? parameter, string folderCurrent, string? last)
    {
        var invalid = false;
        if (!string.IsNullOrWhiteSpace(parameter))
        {
            if (Directory.Exists(parameter)) return new(ProjectOrigin.Parameter, Path.GetFullPath(parameter));
            invalid = true;
        }
        else
        {
            if (IsProject(folderCurrent)) return new(ProjectOrigin.CurrentFolder, Path.GetFullPath(folderCurrent));
            if (!string.IsNullOrWhiteSpace(last) && Directory.Exists(last))
                return new(ProjectOrigin.LastUsed, last);
        }
        return new(ProjectOrigin.Selector, null, invalid);
    }
}
