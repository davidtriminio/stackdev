using Spectre.Console;
using StackDev.Core.Ui;

namespace StackDev.Cli.Ui;

/// <summary>La pantalla interactiva: mensajes con color, confirmaciones y listas con flechas.</summary>
public sealed class SpectreUi : IConsolaUi
{
    public bool Interactiva => !Console.IsInputRedirected;

    public void Escribir(string texto, Nivel nivel = Nivel.Normal)
    {
        if (texto.Length == 0) { AnsiConsole.WriteLine(); return; }
        var lineas = texto.Split('\n');
        foreach (var l in lineas)
        {
            var e = Markup.Escape(l.TrimEnd('\r'));
            AnsiConsole.MarkupLine(nivel switch
            {
                Nivel.Titulo => $"[bold cyan]{e}[/]",
                Nivel.Info => $"[cyan]{e}[/]",
                Nivel.Ok => $"[green]{e}[/]",
                Nivel.Aviso => $"[yellow]{e}[/]",
                Nivel.Error => $"[red]{e}[/]",
                Nivel.Tenue => $"[grey]{e}[/]",
                _ => e,
            });
        }
    }

    public void EscribirInline(string texto) => AnsiConsole.Markup(Markup.Escape(texto));

    public bool Confirmar(string pregunta, bool porDefecto = true) =>
        AnsiConsole.Confirm(Markup.Escape(pregunta), porDefecto);

    public string? Preguntar(string pregunta, string? porDefecto = null)
    {
        var p = new TextPrompt<string>(Markup.Escape(pregunta)).AllowEmpty();
        if (!string.IsNullOrEmpty(porDefecto)) p = p.DefaultValue(porDefecto);
        var r = AnsiConsole.Prompt(p).Trim();
        return r.Length == 0 ? null : r;
    }

    public int Elegir(string titulo, IReadOnlyList<string> opciones, int porDefecto = -1)
    {
        // El primero de la lista es el que queda seleccionado: el valor por defecto va arriba.
        var orden = new List<int>();
        if (porDefecto >= 0 && porDefecto < opciones.Count) orden.Add(porDefecto);
        for (var i = 0; i < opciones.Count; i++) if (i != porDefecto) orden.Add(i);
        orden.Add(-1);

        var prompt = new SelectionPrompt<int>()
            .Title(Markup.Escape(titulo))
            .PageSize(15)
            .UseConverter(i => i < 0 ? "[grey]Cancelar[/]" : Markup.Escape(opciones[i]))
            .AddChoices(orden);
        return AnsiConsole.Prompt(prompt);
    }
}
