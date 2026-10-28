using StackDev.Core.Config;

namespace StackDev.Core.Detection;

public enum ProjectOrigin
{
    /// <summary>A valid path was passed.</summary>
    Parameter,
    /// <summary>The current folder looks like a project.</summary>
    CurrentFolder,
    /// <summary>There is a last project used: the user must be asked whether to use it.</summary>
    LastUsed,
    /// <summary>It could not be decided: open the folder picker.</summary>
    Selector,
}

public sealed record ProjectResolution(ProjectOrigin Origin, string? FilePath, bool ParameterInvalid = false);

/// <summary>How the project root is decided.</summary>
public static class ProjectRoot
{
    private static readonly string[] Marks = { ConfigStore.FileName, ".git", "pubspec.yaml", "package.json" };

    /// <summary>Does the folder look like a project root? (it has .git, dev-menu.json, *.sln, pubspec.yaml…)</summary>
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
    /// If a subfolder was chosen (e.g. mobile/) and the parent folder has .git or dev-menu.json,
    /// returns the parent so it can be offered as the root. Otherwise null.
    /// </summary>
    public static string? SuggestParent(string path)
    {
        var r = Util.Paths.Normalize(path);
        if (HasRepoOrConfig(r)) return null;
        var parent = Path.GetDirectoryName(r);
        return !string.IsNullOrEmpty(parent) && HasRepoOrConfig(parent) ? parent : null;
    }

    /// <summary>Argument → current folder (if it looks like a project) → last project used (asks) → picker.</summary>
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
