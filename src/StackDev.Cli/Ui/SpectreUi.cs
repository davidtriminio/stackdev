using Spectre.Console;
using StackDev.Core.Ui;

namespace StackDev.Cli.Ui;

/// <summary>La pantalla interactiva: mensajes con color, confirmaciones y listas con flechas.</summary>
public sealed class SpectreUi : IConsoleUi
{
    public bool Interactive => !Console.IsInputRedirected;

    public void Write(string text, Level level = Level.Normal)
    {
        if (text.Length == 0) { AnsiConsole.WriteLine(); return; }
        var lines = text.Split('\n');
        foreach (var l in lines)
        {
            var e = Markup.Escape(l.TrimEnd('\r'));
            AnsiConsole.MarkupLine(level switch
            {
                Level.Title => $"[bold cyan]{e}[/]",
                Level.Info => $"[cyan]{e}[/]",
                Level.Ok => $"[green]{e}[/]",
                Level.Warning => $"[yellow]{e}[/]",
                Level.Error => $"[red]{e}[/]",
                Level.Muted => $"[grey]{e}[/]",
                _ => e,
            });
        }
    }

    public void WriteInline(string text) => AnsiConsole.Markup(Markup.Escape(text));

    public bool Confirm(string question, bool byDefault = true) =>
        AnsiConsole.Confirm(Markup.Escape(question), byDefault);

    public string? Ask(string question, string? byDefault = null)
    {
        var p = new TextPrompt<string>(Markup.Escape(question)).AllowEmpty();
        if (!string.IsNullOrEmpty(byDefault)) p = p.DefaultValue(byDefault);
        var r = AnsiConsole.Prompt(p).Trim();
        return r.Length == 0 ? null : r;
    }

    public int Choose(string title, IReadOnlyList<string> options, int byDefault = -1)
    {
        // El primero de la lista es el que queda seleccionado: el valor por defecto va arriba.
        var order = new List<int>();
        if (byDefault >= 0 && byDefault < options.Count) order.Add(byDefault);
        for (var i = 0; i < options.Count; i++) if (i != byDefault) order.Add(i);
        order.Add(-1);

        var prompt = new SelectionPrompt<int>()
            .Title(Markup.Escape(title))
            .PageSize(15)
            .UseConverter(i => i < 0 ? "[grey]Cancelar[/]" : Markup.Escape(options[i]))
            .AddChoices(order);
        return AnsiConsole.Prompt(prompt);
    }
}
