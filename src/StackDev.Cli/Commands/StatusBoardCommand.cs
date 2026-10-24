using Spectre.Console;
using Spectre.Console.Cli;
using StackDev.Core.State;

namespace StackDev.Cli.Commands;

public sealed class StatusBoardCommand : Command<StatusBoardCommand.Settings>
{
    public sealed class Settings : CommandSettings { }

    public override int Execute(CommandContext context, Settings settings)
    {
        var state = StateStore.Open();
        AnsiConsole.MarkupLineInterpolated($"Estado:   [bold]{state.Dir}[/]");

        AnsiConsole.MarkupLineInterpolated($"Último proyecto: {state.LastProject ?? "(ninguno)"}");
        AnsiConsole.MarkupLineInterpolated($"Último emulador: {state.GlobalLastAvd ?? "(ninguno)"}");
        return 0;
    }
}
