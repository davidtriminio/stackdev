using StackDev.Core.Monitoring;
using Spectre.Console;
using Spectre.Console.Rendering;
using StackDev.Core;
using StackDev.Core.Android;
using StackDev.Core.Config;
using StackDev.Core.Docker;
using StackDev.Core.Services;

namespace StackDev.Cli.Views;

/// <summary>Draws the status panel.</summary>
public static class Board
{
    private static string Color(ServiceCode c) => c switch
    {
        ServiceCode.Running => "green",
        ServiceCode.Starting => "yellow",
        ServiceCode.Error => "red",
        _ => "grey",
    };

    private static string Color(PostgresCode c) => c switch
    {
        PostgresCode.Running => "green",
        PostgresCode.Stopped => "grey",
        _ => "yellow",
    };

    private static string Color(EmuCode c) => c switch
    {
        EmuCode.Ok => "green",
        EmuCode.NotResponding => "red",
        _ => "grey",
    };

    private static string Dot(string color) => $"[{color}]●[/]";

    public static IRenderable Build(Session s, Snapshot? i, string code, string? warning = null, bool withFooter = true)
    {
        var c = s.Ctx;
        var table = new Table().Border(TableBorder.None).HideHeaders().Expand();
        table.AddColumn(new TableColumn("").NoWrap().Width(12));
        table.AddColumn(new TableColumn(""));

        void Row(string name, string color, string text, string? extra = null)
        {
            var detail = $"{Dot(color)} [{color}]{Markup.Escape(text)}[/]";
            if (!string.IsNullOrEmpty(extra)) detail += $"  [grey]{Markup.Escape(extra)}[/]";
            table.AddRow($"[bold]{name}[/]", detail);
        }

        if (i is null)
        {
            table.AddRow("", "[grey]Consultando el estado…[/]");
        }
        else
        {
            if (c.Has(ProjectComponent.Postgres) && i.Postgres is { } pg) Row("Postgres", Color(pg.Code), pg.Text);
            foreach (var (comp, name, e, log) in new[]
                     {
                         (ProjectComponent.Api, "API", i.Api, i.LogApi),
                         (ProjectComponent.Web, "Web", i.Web, i.LogWeb),
                     })
            {
                if (!c.Has(comp) || e is null) continue;
                Row(name, Color(e.Code), e.Text, e.Running ? e.Url : log);
            }
            if (c.Has(ProjectComponent.Mobile))
            {
                if (i.Emulator is { } emu) Row("Emulador", Color(emu.Code), emu.Text);
                else Row("Emulador", "grey", "sin Android SDK");
                if (i.Phones.Length > 0) Row("Teléfonos", "green", i.Phones);
                Row("App", i.App ? "green" : "grey", i.App ? "CORRIENDO (flutter run)" : "detenida", i.App ? s.BuildMode : null);
            }
        }

        var cab = new List<string> { $"[bold cyan]{Markup.Escape(c.Name)}[/]  [grey]{Markup.Escape(s.Root)}[/]" };
        if (c.Has(ProjectComponent.Mobile))
        {
            var cond = i?.Conditions ?? "";
            cab.Add($"[grey]Modo:[/] {s.BuildMode}   [grey]Emulador:[/] {Markup.Escape(s.Avd ?? "(ninguno)")}" +
                    (string.IsNullOrEmpty(cond) ? "" : $"   [grey]Condiciones:[/] {Markup.Escape(cond)}"));
        }

        var footer = string.IsNullOrEmpty(code)
            ? "[grey]Código + Enter (a1, w1, d5, 1…) · Enter o ↓ = menú · ? = códigos · q = salir[/]"
            : $"[bold]Código:[/] {Markup.Escape(code)}[grey]▌[/]  [grey](Enter = ejecutar · Esc = borrar)[/]";
        if (!string.IsNullOrEmpty(warning)) footer = $"[yellow]{Markup.Escape(warning)}[/]\n" + footer;

        var parts = new List<IRenderable> { new Markup(string.Join("\n", cab)), new Rule().RuleStyle("grey"), table };
        if (withFooter) { parts.Add(new Rule().RuleStyle("grey")); parts.Add(new Markup(footer)); }
        var body = new Rows(parts);

        return new Panel(body).Border(BoxBorder.Rounded).Header($"[bold] stackdev [/]").Expand();
    }
}
