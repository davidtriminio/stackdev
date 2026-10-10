using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using StackDev.Core.Config;
using StackDev.Core.Detection;
using StackDev.Core.State;

namespace StackDev.Cli.Comandos;

public sealed class DetectarCommand : Command<DetectarCommand.Settings>
{
    public sealed class Settings : RutaSettings
    {
        [CommandOption("--guardar")]
        [Description("Escribe dev-menu.json con lo detectado (si no existe).")]
        public bool Guardar { get; init; }

        [CommandOption("--forzar")]
        [Description("Con --guardar: reemplaza dev-menu.json existente dejando una copia .bak.")]
        public bool Forzar { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var estado = StateStore.Abrir();
        var raiz = Proyecto.Elegir(settings.Ruta, estado);
        if (raiz is null) { AnsiConsole.MarkupLine("[yellow]No se seleccionó proyecto.[/]"); return 1; }

        AnsiConsole.MarkupLineInterpolated($"[grey]Buscando componentes en {raiz} ...[/]");
        var d = Detector.Detectar(raiz);
        foreach (var h in d.Hallazgos) AnsiConsole.MarkupLineInterpolated($"  [green]{h}[/]");

        if (d.ApiUsaNpgsql)
        {
            AnsiConsole.MarkupLine("\n[cyan]La API usa PostgreSQL.[/]");
            AnsiConsole.MarkupLineInterpolated($"[grey]Nombre de la cadena de conexión: {d.NombreConexion ?? "(no encontrado)"}[/]");
            AnsiConsole.MarkupLine("[yellow]Leer el contenedor con docker inspect llega en la fase 3; por ahora agrega la sección \"postgres\" a mano.[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Text(ConfigJson.Escribir(d.Config)));

        if (!settings.Guardar) return 0;

        var store = new ConfigStore(raiz, estado.CarpetaConfig(StateStore.Clave(raiz)));
        if (store.Existe && !settings.Forzar)
        {
            AnsiConsole.MarkupLine("[red]dev-menu.json ya existe.[/] Usa [bold]--guardar --forzar[/] para reemplazarlo (queda una copia .bak).");
            return 1;
        }
        if (store.Existe) store.Reemplazar(d.Config); else store.Guardar(d.Config);
        AnsiConsole.MarkupLineInterpolated($"[green]Guardado en {store.Ruta}[/]");
        return 0;
    }
}
