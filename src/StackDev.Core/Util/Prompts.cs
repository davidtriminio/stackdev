using System.Text.RegularExpressions;

namespace StackDev.Core.Util;

/// <summary>{?Text} inside a task asks for a value when it runs; if left empty, it is cancelled.</summary>
public static class Prompts
{
    private static readonly Regex Pattern = new(@"\{\?([^}]+)\}", RegexOptions.Compiled);

    /// <summary>The texts of the questions, in order and without repeats.</summary>
    public static IReadOnlyList<string> Texts(string command) =>
        Pattern.Matches(command).Select(m => m.Groups[1].Value).Distinct().ToList();

    public static bool HasPrompts(string command) => Pattern.IsMatch(command);

    /// <summary>
    /// Replaces each {?Text} with whatever <paramref name="ask"/> returns.
    /// Returns null if any answer is empty (the task is cancelled).
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
