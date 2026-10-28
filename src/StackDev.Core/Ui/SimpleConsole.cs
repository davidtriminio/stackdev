namespace StackDev.Core.Ui;

/// <summary>
/// Implementation on top of System.Console, for the headless commands (scripts).
/// With nobody in front it answers with the default values (or "yes" to everything with <c>--si</c>).
/// </summary>
public sealed class SimpleConsole : IConsoleUi
{
    private readonly bool _yesToAll;

    public SimpleConsole(bool interactive, bool yesAAll = false)
    {
        Interactive = interactive && !yesAAll;
        _yesToAll = yesAAll;
    }

    public bool Interactive { get; }

    public void Write(string text, Level level = Level.Normal)
    {
        var color = level switch
        {
            Level.Title => ConsoleColor.Cyan,
            Level.Info => ConsoleColor.Cyan,
            Level.Ok => ConsoleColor.Green,
            Level.Warning => ConsoleColor.Yellow,
            Level.Error => ConsoleColor.Red,
            Level.Muted => ConsoleColor.DarkGray,
            _ => (ConsoleColor?)null,
        };
        if (color is null || Console.IsOutputRedirected) { Console.WriteLine(text); return; }
        var before = Console.ForegroundColor;
        Console.ForegroundColor = color.Value;
        Console.WriteLine(text);
        Console.ForegroundColor = before;
    }

    public void WriteInline(string text) => Console.Write(text);

    public bool Confirm(string question, bool byDefault = true)
    {
        if (_yesToAll) return true;
        if (!Interactive) return byDefault;
        Console.Write($"{question} ({(byDefault ? "S/n" : "s/N")}) ");
        var r = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        if (r.Length == 0) return byDefault;
        return r is "s" or "si" or "sí" or "y" or "yes";
    }

    public string? Ask(string question, string? byDefault = null)
    {
        if (!Interactive) return byDefault;
        Console.Write(byDefault is null ? $"{question}: " : $"{question} (Enter = {byDefault}): ");
        var r = (Console.ReadLine() ?? "").Trim();
        return r.Length == 0 ? byDefault : r;
    }

    public int Choose(string title, IReadOnlyList<string> options, int byDefault = -1)
    {
        if (!Interactive) return byDefault;
        Console.WriteLine(title);
        for (var i = 0; i < options.Count; i++) Console.WriteLine($"  [{i + 1}] {options[i]}");
        while (true)
        {
            Console.Write(byDefault >= 0 ? $"Opción (Enter = {byDefault + 1}, c = cancelar): " : "Opción (c = cancelar): ");
            var r = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (r.Length == 0 && byDefault >= 0) return byDefault;
            if (r == "c") return -1;
            if (int.TryParse(r, out var n) && n >= 1 && n <= options.Count) return n - 1;
            Console.WriteLine("Opción no válida.");
        }
    }
}
