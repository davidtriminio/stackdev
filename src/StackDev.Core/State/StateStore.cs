using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using StackDev.Core.Util;

namespace StackDev.Core.State;

public sealed record StateMigration(bool Done, int Copied, int Skipped, string? Origin);

/// <summary>
/// Estado personal (no va en el repo): último proyecto, último AVD, historial de comandos,
/// condiciones por AVD y respaldos de la base. Vive en %APPDATA%\stackdev y se migra desde
/// %APPDATA%\previous-state. Los archivos tienen el mismo formato que usa the previous script/the previous script.
/// </summary>
public sealed class StateStore
{
    public const string FolderName = "stackdev";
    public const string PreviousFolderName = "previous-state";
    private const string Mark = ".migrado";
    public const int MaxRecent = 10;

    // UTF-8 con BOM: así el script de PowerShell 5.1 sigue leyendo bien las rutas con tildes.
    private static readonly UTF8Encoding Utf8Bom = new(true);

    public StateStore(string dir)
    {
        Dir = dir;
    }

    public string Dir { get; }

    public static string DefaultDir(string? appData = null) =>
        Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

    public static string DirPrevious(string? appData = null) =>
        Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), PreviousFolderName);

    /// <summary>Abre el estado de siempre (%APPDATA%\stackdev), migrando lo que haya en previous-state la primera vez.</summary>
    public static StateStore Open(string? appData = null)
    {
        var store = new StateStore(DefaultDir(appData));
        store.Migrate(DirPrevious(appData));
        return store;
    }

    /// <summary>
    /// Copia lo que falta de la carpeta anterior (no pisa nada y no borra el origen, para que el script viejo siga funcionando).
    /// Solo corre una vez, salvo con <paramref name="force"/>: entonces trae lo que sea más nuevo en el origen.
    /// </summary>
    public StateMigration Migrate(string origin, bool force = false)
    {
        Directory.CreateDirectory(Dir);
        var mark = Path.Combine(Dir, Mark);
        if (!Directory.Exists(origin)) return new(false, 0, 0, null);
        if (File.Exists(mark) && !force) return new(false, 0, 0, origin);

        int copied = 0, skipped = 0;
        foreach (var file in Directory.EnumerateFiles(origin, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(origin, file);
            var destination = Path.Combine(Dir, rel);
            var exists = File.Exists(destination);
            if (exists && !(force && File.GetLastWriteTimeUtc(file) > File.GetLastWriteTimeUtc(destination)))
            {
                skipped++;
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            copied++;
        }
        File.WriteAllText(mark, $"Migrado de {origin} el {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        return new(true, copied, skipped, origin);
    }

    // ---------------------------------------------------------------- Proyecto

    /// <summary>Clave estable de un proyecto: "nombre-xxxxxxxx" (MD5 de la ruta en minúsculas). Igual que en el script.</summary>
    public static string Key(string root)
    {
        var path = root.ToLowerInvariant();
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(path));
        var name = Regex.Replace(Paths.FolderName(root), @"[^\w\-]", "_");
        return name + "-" + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    public string? LastProject
    {
        get => Read("ultimo-proyecto.txt");
        set => Write("ultimo-proyecto.txt", value);
    }

    /// <summary>El proyecto favorito (solo uno): el que se abre al iniciar la aplicación con ventana.</summary>
    public string? Favorite
    {
        get => Read("favorito.txt");
        set => Write("favorito.txt", value);
    }

    /// <summary>El navegador para abrir direcciones en todos los proyectos (id o ruta de .exe); vacío = el predeterminado de Windows.</summary>
    public string? Browser
    {
        get => Read("navegador.txt");
        set => Write("navegador.txt", value);
    }

    /// <summary>El color de acento de la interfaz: «verde» (predeterminado) o «ambar».</summary>
    public string? Accent
    {
        get => Read("acento.txt");
        set => Write("acento.txt", value);
    }

    /// <summary>Densidad de las tarjetas del panel: «1» = compacta.</summary>
    public string? Compact
    {
        get => Read("compacto.txt");
        set => Write("compacto.txt", value);
    }

    /// <summary>Dónde vive el dev-menu.json de un proyecto (fuera del repo, para que no llegue a un commit).</summary>
    public string ConfigFolder(string key) => Path.Combine(Dir, "config", key);

    // ---------------------------------------------------------------- Proyectos abiertos (la lista de inicio)

    public const int MaxProjects = 30;

    /// <summary>Los proyectos que se han abierto, el más reciente primero.</summary>
    public List<string> ReadProjects()
    {
        var list = ReadLines("proyectos.txt").Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var last = LastProject;
        if (list.Count == 0 && !string.IsNullOrWhiteSpace(last)) list.Add(last);
        return list;
    }

    public void AddProject(string path)
    {
        var list = new List<string> { path };
        list.AddRange(ReadProjects().Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)));
        SaveProjects(list.Take(MaxProjects));
    }

    public void RemoveProject(string path) =>
        SaveProjects(ReadProjects().Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)));

    private void SaveProjects(IEnumerable<string> list)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllLines(Path.Combine(Dir, "proyectos.txt"), list, Utf8Bom);
    }

    // ---------------------------------------------------------------- Historial de la barra de comandos

    /// <summary>Lo que se ha escrito en la barra de comandos de este proyecto, lo más reciente primero (↑/↓).</summary>
    public List<string> ReadBarHistory(string key) =>
        ReadLines($"barra-{key}.txt").Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    public void AddBarHistory(string key, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        var list = new List<string> { text };
        list.AddRange(ReadBarHistory(key).Where(c => c != text));
        Directory.CreateDirectory(Dir);
        File.WriteAllLines(Path.Combine(Dir, $"barra-{key}.txt"), list.Take(100), Utf8Bom);
    }

    // ---------------------------------------------------------------- Dispositivos Android en red

    /// <summary>Direcciones «ip:puerto» de los teléfonos que se han conectado por red en este proyecto.</summary>
    public List<string> ReadNetworkDevices(string key) =>
        ReadLines($"dispositivos-{key}.txt").Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    public void AddNetworkDevice(string key, string address)
    {
        var list = new List<string> { address };
        list.AddRange(ReadNetworkDevices(key).Where(d => !string.Equals(d, address, StringComparison.OrdinalIgnoreCase)));
        Directory.CreateDirectory(Dir);
        File.WriteAllLines(Path.Combine(Dir, $"dispositivos-{key}.txt"), list.Take(10), Utf8Bom);
    }

    public void RemoveNetworkDevice(string key, string address)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllLines(Path.Combine(Dir, $"dispositivos-{key}.txt"),
            ReadNetworkDevices(key).Where(d => !string.Equals(d, address, StringComparison.OrdinalIgnoreCase)), Utf8Bom);
    }

    // ---------------------------------------------------------------- AVD

    public string? GlobalLastAvd => Read("ultimo-avd.txt");

    public string? LastAvd(string key) => Read($"ultimo-avd-{key}.txt");

    public void SaveLastAvd(string key, string avd)
    {
        Write($"ultimo-avd-{key}.txt", avd);
        Write("ultimo-avd.txt", avd);
    }

    /// <summary>Emulador por defecto: parámetro → último de este proyecto → movil.avd → último en general.</summary>
    public string? ResolveAvd(string key, string? parameter, string? ofConfig)
    {
        if (!string.IsNullOrWhiteSpace(parameter)) return parameter;
        var byProject = LastAvd(key);
        if (!string.IsNullOrEmpty(byProject)) return byProject;
        if (!string.IsNullOrWhiteSpace(ofConfig)) return ofConfig;
        return GlobalLastAvd;
    }

    // ---------------------------------------------------------------- Condiciones del dispositivo (por AVD)

    public static readonly string[] ConditionCategories =
        { "Red", "Velocidad", "Bateria", "Tema", "Texto", "Pantalla", "Ubicacion" };

    private static string ConditionsFile(string? avd) =>
        $"condiciones-{(string.IsNullOrEmpty(avd) ? "general" : avd)}.txt";

    public Dictionary<string, string> ReadConditions(string? avd)
    {
        var res = new Dictionary<string, string>();
        foreach (var line in ReadLines(ConditionsFile(avd)))
        {
            var m = Regex.Match(line, "^([^=]+)=(.*)$");
            if (m.Success) res[m.Groups[1].Value] = m.Groups[2].Value;
        }
        return res;
    }

    public void SaveConditions(string? avd, IReadOnlyDictionary<string, string> conditions)
    {
        var path = Path.Combine(Dir, ConditionsFile(avd));
        if (conditions.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        var lines = ConditionCategories.Where(conditions.ContainsKey).Select(c => $"{c}={conditions[c]}");
        Directory.CreateDirectory(Dir);
        File.WriteAllLines(path, lines, Utf8Bom);
    }

    // ---------------------------------------------------------------- Comandos recientes (por proyecto)

    public sealed record RecentCommand(string Folder, string Command);

    public List<RecentCommand> ReadRecent(string key)
    {
        var res = new List<RecentCommand>();
        foreach (var line in ReadLines($"comandos-{key}.txt"))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('\t', 2);
            res.Add(parts.Length == 2 ? new(parts[0], parts[1]) : new(".", line));
        }
        return res;
    }

    /// <summary>El comando más reciente va primero; sin repetidos; los últimos 10.</summary>
    public void AddRecent(string key, string folder, string command)
    {
        var fresh = new RecentCommand(folder, command);
        var list = new List<RecentCommand> { fresh };
        list.AddRange(ReadRecent(key).Where(c => c != fresh));
        var lines = list.Take(MaxRecent).Select(c => $"{c.Folder}\t{c.Command}");
        Directory.CreateDirectory(Dir);
        File.WriteAllLines(Path.Combine(Dir, $"comandos-{key}.txt"), lines, Utf8Bom);
    }

    // ---------------------------------------------------------------- Respaldos

    public string BackupsFolder(string key)
    {
        var d = Path.Combine(Dir, "respaldos", key);
        Directory.CreateDirectory(d);
        return d;
    }

    // ---------------------------------------------------------------- Archivos

    private string? Read(string name)
    {
        var path = Path.Combine(Dir, name);
        if (!File.Exists(path)) return null;
        try
        {
            var t = File.ReadAllText(path).Trim();
            return t.Length == 0 ? null : t;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private IEnumerable<string> ReadLines(string name)
    {
        var path = Path.Combine(Dir, name);
        if (!File.Exists(path)) return Array.Empty<string>();
        try { return File.ReadAllLines(path).Select(l => l.TrimEnd('\r')).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private void Write(string name, string? val)
    {
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, name);
        if (string.IsNullOrEmpty(val)) { if (File.Exists(path)) File.Delete(path); return; }
        File.WriteAllText(path, val, Utf8Bom);
    }
}
