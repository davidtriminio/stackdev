using System.Text;
using StackDev.Core.Util;

namespace StackDev.Core.Config;

/// <summary>
/// dev-menu.json en la raíz del repo: lo lee, lo guarda y detecta si el usuario lo editó
/// (se recarga solo al cambiar la fecha de modificación).
/// </summary>
public sealed class ConfigStore
{
    public const string NombreArchivo = "dev-menu.json";
    private static readonly UTF8Encoding Utf8SinBom = new(false);

    private DateTime _fecha;

    private readonly string? _dirExterno;

    /// <param name="dirExterno">
    /// Carpeta fuera del proyecto (en %APPDATA%\stackdev) donde se crea y guarda el archivo, para no ensuciar el repo.
    /// Si el proyecto ya trae su propio dev-menu.json, se sigue usando ese.
    /// </param>
    public ConfigStore(string raiz, string? dirExterno = null)
    {
        _dirExterno = dirExterno;
        Raiz = raiz;
        Config = new ProyectoConfig { Nombre = Rutas.NombreCarpeta(raiz) };
    }

    public string Raiz { get; }
    private string RutaRepo => Path.Combine(Raiz, NombreArchivo);
    private string? RutaExterna => _dirExterno is null ? null : Path.Combine(_dirExterno, NombreArchivo);

    /// <summary>El archivo en uso: el de fuera del proyecto si existe; si no, el del repo (heredado); si no, donde se creará.</summary>
    public string Ruta =>
        RutaExterna is not null && File.Exists(RutaExterna) ? RutaExterna :
        File.Exists(RutaRepo) ? RutaRepo :
        RutaExterna ?? RutaRepo;

    public bool Existe => File.Exists(Ruta);

    /// <summary>¿Se está usando un dev-menu.json que vive dentro del proyecto?</summary>
    public bool EnElRepo => string.Equals(Ruta, RutaRepo, StringComparison.OrdinalIgnoreCase) && Existe;

    /// <summary>Copia el dev-menu.json del proyecto a la carpeta externa (no borra el original). Devuelve la ruta nueva.</summary>
    public string? CopiarAfuera()
    {
        if (RutaExterna is null || !File.Exists(RutaRepo)) return null;
        Directory.CreateDirectory(_dirExterno!);
        File.Copy(RutaRepo, RutaExterna, overwrite: true);
        _fecha = DateTime.MinValue;
        return RutaExterna;
    }

    public ProyectoConfig Config { get; private set; }

    /// <summary>Mensaje de error si el archivo no se pudo leer (el menú queda vacío hasta corregirlo).</summary>
    public string? Error { get; private set; }

    public ProyectoContexto Contexto => new(Raiz, Config);

    /// <summary>Lee el archivo. Si tiene un error, deja una configuración vacía y llena <see cref="Error"/>.</summary>
    public void Cargar()
    {
        Error = null;
        try
        {
            Config = ConfigJson.Leer(File.ReadAllText(Ruta));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
            Config = new ProyectoConfig { Nombre = Rutas.NombreCarpeta(Raiz) };
        }
        _fecha = File.GetLastWriteTimeUtc(Ruta);
    }

    public void Guardar(ProyectoConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Ruta)!);
        File.WriteAllText(Ruta, ConfigJson.EscribirProtegido(config), Utf8SinBom);
        Config = config;
        Error = null;
        _fecha = File.GetLastWriteTimeUtc(Ruta);
    }

    /// <summary>Vuelve a leer el archivo si cambió desde la última vez. Devuelve true si recargó.</summary>
    public bool RecargarSiCambio()
    {
        if (!Existe) return false;
        if (File.GetLastWriteTimeUtc(Ruta) == _fecha) return false;
        Cargar();
        return true;
    }

    /// <summary>Guarda <paramref name="nueva"/> dejando una copia <c>dev-menu.json.bak</c> de la anterior.</summary>
    public void Reemplazar(ProyectoConfig nueva)
    {
        if (Existe) File.Copy(Ruta, Ruta + ".bak", overwrite: true);
        Guardar(nueva);
    }
}
