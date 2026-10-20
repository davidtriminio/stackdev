using System.Text.RegularExpressions;

namespace StackDev.Core.Detection;

/// <summary>Búsqueda de archivos por niveles saltando las carpetas que nunca importan.</summary>
public static class ArbolArchivos
{
    private static readonly Regex Excluidas = new(
        @"^(node_modules|bin|obj|build|dist|coverage|Pods|android|ios|windows|linux|macos)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly EnumerationOptions Opciones = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        MatchType = MatchType.Simple,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    public static bool EsCarpetaExcluida(string nombre) =>
        nombre.StartsWith('.') || Excluidas.IsMatch(nombre);

    /// <summary>Busca por niveles (hasta <paramref name="profundidad"/>) los archivos que coincidan con los patrones.</summary>
    public static List<string> Buscar(string raiz, IEnumerable<string> patrones, int profundidad = 4)
    {
        var lista = patrones.ToList();
        var res = new List<string>();
        var cola = new Queue<(string Dir, int Nivel)>();
        cola.Enqueue((raiz, 0));

        while (cola.Count > 0)
        {
            var (dir, nivel) = cola.Dequeue();
            foreach (var patron in lista)
            {
                try { res.AddRange(Directory.EnumerateFiles(dir, patron, Opciones)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (nivel >= profundidad) continue;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir, "*", Opciones))
                {
                    if (!EsCarpetaExcluida(Path.GetFileName(sub))) cola.Enqueue((sub, nivel + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return res;
    }

    /// <summary>Contenido de un archivo de texto, o "" si no se puede leer.</summary>
    public static string Leer(string archivo)
    {
        try { return File.ReadAllText(archivo); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
}
