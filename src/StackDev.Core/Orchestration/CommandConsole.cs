using StackDev.Core.Config;
using StackDev.Core.Ui;
using StackDev.Core.Util;

namespace StackDev.Core.Orchestration;

public sealed record CommandItem(string Folder, string Command, bool Task, string Title);

/// <summary>
/// A console to run any command in a project folder: tasks from the configuration,
/// history of the last 10 per project, folder change (<c>cd</c> and <c>@api</c> <c>@web</c> <c>@movil</c> <c>@raiz</c>),
/// <c>v &lt;command&gt;</c> in another window and <c>t</c> to open a terminal.
/// </summary>
public sealed class CommandConsole
{
    private static readonly Dictionary<string, string> Alias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["@api"] = "api", ["@web"] = "web", ["@movil"] = "movil", ["@mobile"] = "movil", ["@raiz"] = "raiz",
    };

    private readonly Session _s;

    public CommandConsole(Session s)
    {
        _s = s;
        Dir = s.Root;
    }

    public string Dir { get; private set; }

    public string Prompt => $"[{_s.Rel(Dir)}]>";

    /// <summary>Project tasks and recent commands, numbered in that order.</summary>
    public List<CommandItem> Items()
    {
        var items = new List<CommandItem>();
        foreach (var t in _s.Ctx.Tasks)
            items.Add(new CommandItem(t.Folder ?? ".", t.Command ?? "", true, $"{t.Name,-34} [{(string.IsNullOrEmpty(t.Folder) ? "." : t.Folder)}]"));
        foreach (var r in _s.State.ReadRecent(_s.Key))
            items.Add(new CommandItem(r.Folder, r.Command, false, $"[{r.Folder}] {r.Command}"));
        return items;
    }

    public string Help()
    {
        var alias = string.Join(" ", Alias.Where(kv => kv.Value == "raiz" || _s.Ctx.Has(Comp(kv.Value))).Select(kv => kv.Key).OrderBy(x => x));
        var extra = _s.Ctx.EnvForFolder(Dir) is not null ? "\n  (en esta carpeta se aplican las variables de la API, p. ej. la cadena de conexión)" : "";
        return $"  Escribe un comando | número = ejecutar | v <comando> = en otra ventana\n  cd <carpeta> o {alias} | t = abrir terminal | Enter = volver{extra}";
    }

    private static ProjectComponent Comp(string n) => n switch { "api" => ProjectComponent.Api, "web" => ProjectComponent.Web, _ => ProjectComponent.Mobile };

    /// <summary>Processes a line. Returns false when the console must be left (empty line).</summary>
    public bool HandleInput(string? input)
    {
        input = input?.Trim();
        if (string.IsNullOrEmpty(input)) return false;

        if (input == "t") { _s.Executor.OpenTerminal(Dir); return true; }

        if (Alias.TryGetValue(input, out var comp))
        {
            if (comp == "raiz") Dir = _s.Root;
            else if (_s.Ctx.Has(Comp(comp))) Dir = _s.Ctx.Folder(Comp(comp));
            else _s.Ui.Write($"Este proyecto no tiene {comp}.", Level.Warning);
            return true;
        }
        if (input.StartsWith("cd ", StringComparison.OrdinalIgnoreCase))
        {
            var dest = input[3..].Trim().Trim('"');
            var fresh = Path.IsPathRooted(dest) ? dest : Path.Combine(Dir, dest);
            if (Directory.Exists(fresh)) Dir = Path.GetFullPath(fresh);
            else _s.Ui.Write($"No existe: {fresh}", Level.Error);
            return true;
        }

        var inWindow = false;
        if (input.StartsWith("v ", StringComparison.OrdinalIgnoreCase)) { inWindow = true; input = input[2..].Trim(); }

        var folderCmd = Dir;
        var command = input;
        var rel = _s.Rel(Dir);
        if (int.TryParse(input, out var n))
        {
            var items = Items();
            if (n < 1 || n > items.Count) { _s.Ui.Write("Número fuera de rango.", Level.Error); return true; }
            var it = items[n - 1];
            folderCmd = Paths.Resolve(_s.Root, it.Folder);
            var expanded = Prompts.Expand(it.Command, t => _s.Ui.Ask(t));
            if (expanded is null) { _s.Ui.Write("Cancelado.", Level.Muted); return true; }
            command = expanded;
            if (!it.Task) _s.State.AddRecent(_s.Key, it.Folder, it.Command);
        }
        else _s.State.AddRecent(_s.Key, rel, command);

        if (inWindow)
        {
            if (_s.Executor.InWindow("cmd", command, folderCmd, command))
                _s.Ui.Write($"Corriendo en otra ventana: {command}", Level.Muted);
        }
        else _s.Executor.Here(command, folderCmd);
        return true;
    }

    /// <summary>Runs a configuration task by name (or part of the name). For <c>stackdev tarea "..."</c>.</summary>
    public int RunTask(string name)
    {
        var t = _s.Ctx.Tasks.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
             ?? _s.Ctx.Tasks.FirstOrDefault(x => x.Name?.Contains(name, StringComparison.OrdinalIgnoreCase) == true);
        if (t is null) { _s.Ui.Write($"No hay una tarea llamada '{name}'.", Level.Error); return 1; }
        var cmd = Prompts.Expand(t.Command ?? "", p => _s.Ui.Ask(p));
        if (cmd is null) { _s.Ui.Write("Cancelado.", Level.Muted); return 1; }
        return _s.Executor.Here(cmd, Paths.Resolve(_s.Root, t.Folder));
    }
}
