using System.Text.RegularExpressions;

namespace StackDev.Core.Util;

/// <summary>{?Texto} dentro de una tarea pide un valor al ejecutarla; si se deja vacío, se cancela.</summary>
public static class Preguntas
{
    private static readonly Regex Patron = new(@"\{\?([^}]+)\}", RegexOptions.Compiled);

    /// <summary>Los textos de las preguntas, en orden y sin repetir.</summary>
    public static IReadOnlyList<string> Textos(string comando) =>
        Patron.Matches(comando).Select(m => m.Groups[1].Value).Distinct().ToList();

    public static bool TienePreguntas(string comando) => Patron.IsMatch(comando);

    /// <summary>
    /// Reemplaza cada {?Texto} por lo que devuelva <paramref name="preguntar"/>.
    /// Devuelve null si alguna respuesta queda vacía (la tarea se cancela).
    /// </summary>
    public static string? Expandir(string comando, Func<string, string?> preguntar)
    {
        foreach (var m in Patron.Matches(comando).DistinctBy(x => x.Value))
        {
            var valor = preguntar(m.Groups[1].Value)?.Trim();
            if (string.IsNullOrEmpty(valor)) return null;
            comando = comando.Replace(m.Value, valor);
        }
        return comando;
    }
}
