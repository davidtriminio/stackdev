using Spectre.Console;
using StackDev.Core.Detection;
using StackDev.Core.Config;

namespace StackDev.Cli.Interactivo;

/// <summary>Elegir la carpeta del proyecto con flechas: escribir la ruta, buscar proyectos o explorar carpetas.</summary>
public static class SelectorProyecto
{
    private const string Escribir = "e", Buscar = "b", Explorar = "x", Cancelar = "c", Ultimo = "u";

    public static string? Elegir(string? ultimo)
    {
        while (true)
        {
            var opciones = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(ultimo) && Directory.Exists(ultimo)) opciones[Ultimo] = $"Último proyecto: {ultimo}";
            opciones[Buscar] = "Buscar proyectos dentro de una carpeta…";
            opciones[Explorar] = "Explorar carpetas…";
            opciones[Escribir] = "Escribir una ruta…";
            opciones[Cancelar] = "Cancelar";

            var sel = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title("[bold]¿Qué proyecto quieres abrir?[/]")
                .UseConverter(k => Markup.Escape(opciones[k]))
                .AddChoices(opciones.Keys));

            string? ruta = sel switch
            {
                Ultimo => ultimo,
                Buscar => BuscarEn(),
                Explorar => ExplorarCarpetas(ultimo),
                Escribir => PedirRuta(),
                _ => null,
            };
            if (sel == Cancelar) return null;
            if (ruta is not null) return ruta;
        }
    }

    private static string? PedirRuta()
    {
        var t = AnsiConsole.Prompt(new TextPrompt<string>("Ruta de la carpeta [grey](vacío = volver)[/]:").AllowEmpty()).Trim().Trim('"');
        if (t.Length == 0) return null;
        if (!Directory.Exists(t)) { AnsiConsole.MarkupLineInterpolated($"[red]No existe:[/] {t}"); return null; }
        return Path.GetFullPath(t);
    }

    private static string? BuscarEn()
    {
        var baseDir = AnsiConsole.Prompt(new TextPrompt<string>("¿Dentro de qué carpeta busco? [grey](vacío = volver)[/]:").AllowEmpty()).Trim().Trim('"');
        if (baseDir.Length == 0) return null;
        if (!Directory.Exists(baseDir)) { AnsiConsole.MarkupLineInterpolated($"[red]No existe:[/] {baseDir}"); return null; }

        var encontrados = AnsiConsole.Status().Start("Buscando proyectos…", _ => Explorador.BuscarProyectos(baseDir));
        if (encontrados.Count == 0) { AnsiConsole.MarkupLine("[yellow]No encontré proyectos (carpetas con .git o dev-menu.json) hasta 3 niveles.[/]"); return null; }

        var lista = encontrados.Select(p => Path.GetRelativePath(baseDir, p)).ToList();
        lista.Insert(0, "← Volver");
        var i = lista.IndexOf(AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title($"Proyectos en {Markup.Escape(baseDir)}")
            .PageSize(15).UseConverter(Markup.Escape).AddChoices(lista)));
        return i <= 0 ? null : encontrados[i - 1];
    }

    private static string? ExplorarCarpetas(string? inicio)
    {
        var actual = !string.IsNullOrWhiteSpace(inicio) && Directory.Exists(inicio)
            ? Path.GetDirectoryName(inicio) ?? inicio
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        while (true)
        {
            var etiquetas = new List<string>();
            var acciones = new List<(string Tipo, string Valor)>();
            void Add(string e, string t, string v = "") { etiquetas.Add(e); acciones.Add((t, v)); }

            Add("✔ Usar esta carpeta", "usar");
            var padre = Path.GetDirectoryName(actual);
            if (!string.IsNullOrEmpty(padre)) Add("⬆ Subir", "ir", padre);
            Add("💽 Cambiar de unidad…", "unidades");
            Add("← Volver", "volver");
            foreach (var sub in Explorador.Subcarpetas(actual))
            {
                var ruta = Path.Combine(actual, sub);
                var marca = RaizProyecto.EsProyecto(ruta) ? " [proyecto]" : "";
                Add($"  {sub}{marca}", "ir", ruta);
            }

            var elegido = AnsiConsole.Prompt(new SelectionPrompt<int>()
                .Title($"[bold]{Markup.Escape(actual)}[/]")
                .PageSize(18).MoreChoicesText("[grey](↑↓ para ver más)[/]")
                .UseConverter(i => Markup.Escape(etiquetas[i]))
                .AddChoices(Enumerable.Range(0, etiquetas.Count)));

            var (tipo, valor) = acciones[elegido];
            switch (tipo)
            {
                case "usar": return actual;
                case "ir": actual = valor; break;
                case "volver": return null;
                case "unidades":
                    var u = Explorador.Unidades();
                    if (u.Count == 0) break;
                    actual = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Unidad").AddChoices(u));
                    break;
            }
        }
    }
}
