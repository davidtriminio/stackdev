using Spectre.Console;
using StackDev.Core.Detection;
using StackDev.Core.Config;

namespace StackDev.Cli.Interactive;

/// <summary>Choose the project folder with the arrow keys: type the path, search for projects or browse folders.</summary>
public static class ProjectSelector
{
    private const string Write = "e", Search = "b", Explore = "x", Cancel = "c", Last = "u";

    public static string? Choose(string? last)
    {
        while (true)
        {
            var options = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(last) && Directory.Exists(last)) options[Last] = $"Último proyecto: {last}";
            options[Search] = "Buscar proyectos dentro de una carpeta…";
            options[Explore] = "Explorar carpetas…";
            options[Write] = "Escribir una ruta…";
            options[Cancel] = "Cancelar";

            var sel = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title("[bold]¿Qué proyecto quieres abrir?[/]")
                .UseConverter(k => Markup.Escape(options[k]))
                .AddChoices(options.Keys));

            string? path = sel switch
            {
                Last => last,
                Search => SearchIn(),
                Explore => BrowseFolders(last),
                Write => AskPath(),
                _ => null,
            };
            if (sel == Cancel) return null;
            if (path is not null) return path;
        }
    }

    private static string? AskPath()
    {
        var t = AnsiConsole.Prompt(new TextPrompt<string>("Ruta de la carpeta [grey](vacío = volver)[/]:").AllowEmpty()).Trim().Trim('"');
        if (t.Length == 0) return null;
        if (!Directory.Exists(t)) { AnsiConsole.MarkupLineInterpolated($"[red]No existe:[/] {t}"); return null; }
        return Path.GetFullPath(t);
    }

    private static string? SearchIn()
    {
        var baseDir = AnsiConsole.Prompt(new TextPrompt<string>("¿Dentro de qué carpeta busco? [grey](vacío = volver)[/]:").AllowEmpty()).Trim().Trim('"');
        if (baseDir.Length == 0) return null;
        if (!Directory.Exists(baseDir)) { AnsiConsole.MarkupLineInterpolated($"[red]No existe:[/] {baseDir}"); return null; }

        var found = AnsiConsole.Status().Start("Buscando proyectos…", _ => Explorer.SearchProjects(baseDir));
        if (found.Count == 0) { AnsiConsole.MarkupLine("[yellow]No encontré proyectos (carpetas con .git o dev-menu.json) hasta 3 niveles.[/]"); return null; }

        var list = found.Select(p => Path.GetRelativePath(baseDir, p)).ToList();
        list.Insert(0, "← Volver");
        var i = list.IndexOf(AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title($"Proyectos en {Markup.Escape(baseDir)}")
            .PageSize(15).UseConverter(Markup.Escape).AddChoices(list)));
        return i <= 0 ? null : found[i - 1];
    }

    private static string? BrowseFolders(string? home)
    {
        var current = !string.IsNullOrWhiteSpace(home) && Directory.Exists(home)
            ? Path.GetDirectoryName(home) ?? home
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        while (true)
        {
            var labels = new List<string>();
            var actions = new List<(string Tipo, string Valor)>();
            void Add(string e, string t, string v = "") { labels.Add(e); actions.Add((t, v)); }

            Add("✔ Usar esta carpeta", "usar");
            var parent = Path.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(parent)) Add("⬆ Subir", "ir", parent);
            Add("💽 Cambiar de unidad…", "unidades");
            Add("← Volver", "volver");
            foreach (var sub in Explorer.Subfolders(current))
            {
                var path = Path.Combine(current, sub);
                var mark = ProjectRoot.IsProject(path) ? " [proyecto]" : "";
                Add($"  {sub}{mark}", "ir", path);
            }

            var chosen = AnsiConsole.Prompt(new SelectionPrompt<int>()
                .Title($"[bold]{Markup.Escape(current)}[/]")
                .PageSize(18).MoreChoicesText("[grey](↑↓ para ver más)[/]")
                .UseConverter(i => Markup.Escape(labels[i]))
                .AddChoices(Enumerable.Range(0, labels.Count)));

            var (type, val) = actions[chosen];
            switch (type)
            {
                case "usar": return current;
                case "ir": current = val; break;
                case "volver": return null;
                case "unidades":
                    var u = Explorer.Units();
                    if (u.Count == 0) break;
                    current = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Unidad").AddChoices(u));
                    break;
            }
        }
    }
}
