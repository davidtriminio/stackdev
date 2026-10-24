using System.Text;
using StackDev.Core.Util;

namespace StackDev.Core.Config;

/// <summary>
/// dev-menu.json en la raíz del repo: lo lee, lo guarda y detecta si el usuario lo editó
/// (se recarga solo al cambiar la fecha de modificación).
/// </summary>
public sealed class ConfigStore
{
    public const string FileName = "dev-menu.json";
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private DateTime _date;

    private readonly string? _externalDir;

    /// <param name="dirExternal">
    /// Carpeta fuera del proyecto (en %APPDATA%\stackdev) donde se crea y guarda el archivo, para no ensuciar el repo.
    /// Si el proyecto ya trae su propio dev-menu.json, se sigue usando ese.
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

    /// <summary>El archivo en uso: el de fuera del proyecto si existe; si no, el del repo (heredado); si no, donde se creará.</summary>
    public string FilePath =>
        ExternalPath is not null && File.Exists(ExternalPath) ? ExternalPath :
        File.Exists(RepoPath) ? RepoPath :
        ExternalPath ?? RepoPath;

    public bool Exists => File.Exists(FilePath);

    /// <summary>¿Se está usando un dev-menu.json que vive dentro del proyecto?</summary>
    public bool InRepo => string.Equals(FilePath, RepoPath, StringComparison.OrdinalIgnoreCase) && Exists;

    /// <summary>Copia el dev-menu.json del proyecto a la carpeta externa (no borra el original). Devuelve la ruta nueva.</summary>
    public string? CopyOut()
    {
        if (ExternalPath is null || !File.Exists(RepoPath)) return null;
        Directory.CreateDirectory(_externalDir!);
        File.Copy(RepoPath, ExternalPath, overwrite: true);
        _date = DateTime.MinValue;
        return ExternalPath;
    }

    public ProjectConfig Config { get; private set; }

    /// <summary>Mensaje de error si el archivo no se pudo leer (el menú queda vacío hasta corregirlo).</summary>
    public string? Error { get; private set; }

    public ProjectContext Context => new(Root, Config);

    /// <summary>Lee el archivo. Si tiene un error, deja una configuración vacía y llena <see cref="Error"/>.</summary>
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

    /// <summary>Vuelve a leer el archivo si cambió desde la última vez. Devuelve true si recargó.</summary>
    public bool ReloadIfChanged()
    {
        if (!Exists) return false;
        if (File.GetLastWriteTimeUtc(FilePath) == _date) return false;
        Load();
        return true;
    }

    /// <summary>Guarda <paramref name="fresh"/> dejando una copia <c>dev-menu.json.bak</c> de la anterior.</summary>
    public void Replace(ProjectConfig fresh)
    {
        if (Exists) File.Copy(FilePath, FilePath + ".bak", overwrite: true);
        Save(fresh);
    }
}
