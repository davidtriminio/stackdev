using System.Text.RegularExpressions;

namespace StackDev.Core.Util;

/// <summary>{?Texto} dentro de una tarea pide un valor al ejecutarla; si se deja vacío, se cancela.</summary>
public static class Prompts
{
    private static readonly Regex Pattern = new(@"\{\?([^}]+)\}", RegexOptions.Compiled);

    /// <summary>Los textos de las preguntas, en orden y sin repetir.</summary>
    public static IReadOnlyList<string> Texts(string command) =>
        Pattern.Matches(command).Select(m => m.Groups[1].Value).Distinct().ToList();

    public static bool HasPrompts(string command) => Pattern.IsMatch(command);

    /// <summary>
    /// Reemplaza cada {?Texto} por lo que devuelva <paramref name="ask"/>.
    /// Devuelve null si alguna respuesta queda vacía (la tarea se cancela).
    /// </summary>
    public static string? Expand(string command, Func<string, string?> ask)
    {
        foreach (var m in Pattern.Matches(command).DistinctBy(x => x.Value))
        {
            var val = ask(m.Groups[1].Value)?.Trim();
            if (string.IsNullOrEmpty(val)) return null;
            command = command.Replace(m.Value, val);
        }
        return command;
    }
}
