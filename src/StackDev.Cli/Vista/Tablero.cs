using StackDev.Core.Monitoreo;
using Spectre.Console;
using Spectre.Console.Rendering;
using StackDev.Core;
using StackDev.Core.Android;
using StackDev.Core.Config;
using StackDev.Core.Docker;
using StackDev.Core.Servicios;

namespace StackDev.Cli.Vista;

/// <summary>Dibuja el panel de estado.</summary>
public static class Tablero
{
    private static string Color(CodigoServicio c) => c switch
    {
        CodigoServicio.Corriendo => "green",
        CodigoServicio.Arrancando => "yellow",
        CodigoServicio.Error => "red",
        _ => "grey",
    };

    private static string Color(CodigoPostgres c) => c switch
    {
        CodigoPostgres.Corriendo => "green",
        CodigoPostgres.Detenido => "grey",
        _ => "yellow",
    };

    private static string Color(CodigoEmu c) => c switch
    {
        CodigoEmu.Ok => "green",
        CodigoEmu.NoResponde => "red",
        _ => "grey",
    };

    private static string Punto(string color) => $"[{color}]●[/]";

    public static IRenderable Construir(Sesion s, Instantanea? i, string codigo, string? aviso = null, bool conPie = true)
    {
        var c = s.Ctx;
        var tabla = new Table().Border(TableBorder.None).HideHeaders().Expand();
        tabla.AddColumn(new TableColumn("").NoWrap().Width(12));
        tabla.AddColumn(new TableColumn(""));

        void Fila(string nombre, string color, string texto, string? extra = null)
        {
            var detalle = $"{Punto(color)} [{color}]{Markup.Escape(texto)}[/]";
            if (!string.IsNullOrEmpty(extra)) detalle += $"  [grey]{Markup.Escape(extra)}[/]";
            tabla.AddRow($"[bold]{nombre}[/]", detalle);
        }

        if (i is null)
        {
            tabla.AddRow("", "[grey]Consultando el estado…[/]");
        }
        else
        {
            if (c.Tiene(Componente.Postgres) && i.Postgres is { } pg) Fila("Postgres", Color(pg.Codigo), pg.Texto);
            foreach (var (comp, nombre, e, log) in new[]
                     {
                         (Componente.Api, "API", i.Api, i.LogApi),
                         (Componente.Web, "Web", i.Web, i.LogWeb),
                     })
            {
                if (!c.Tiene(comp) || e is null) continue;
                Fila(nombre, Color(e.Codigo), e.Texto, e.Corriendo ? e.Url : log);
            }
            if (c.Tiene(Componente.Movil))
            {
                if (i.Emulador is { } emu) Fila("Emulador", Color(emu.Codigo), emu.Texto);
                else Fila("Emulador", "grey", "sin Android SDK");
                if (i.Telefonos.Length > 0) Fila("Teléfonos", "green", i.Telefonos);
                Fila("App", i.App ? "green" : "grey", i.App ? "CORRIENDO (flutter run)" : "detenida", i.App ? s.ModoBuild : null);
            }
        }

        var cab = new List<string> { $"[bold cyan]{Markup.Escape(c.Nombre)}[/]  [grey]{Markup.Escape(s.Raiz)}[/]" };
        if (c.Tiene(Componente.Movil))
        {
            var cond = i?.Condiciones ?? "";
            cab.Add($"[grey]Modo:[/] {s.ModoBuild}   [grey]Emulador:[/] {Markup.Escape(s.Avd ?? "(ninguno)")}" +
                    (string.IsNullOrEmpty(cond) ? "" : $"   [grey]Condiciones:[/] {Markup.Escape(cond)}"));
        }

        var pie = string.IsNullOrEmpty(codigo)
            ? "[grey]Código + Enter (a1, w1, d5, 1…) · Enter o ↓ = menú · ? = códigos · q = salir[/]"
            : $"[bold]Código:[/] {Markup.Escape(codigo)}[grey]▌[/]  [grey](Enter = ejecutar · Esc = borrar)[/]";
        if (!string.IsNullOrEmpty(aviso)) pie = $"[yellow]{Markup.Escape(aviso)}[/]\n" + pie;

        var partes = new List<IRenderable> { new Markup(string.Join("\n", cab)), new Rule().RuleStyle("grey"), tabla };
        if (conPie) { partes.Add(new Rule().RuleStyle("grey")); partes.Add(new Markup(pie)); }
        var cuerpo = new Rows(partes);

        return new Panel(cuerpo).Border(BoxBorder.Rounded).Header($"[bold] stackdev [/]").Expand();
    }
}
