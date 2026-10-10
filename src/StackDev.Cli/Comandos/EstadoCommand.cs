using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using StackDev.Core.State;

namespace StackDev.Cli.Comandos;

public sealed class EstadoCommand : Command<EstadoCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--sincronizar")]
        [Description("Vuelve a copiar desde previous-state lo que sea más nuevo (útil si sigues usando the previous script).")]
        public bool Sincronizar { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var estado = StateStore.Abrir();
        AnsiConsole.MarkupLineInterpolated($"Estado:   [bold]{estado.Dir}[/]");
        AnsiConsole.MarkupLineInterpolated($"Anterior: {StateStore.DirAnterior()} [grey]({(Directory.Exists(StateStore.DirAnterior()) ? "existe; no se borra" : "no existe")})[/]");

        if (settings.Sincronizar)
        {
            var r = estado.Migrar(StateStore.DirAnterior(), forzar: true);
            AnsiConsole.MarkupLineInterpolated($"[green]Sincronizado:[/] {r.Copiados} archivos copiados, {r.Omitidos} sin cambios.");
        }

        AnsiConsole.MarkupLineInterpolated($"Último proyecto: {estado.UltimoProyecto ?? "(ninguno)"}");
        AnsiConsole.MarkupLineInterpolated($"Último emulador: {estado.UltimoAvdGlobal ?? "(ninguno)"}");
        return 0;
    }
}
