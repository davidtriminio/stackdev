using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using StackDev.Core.Config;
using StackDev.Core.Detection;
using StackDev.Core.State;

namespace StackDev.Cli.Commands;

public sealed class DetectCommand : Command<DetectCommand.Settings>
{
    public sealed class Settings : PathSettings
    {
        [CommandOption("--guardar")]
        [Description("Escribe dev-menu.json con lo detectado (si no existe).")]
        public bool Save { get; init; }

        [CommandOption("--forzar")]
        [Description("Con --guardar: reemplaza dev-menu.json existente dejando una copia .bak.")]
        public bool Force { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var state = StateStore.Open();
        var root = Project.Choose(settings.FilePath, state);
        if (root is null) { AnsiConsole.MarkupLine("[yellow]No se seleccionó proyecto.[/]"); return 1; }

        AnsiConsole.MarkupLineInterpolated($"[grey]Buscando componentes en {root} ...[/]");
        var d = Detector.Detect(root);
        foreach (var h in d.Findings) AnsiConsole.MarkupLineInterpolated($"  [green]{h}[/]");

        if (d.ApiUsesNpgsql)
        {
            AnsiConsole.MarkupLine("\n[cyan]La API usa PostgreSQL.[/]");
            AnsiConsole.MarkupLineInterpolated($"[grey]Nombre de la cadena de conexión: {d.ConnectionName ?? "(no encontrado)"}[/]");
            AnsiConsole.MarkupLine("[yellow]Leer el contenedor con docker inspect llega en la fase 3; por ahora agrega la sección \"postgres\" a mano.[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Text(ConfigJson.Write(d.Config)));

        if (!settings.Save) return 0;

        var store = new ConfigStore(root, state.ConfigFolder(StateStore.Key(root)));
        if (store.Exists && !settings.Force)
        {
            AnsiConsole.MarkupLine("[red]dev-menu.json ya existe.[/] Usa [bold]--guardar --forzar[/] para reemplazarlo (queda una copia .bak).");
            return 1;
        }
        if (store.Exists) store.Replace(d.Config); else store.Save(d.Config);
        AnsiConsole.MarkupLineInterpolated($"[green]Guardado en {store.FilePath}[/]");
        return 0;
    }
}
