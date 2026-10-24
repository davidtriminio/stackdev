using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using StackDev.Core.State;

namespace StackDev.Cli.Commands;

public sealed class StatusBoardCommand : Command<StatusBoardCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--sincronizar")]
        [Description("Vuelve a copiar desde previous-state lo que sea más nuevo (útil si sigues usando the previous script).")]
        public bool Sync { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var state = StateStore.Open();
        AnsiConsole.MarkupLineInterpolated($"Estado:   [bold]{state.Dir}[/]");
        AnsiConsole.MarkupLineInterpolated($"Anterior: {StateStore.DirPrevious()} [grey]({(Directory.Exists(StateStore.DirPrevious()) ? "existe; no se borra" : "no existe")})[/]");

        if (settings.Sync)
        {
            var r = state.Migrate(StateStore.DirPrevious(), force: true);
            AnsiConsole.MarkupLineInterpolated($"[green]Sincronizado:[/] {r.Copied} archivos copiados, {r.Skipped} sin cambios.");
        }

        AnsiConsole.MarkupLineInterpolated($"Último proyecto: {state.LastProject ?? "(ninguno)"}");
        AnsiConsole.MarkupLineInterpolated($"Último emulador: {state.GlobalLastAvd ?? "(ninguno)"}");
        return 0;
    }
}
