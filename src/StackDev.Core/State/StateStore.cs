using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using StackDev.Core.Util;

namespace StackDev.Core.State;

public sealed record MigracionEstado(bool Realizada, int Copiados, int Omitidos, string? Origen);

/// <summary>
/// Estado personal (no va en el repo): último proyecto, último AVD, historial de comandos,
/// condiciones por AVD y respaldos de la base. Vive en %APPDATA%\stackdev y se migra desde
/// %APPDATA%\flutter-dev. Los archivos tienen el mismo formato que usa dev.ps1/flutter.ps1.
/// </summary>
public sealed class StateStore
{
    public const string NombreCarpeta = "stackdev";
    public const string NombreCarpetaAnterior = "flutter-dev";
    private const string Marca = ".migrado";
    public const int MaxRecientes = 10;

    // UTF-8 con BOM: así el script de PowerShell 5.1 sigue leyendo bien las rutas con tildes.
    private static readonly UTF8Encoding Utf8Bom = new(true);

    public StateStore(string dir)
    {
        Dir = dir;
    }

    public string Dir { get; }

    public static string DirPredeterminado(string? appData = null) =>
        Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), NombreCarpeta);

    public static string DirAnterior(string? appData = null) =>
        Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), NombreCarpetaAnterior);

    /// <summary>Abre el estado de siempre (%APPDATA%\stackdev), migrando lo que haya en flutter-dev la primera vez.</summary>
    public static StateStore Abrir(string? appData = null)
    {
        var store = new StateStore(DirPredeterminado(appData));
        store.Migrar(DirAnterior(appData));
        return store;
    }

    /// <summary>
    /// Copia lo que falta de la carpeta anterior (no pisa nada y no borra el origen, para que el script viejo siga funcionando).
    /// Solo corre una vez, salvo con <paramref name="forzar"/>: entonces trae lo que sea más nuevo en el origen.
    /// </summary>
    public MigracionEstado Migrar(string origen, bool forzar = false)
    {
        Directory.CreateDirectory(Dir);
        var marca = Path.Combine(Dir, Marca);
        if (!Directory.Exists(origen)) return new(false, 0, 0, null);
        if (File.Exists(marca) && !forzar) return new(false, 0, 0, origen);

        int copiados = 0, omitidos = 0;
        foreach (var archivo in Directory.EnumerateFiles(origen, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(origen, archivo);
            var destino = Path.Combine(Dir, rel);
            var existe = File.Exists(destino);
            if (existe && !(forzar && File.GetLastWriteTimeUtc(archivo) > File.GetLastWriteTimeUtc(destino)))
            {
                omitidos++;
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
            File.Copy(archivo, destino, overwrite: true);
            copiados++;
        }
        File.WriteAllText(marca, $"Migrado de {origen} el {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        return new(true, copiados, omitidos, origen);
    }

    // ---------------------------------------------------------------- Proyecto

    /// <summary>Clave estable de un proyecto: "nombre-xxxxxxxx" (MD5 de la ruta en minúsculas). Igual que en el script.</summary>
    public static string Clave(string raiz)
    {
        var ruta = raiz.ToLowerInvariant();
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(ruta));
        var nombre = Regex.Replace(Rutas.NombreCarpeta(raiz), @"[^\w\-]", "_");
        return nombre + "-" + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    public string? UltimoProyecto
    {
        get => Leer("ultimo-proyecto.txt");
        set => Escribir("ultimo-proyecto.txt", value);
    }

    /// <summary>El proyecto favorito (solo uno): el que se abre al iniciar la aplicación con ventana.</summary>
    public string? Favorito
    {
        get => Leer("favorito.txt");
        set => Escribir("favorito.txt", value);
    }

    /// <summary>El navegador para abrir direcciones en todos los proyectos (id o ruta de .exe); vacío = el predeterminado de Windows.</summary>
    public string? Navegador
    {
        get => Leer("navegador.txt");
        set => Escribir("navegador.txt", value);
    }

    /// <summary>El color de acento de la interfaz: «verde» (predeterminado) o «ambar».</summary>
    public string? Acento
    {
        get => Leer("acento.txt");
        set => Escribir("acento.txt", value);
    }

    /// <summary>Densidad de las tarjetas del panel: «1» = compacta.</summary>
    public string? Compacto
    {
        get => Leer("compacto.txt");
        set => Escribir("compacto.txt", value);
    }

    /// <summary>Dónde vive el dev-menu.json de un proyecto (fuera del repo, para que no llegue a un commit).</summary>
    public string CarpetaConfig(string clave) => Path.Combine(Dir, "config", clave);

    // ---------------------------------------------------------------- Proyectos abiertos (la lista de inicio)

    public const int MaxProyectos = 30;

    /// <summary>Los proyectos que se han abierto, el más reciente primero.</summary>
    public List<string> LeerProyectos()
    {
        var lista = LeerLineas("proyectos.txt").Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var ultimo = UltimoProyecto;
        if (lista.Count == 0 && !string.IsNullOrWhiteSpace(ultimo)) lista.Add(ultimo);
        return lista;
    }

    public void AgregarProyecto(string ruta)
    {
        var lista = new List<string> { ruta };
        lista.AddRange(LeerProyectos().Where(p => !string.Equals(p, ruta, StringComparison.OrdinalIgnoreCase)));
        GuardarProyectos(lista.Take(MaxProyectos));
    }

    public void QuitarProyecto(string ruta) =>
        GuardarProyectos(LeerProyectos().Where(p => !string.Equals(p, ruta, StringComparison.OrdinalIgnoreCase)));

    private void GuardarProyectos(IEnumerable<string> lista)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllLines(Path.Combine(Dir, "proyectos.txt"), lista, Utf8Bom);
    }

    // ---------------------------------------------------------------- Historial de la barra de comandos

    /// <summary>Lo que se ha escrito en la barra de comandos de este proyecto, lo más reciente primero (↑/↓).</summary>
    public List<string> LeerHistorialBarra(string clave) =>
        LeerLineas($"barra-{clave}.txt").Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    public void AgregarHistorialBarra(string clave, string texto)
    {
        texto = texto.Trim();
        if (texto.Length == 0) return;
        var lista = new List<string> { texto };
        lista.AddRange(LeerHistorialBarra(clave).Where(c => c != texto));
        Directory.CreateDirectory(Dir);
        File.WriteAllLines(Path.Combine(Dir, $"barra-{clave}.txt"), lista.Take(100), Utf8Bom);
    }

    // ---------------------------------------------------------------- Dispositivos Android en red

    /// <summary>Direcciones «ip:puerto» de los teléfonos que se han conectado por red en este proyecto.</summary>
    public List<string> LeerDispositivosRed(string clave) =>
        LeerLineas($"dispositivos-{clave}.txt").Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    public void AgregarDispositivoRed(string clave, string direccion)
    {
        var lista = new List<string> { direccion };
        lista.AddRange(LeerDispositivosRed(clave).Where(d => !string.Equals(d, direccion, StringComparison.OrdinalIgnoreCase)));
        Directory.CreateDirectory(Dir);
        File.WriteAllLines(Path.Combine(Dir, $"dispositivos-{clave}.txt"), lista.Take(10), Utf8Bom);
    }

    public void QuitarDispositivoRed(string clave, string direccion)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllLines(Path.Combine(Dir, $"dispositivos-{clave}.txt"),
            LeerDispositivosRed(clave).Where(d => !string.Equals(d, direccion, StringComparison.OrdinalIgnoreCase)), Utf8Bom);
    }

    // ---------------------------------------------------------------- AVD

    public string? UltimoAvdGlobal => Leer("ultimo-avd.txt");

    public string? UltimoAvd(string clave) => Leer($"ultimo-avd-{clave}.txt");

    public void GuardarUltimoAvd(string clave, string avd)
    {
        Escribir($"ultimo-avd-{clave}.txt", avd);
        Escribir("ultimo-avd.txt", avd);
    }

    /// <summary>Emulador por defecto: parámetro → último de este proyecto → movil.avd → último en general.</summary>
    public string? ResolverAvd(string clave, string? parametro, string? deConfig)
    {
        if (!string.IsNullOrWhiteSpace(parametro)) return parametro;
        var porProyecto = UltimoAvd(clave);
        if (!string.IsNullOrEmpty(porProyecto)) return porProyecto;
        if (!string.IsNullOrWhiteSpace(deConfig)) return deConfig;
        return UltimoAvdGlobal;
    }

    // ---------------------------------------------------------------- Condiciones del dispositivo (por AVD)

    public static readonly string[] CategoriasCondiciones =
        { "Red", "Velocidad", "Bateria", "Tema", "Texto", "Pantalla", "Ubicacion" };

    private static string ArchivoCondiciones(string? avd) =>
        $"condiciones-{(string.IsNullOrEmpty(avd) ? "general" : avd)}.txt";

    public Dictionary<string, string> LeerCondiciones(string? avd)
    {
        var res = new Dictionary<string, string>();
        foreach (var linea in LeerLineas(ArchivoCondiciones(avd)))
        {
            var m = Regex.Match(linea, "^([^=]+)=(.*)$");
            if (m.Success) res[m.Groups[1].Value] = m.Groups[2].Value;
        }
        return res;
    }

    public void GuardarCondiciones(string? avd, IReadOnlyDictionary<string, string> condiciones)
    {
        var ruta = Path.Combine(Dir, ArchivoCondiciones(avd));
        if (condiciones.Count == 0)
        {
            if (File.Exists(ruta)) File.Delete(ruta);
            return;
        }
        var lineas = CategoriasCondiciones.Where(condiciones.ContainsKey).Select(c => $"{c}={condiciones[c]}");
        Directory.CreateDirectory(Dir);
        File.WriteAllLines(ruta, lineas, Utf8Bom);
    }

    // ---------------------------------------------------------------- Comandos recientes (por proyecto)

    public sealed record ComandoReciente(string Carpeta, string Comando);

    public List<ComandoReciente> LeerRecientes(string clave)
    {
        var res = new List<ComandoReciente>();
        foreach (var linea in LeerLineas($"comandos-{clave}.txt"))
        {
            if (string.IsNullOrWhiteSpace(linea)) continue;
            var partes = linea.Split('\t', 2);
            res.Add(partes.Length == 2 ? new(partes[0], partes[1]) : new(".", linea));
        }
        return res;
    }

    /// <summary>El comando más reciente va primero; sin repetidos; los últimos 10.</summary>
    public void AgregarReciente(string clave, string carpeta, string comando)
    {
        var nuevo = new ComandoReciente(carpeta, comando);
        var lista = new List<ComandoReciente> { nuevo };
        lista.AddRange(LeerRecientes(clave).Where(c => c != nuevo));
        var lineas = lista.Take(MaxRecientes).Select(c => $"{c.Carpeta}\t{c.Comando}");
        Directory.CreateDirectory(Dir);
        File.WriteAllLines(Path.Combine(Dir, $"comandos-{clave}.txt"), lineas, Utf8Bom);
    }

    // ---------------------------------------------------------------- Respaldos

    public string CarpetaRespaldos(string clave)
    {
        var d = Path.Combine(Dir, "respaldos", clave);
        Directory.CreateDirectory(d);
        return d;
    }

    // ---------------------------------------------------------------- Archivos

    private string? Leer(string nombre)
    {
        var ruta = Path.Combine(Dir, nombre);
        if (!File.Exists(ruta)) return null;
        try
        {
            var t = File.ReadAllText(ruta).Trim();
            return t.Length == 0 ? null : t;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private IEnumerable<string> LeerLineas(string nombre)
    {
        var ruta = Path.Combine(Dir, nombre);
        if (!File.Exists(ruta)) return Array.Empty<string>();
        try { return File.ReadAllLines(ruta).Select(l => l.TrimEnd('\r')).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private void Escribir(string nombre, string? valor)
    {
        Directory.CreateDirectory(Dir);
        var ruta = Path.Combine(Dir, nombre);
        if (string.IsNullOrEmpty(valor)) { if (File.Exists(ruta)) File.Delete(ruta); return; }
        File.WriteAllText(ruta, valor, Utf8Bom);
    }
}
