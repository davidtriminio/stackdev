using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using StackDev.Cli.Interactivo;
using StackDev.Cli.Ui;
using StackDev.Core;
using StackDev.Core.State;

namespace StackDev.Cli.Comandos;

public sealed class InicioSettings : ComandoSettings
{
    [CommandArgument(0, "[ruta]")]
    [Description("Raíz del proyecto (igual que -p).")]
    public string? Ruta { get; init; }
}

/// <summary>Lo que corre al escribir <c>stackdev</c>: el panel en vivo con el menú.</summary>
public sealed class InicioCommand : Command<InicioSettings>
{
    public override int Execute(CommandContext context, InicioSettings st)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            AnsiConsole.MarkupLine("[yellow]El panel necesita una terminal interactiva.[/] Para scripts usa los comandos: [bold]stackdev --help[/].");
            return 2;
        }

        var estado = StateStore.Abrir();
        var raiz = Proyecto.Elegir(st.Proyecto ?? st.Ruta, estado);
        if (raiz is null) { AnsiConsole.MarkupLine("[yellow]No se seleccionó proyecto.[/]"); return 1; }

        var s = new Sesion(raiz, estado, new SpectreUi(), st.ComoOpciones());
        s.Iniciar();
        return new AppInteractiva(s).Correr();
    }
}
