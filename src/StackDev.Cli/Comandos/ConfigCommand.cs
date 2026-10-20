using Spectre.Console;
using Spectre.Console.Cli;
using StackDev.Core.Config;
using StackDev.Core.State;
using StackDev.Core.Util;

namespace StackDev.Cli.Comandos;

public sealed class ConfigCommand : Command<RutaSettings>
{
    public override int Execute(CommandContext context, RutaSettings settings)
    {
        var estado = StateStore.Abrir();
        var raiz = Proyecto.Elegir(settings.Ruta, estado);
        if (raiz is null) { AnsiConsole.MarkupLine("[yellow]No se seleccionó proyecto.[/]"); return 1; }

        var store = new ConfigStore(raiz, estado.CarpetaConfig(StateStore.Clave(raiz)));
        if (!store.Existe)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]No hay {ConfigStore.NombreArchivo} en {raiz}.[/] Créalo con [bold]stackdev detectar --guardar[/].");
            return 1;
        }
        store.Cargar();
        if (store.Error is not null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ConfigStore.NombreArchivo} tiene un error:[/] {store.Error}");
            return 1;
        }

        var ctx = store.Contexto;
        AnsiConsole.MarkupLineInterpolated($"[bold cyan]{ctx.Nombre}[/]  [grey]{raiz}[/]");
        AnsiConsole.MarkupLineInterpolated($"[grey]Clave del estado: {StateStore.Clave(raiz)}[/]\n");

        var tabla = new Table().Border(TableBorder.Rounded).AddColumn("Componente").AddColumn("Carpeta").AddColumn("URL / puerto").AddColumn("Estado");
        Fila(tabla, "Postgres", ctx, Componente.Postgres,
            ctx.Config.Postgres is { } pg ? $"localhost:{pg.PuertoEf}  ({pg.ContenedorEf}, {pg.ImagenEf})" : "");
        Fila(tabla, "API", ctx, Componente.Api, ctx.Url(Componente.Api) ?? "");
        Fila(tabla, "Web", ctx, Componente.Web, ctx.Url(Componente.Web) ?? "");
        Fila(tabla, "Móvil", ctx, Componente.Movil, ctx.Config.Movil?.AndroidSdk is { Length: > 0 } sdk ? $"SDK {sdk}" : "");
        AnsiConsole.Write(tabla);

        if (ctx.Tiene(Componente.Api))
        {
            var env = ctx.EnvParaCarpeta(ctx.Carpeta(Componente.Api));
            AnsiConsole.MarkupLine("\n[bold]Variables de entorno de la API[/] [grey](también valen para cualquier comando dentro de esa carpeta)[/]");
            if (env is null || env.Count == 0) AnsiConsole.MarkupLine("  [grey](ninguna)[/]");
            else foreach (var (k, v) in env) AnsiConsole.MarkupLineInterpolated($"  {k} = {Ocultar(v)}");
        }

        AnsiConsole.MarkupLineInterpolated($"\n[bold]Levantar todo:[/] {string.Join(" → ", ctx.LevantarTodo)}");
        if (ctx.Tareas.Count > 0)
        {
            AnsiConsole.MarkupLine("[bold]Tareas:[/]");
            foreach (var t in ctx.Tareas)
            {
                var pregunta = Preguntas.TienePreguntas(t.Comando ?? "") ? "  [pide un valor]" : "";
                AnsiConsole.MarkupLineInterpolated($"  {t.Nombre}  [grey]({t.Carpeta ?? "."}){pregunta}[/]");
            }
        }
        return 0;
    }

    private static void Fila(Table tabla, string nombre, ProyectoContexto ctx, Componente c, string detalle)
    {
        if (!ctx.Tiene(c)) { tabla.AddRow(nombre, "[grey]-[/]", "[grey]-[/]", "[grey]no configurado[/]"); return; }
        var carpeta = ctx.Carpeta(c);
        var existe = c == Componente.Postgres || Directory.Exists(carpeta);
        tabla.AddRow(
            Markup.Escape(nombre),
            Markup.Escape(Rutas.Relativa(ctx.Raiz, carpeta)),
            Markup.Escape(detalle),
            existe ? "[green]OK[/]" : "[red]la carpeta no existe[/]");
    }

    /// <summary>Muestra la cadena de conexión sin la contraseña.</summary>
    private static string Ocultar(string valor) =>
        System.Text.RegularExpressions.Regex.Replace(valor, "(?i)(password|pwd)=[^;]*", "$1=****");
}
