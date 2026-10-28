using Spectre.Console;
using Spectre.Console.Cli;
using StackDev.Core.Config;
using StackDev.Core.State;
using StackDev.Core.Util;

namespace StackDev.Cli.Commands;

public sealed class ConfigCommand : Command<PathSettings>
{
    public override int Execute(CommandContext context, PathSettings settings)
    {
        var state = StateStore.Open();
        var root = Project.Choose(settings.FilePath, state);
        if (root is null) { AnsiConsole.MarkupLine("[yellow]No se seleccionó proyecto.[/]"); return 1; }

        var store = new ConfigStore(root, state.ConfigFolder(StateStore.Key(root)));
        if (!store.Exists)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]No hay {ConfigStore.FileName} en {root}.[/] Créalo con [bold]stackdev detectar --guardar[/].");
            return 1;
        }
        store.Load();
        if (store.Error is not null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ConfigStore.FileName} tiene un error:[/] {store.Error}");
            return 1;
        }

        var ctx = store.Context;
        AnsiConsole.MarkupLineInterpolated($"[bold cyan]{ctx.Name}[/]  [grey]{root}[/]");
        AnsiConsole.MarkupLineInterpolated($"[grey]Clave del estado: {StateStore.Key(root)}[/]\n");

        var table = new Table().Border(TableBorder.Rounded).AddColumn("Componente").AddColumn("Carpeta").AddColumn("URL / puerto").AddColumn("Estado");
        Row(table, "Postgres", ctx, ProjectComponent.Postgres,
            ctx.Config.Postgres is { } pg ? $"localhost:{pg.EfPort}  ({pg.EfContainer}, {pg.EfImage})" : "");
        Row(table, "API", ctx, ProjectComponent.Api, ctx.Url(ProjectComponent.Api) ?? "");
        Row(table, "Web", ctx, ProjectComponent.Web, ctx.Url(ProjectComponent.Web) ?? "");
        Row(table, "Móvil", ctx, ProjectComponent.Mobile, ctx.Config.Mobile?.AndroidSdk is { Length: > 0 } sdk ? $"SDK {sdk}" : "");
        AnsiConsole.Write(table);

        if (ctx.Has(ProjectComponent.Api))
        {
            var env = ctx.EnvForFolder(ctx.Folder(ProjectComponent.Api));
            AnsiConsole.MarkupLine("\n[bold]Variables de entorno de la API[/] [grey](también valen para cualquier comando dentro de esa carpeta)[/]");
            if (env is null || env.Count == 0) AnsiConsole.MarkupLine("  [grey](ninguna)[/]");
            else foreach (var (k, v) in env) AnsiConsole.MarkupLineInterpolated($"  {k} = {Hide(v)}");
        }

        AnsiConsole.MarkupLineInterpolated($"\n[bold]Levantar todo:[/] {string.Join(" → ", ctx.StartAll)}");
        if (ctx.Tasks.Count > 0)
        {
            AnsiConsole.MarkupLine("[bold]Tareas:[/]");
            foreach (var t in ctx.Tasks)
            {
                var question = Prompts.HasPrompts(t.Command ?? "") ? "  [pide un valor]" : "";
                AnsiConsole.MarkupLineInterpolated($"  {t.Name}  [grey]({t.Folder ?? "."}){question}[/]");
            }
        }
        return 0;
    }

    private static void Row(Table table, string name, ProjectContext ctx, ProjectComponent c, string detail)
    {
        if (!ctx.Has(c)) { table.AddRow(name, "[grey]-[/]", "[grey]-[/]", "[grey]no configurado[/]"); return; }
        var folder = ctx.Folder(c);
        var exists = c == ProjectComponent.Postgres || Directory.Exists(folder);
        table.AddRow(
            Markup.Escape(name),
            Markup.Escape(Paths.Relative(ctx.Root, folder)),
            Markup.Escape(detail),
            exists ? "[green]OK[/]" : "[red]la carpeta no existe[/]");
    }

    /// <summary>Shows the connection string without the password.</summary>
    private static string Hide(string val) =>
        System.Text.RegularExpressions.Regex.Replace(val, "(?i)(password|pwd)=[^;]*", "$1=****");
}
