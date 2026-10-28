using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using StackDev.Core.Util;

namespace StackDev.Core.State;

/// <summary>
/// Personal state (not in the repo): last project, last AVD, command history,
/// conditions per AVD and database backups. It lives in %APPDATA%\stackdev.
/// </summary>
public sealed class StateStore
{
    public const string FolderName = "stackdev";
    public const int MaxRecent = 10;

    // UTF-8 with BOM: PowerShell 5.1 and Windows editors detect the accents in paths correctly.
    private static readonly UTF8Encoding Utf8Bom = new(true);

    public StateStore(string dir)
    {
        Dir = dir;
    }

    public string Dir { get; }

    public static string DefaultDir(string? appData = null) =>
        Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

    /// <summary>Opens this person's state (%APPDATA%\stackdev).</summary>
    public static StateStore Open(string? appData = null) => new(DefaultDir(appData));


    // ---------------------------------------------------------------- Proyecto

    /// <summary>Stable key of a project: "name-xxxxxxxx" (MD5 of the lowercase path). Stable across runs and across computers with the same path.</summary>
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

    /// <summary>The favourite project (only one): the one opened when the windowed application starts.</summary>
    public string? Favorite
    {
        get => Read("favorito.txt");
        set => Write("favorito.txt", value);
    }

    /// <summary>The browser used to open addresses in all projects (id or .exe path); empty = the Windows default.</summary>
    public string? Browser
    {
        get => Read("navegador.txt");
        set => Write("navegador.txt", value);
    }

    /// <summary>The accent colour of the interface: "verde" (green, default) or "ambar" (amber).</summary>
    public string? Accent
    {
        get => Read("acento.txt");
        set => Write("acento.txt", value);
    }

    /// <summary>Density of the panel cards: "1" = compact.</summary>
    public string? Compact
    {
        get => Read("compacto.txt");
        set => Write("compacto.txt", value);
    }

    /// <summary>Where a project's dev-menu.json lives (outside the repo, so it never reaches a commit).</summary>
    public string ConfigFolder(string key) => Path.Combine(Dir, "config", key);

    // ---------------------------------------------------------------- Open projects (the start list)

    public const int MaxProjects = 30;

    /// <summary>The projects that have been opened, most recent first.</summary>
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

    // ---------------------------------------------------------------- Command bar history

    /// <summary>What has been typed in this project's command bar, most recent first (↑/↓).</summary>
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

    /// <summary>"ip:port" addresses of the phones that have been connected over the network in this project.</summary>
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

    /// <summary>Default emulator: argument → last of this project → mobile.avd → last overall.</summary>
    public string? ResolveAvd(string key, string? parameter, string? ofConfig)
    {
        if (!string.IsNullOrWhiteSpace(parameter)) return parameter;
        var byProject = LastAvd(key);
        if (!string.IsNullOrEmpty(byProject)) return byProject;
        if (!string.IsNullOrWhiteSpace(ofConfig)) return ofConfig;
        return GlobalLastAvd;
    }

    // ---------------------------------------------------------------- Device conditions (per AVD)

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

    // ---------------------------------------------------------------- Recent commands (per project)

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

    /// <summary>The most recent command goes first; no repeats; the last 10.</summary>
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
