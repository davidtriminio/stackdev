using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using StackDev.Cli.Interactive;
using StackDev.Cli.Ui;
using StackDev.Core;
using StackDev.Core.State;

namespace StackDev.Cli.Commands;

public sealed class HomeSettings : CommonSettings
{
    [CommandArgument(0, "[ruta]")]
    [Description("Raíz del proyecto (igual que -p).")]
    public string? FilePath { get; init; }
}

/// <summary>Lo que corre al escribir <c>stackdev</c>: el panel en vivo con el menú.</summary>
public sealed class HomeCommand : Command<HomeSettings>
{
    public override int Execute(CommandContext context, HomeSettings st)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            AnsiConsole.MarkupLine("[yellow]El panel necesita una terminal interactiva.[/] Para scripts usa los comandos: [bold]stackdev --help[/].");
            return 2;
        }

        var state = StateStore.Open();
        var root = Project.Choose(st.Project ?? st.FilePath, state);
        if (root is null) { AnsiConsole.MarkupLine("[yellow]No se seleccionó proyecto.[/]"); return 1; }

        var s = new Session(root, state, new SpectreUi(), st.AsOptions());
        s.Start();
        return new InteractiveApp(s).Run();
    }
}
