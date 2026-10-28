using System.Text;
using StackDev.Core.Util;

namespace StackDev.Core.Config;

/// <summary>
/// dev-menu.json at the repo root: reads it, saves it and detects whether the user edited it
/// (it reloads by itself when the modification date changes).
/// </summary>
public sealed class ConfigStore
{
    public const string FileName = "dev-menu.json";
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private DateTime _date;

    private readonly string? _externalDir;

    /// <param name="dirExternal">
    /// Folder outside the project (in %APPDATA%\stackdev) where the file is created and saved, so as not to dirty the repo.
    /// If the project already ships its own dev-menu.json, that one keeps being used.
    /// </param>
    public ConfigStore(string root, string? dirExternal = null)
    {
        _externalDir = dirExternal;
        Root = root;
        Config = new ProjectConfig { Name = Paths.FolderName(root) };
    }

    public string Root { get; }
    private string RepoPath => Path.Combine(Root, FileName);
    private string? ExternalPath => _externalDir is null ? null : Path.Combine(_externalDir, FileName);

    /// <summary>The file in use: the one outside the project if it exists; otherwise the repo's (inherited); otherwise where it will be created.</summary>
    public string FilePath =>
        ExternalPath is not null && File.Exists(ExternalPath) ? ExternalPath :
        File.Exists(RepoPath) ? RepoPath :
        ExternalPath ?? RepoPath;

    public bool Exists => File.Exists(FilePath);

    /// <summary>Is a dev-menu.json that lives inside the project in use?</summary>
    public bool InRepo => string.Equals(FilePath, RepoPath, StringComparison.OrdinalIgnoreCase) && Exists;

    /// <summary>Copies the project's dev-menu.json to the external folder (does not delete the original). Returns the new path.</summary>
    public string? CopyOut()
    {
        if (ExternalPath is null || !File.Exists(RepoPath)) return null;
        Directory.CreateDirectory(_externalDir!);
        File.Copy(RepoPath, ExternalPath, overwrite: true);
        _date = DateTime.MinValue;
        return ExternalPath;
    }

    public ProjectConfig Config { get; private set; }

    /// <summary>Error message if the file could not be read (the menu stays empty until it is fixed).</summary>
    public string? Error { get; private set; }

    public ProjectContext Context => new(Root, Config);

    /// <summary>Reads the file. If it has an error, leaves an empty configuration and fills <see cref="Error"/>.</summary>
    public void Load()
    {
        Error = null;
        try
        {
            Config = ConfigJson.Read(File.ReadAllText(FilePath));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
            Config = new ProjectConfig { Name = Paths.FolderName(Root) };
        }
        _date = File.GetLastWriteTimeUtc(FilePath);
    }

    public void Save(ProjectConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, ConfigJson.WriteProtected(config), Utf8NoBom);
        Config = config;
        Error = null;
        _date = File.GetLastWriteTimeUtc(FilePath);
    }

    /// <summary>Reads the file again if it changed since last time. Returns true if it reloaded.</summary>
    public bool ReloadIfChanged()
    {
        if (!Exists) return false;
        if (File.GetLastWriteTimeUtc(FilePath) == _date) return false;
        Load();
        return true;
    }

    /// <summary>Saves <paramref name="fresh"/> leaving a <c>dev-menu.json.bak</c> copy of the previous one.</summary>
    public void Replace(ProjectConfig fresh)
    {
        if (Exists) File.Copy(FilePath, FilePath + ".bak", overwrite: true);
        Save(fresh);
    }
}
