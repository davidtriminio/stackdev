using System.Text.RegularExpressions;

namespace StackDev.Core.Detection;

/// <summary>Búsqueda de archivos por niveles saltando las carpetas que nunca importan.</summary>
public static class FileTree
{
    private static readonly Regex Excluded = new(
        @"^(node_modules|bin|obj|build|dist|coverage|Pods|android|ios|windows|linux|macos)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        MatchType = MatchType.Simple,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    public static bool IsExcludedFolder(string name) =>
        name.StartsWith('.') || Excluded.IsMatch(name);

    /// <summary>Busca por niveles (hasta <paramref name="depth"/>) los archivos que coincidan con los patrones.</summary>
    public static List<string> Search(string root, IEnumerable<string> patterns, int depth = 4)
    {
        var list = patterns.ToList();
        var res = new List<string>();
        var queue = new Queue<(string Dir, int Level)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (dir, level) = queue.Dequeue();
            foreach (var pattern in list)
            {
                try { res.AddRange(Directory.EnumerateFiles(dir, pattern, Options)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (level >= depth) continue;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir, "*", Options))
                {
                    if (!IsExcludedFolder(Path.GetFileName(sub))) queue.Enqueue((sub, level + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return res;
    }

    /// <summary>Contenido de un archivo de texto, o "" si no se puede leer.</summary>
    public static string Read(string file)
    {
        try { return File.ReadAllText(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
}
