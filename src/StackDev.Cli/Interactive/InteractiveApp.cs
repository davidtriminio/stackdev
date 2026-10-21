using StackDev.Core.Monitoring;
using Spectre.Console;
using StackDev.Cli.Commands;
using StackDev.Cli.Views;
using StackDev.Cli.Ui;
using StackDev.Core;
using StackDev.Core.Android;
using StackDev.Core.Config;
using StackDev.Core.Orchestration;
using StackDev.Core.State;
using StackDev.Core.Ui;

namespace StackDev.Cli.Interactive;

/// <summary>
/// El modo interactivo: panel de estado en vivo; con un código + Enter (a1, w1, d5…) o con el menú de flechas se ejecuta
/// cualquier acción. Ctrl+C cancela solo la acción en curso.
/// </summary>
public sealed class InteractiveApp
{
    // Acciones que dejan algo que leer: se espera Enter antes de volver al panel.
    private static readonly HashSet<string> AlwaysPause = new()
    {
        "test", "build", "analyze", "format", "gen", "install", "pub-get", "clean", "backup", "restore", "recreate", "check",
    };

    private Session _s;
    private StatusMonitor _monitor;

    public InteractiveApp(Session s)
    {
        _s = s;
        _monitor = new StatusMonitor(s);
    }

    public int Run()
    {
        Cancellation.Hook();
        string? warning = null;
        try
        {
            while (true)
            {
                if (_s.ReloadIfChanged()) warning = "dev-menu.json recargado.";
                Console.Title = $"stackdev — {_s.Ctx.Name}";
                AnsiConsole.Clear();

                var input = WaitForInput(warning);
                warning = null;

                bool exit;
                if (input is null) exit = Menu();
                else exit = Dispatch(input, out warning);
                if (exit) return 0;
            }
        }
        finally { _monitor.Dispose(); }
    }

    // ------------------------------------------------------------ Panel en vivo

    /// <summary>Muestra el panel hasta que escriben un código + Enter (devuelve el texto) o piden el menú (devuelve null).</summary>
    private string? WaitForInput(string? warning)
    {
        var code = "";
        string? result = null;
        _monitor.Resume();

        AnsiConsole.Live(Board.Build(_s, _monitor.Last, code, warning))
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .Start(ctx =>
            {
                Snapshot? shown = _monitor.Last;
                var change = false;
                var lastCheck = DateTime.UtcNow;
                while (true)
                {
                    if (Console.KeyAvailable)
                    {
                        var k = Console.ReadKey(true);
                        switch (k.Key)
                        {
                            case ConsoleKey.Enter:
                                if (code.Length == 0) { result = null; return; }
                                result = code;
                                return;
                            case ConsoleKey.DownArrow:
                            case ConsoleKey.UpArrow:
                                if (code.Length == 0) { result = null; return; }
                                break;
                            case ConsoleKey.Escape:
                                code = "";
                                break;
                            case ConsoleKey.Backspace:
                                if (code.Length > 0) code = code[..^1];
                                break;
                            default:
                                if (!char.IsControl(k.KeyChar)) code += k.KeyChar;
                                break;
                        }
                        warning = null;
                        change = true;
                    }
                    else
                    {
                        Thread.Sleep(40);
                    }

                    if (!ReferenceEquals(shown, _monitor.Last)) { shown = _monitor.Last; change = true; }

                    if ((DateTime.UtcNow - lastCheck).TotalSeconds >= 1)
                    {
                        lastCheck = DateTime.UtcNow;
                        if (_s.ReloadIfChanged()) { warning = "dev-menu.json recargado."; _monitor.Refresh(); change = true; }
                    }

                    if (change)
                    {
                        ctx.UpdateTarget(Board.Build(_s, shown, code, warning));
                        ctx.Refresh();
                        change = false;
                    }
                }
            });

        return result;
    }

    // ------------------------------------------------------------ Códigos y menú

    private bool Dispatch(string text, out string? warning)
    {
        warning = null;
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.ToLowerInvariant();
            switch (t)
            {
                case "q": if (Exit()) return true; continue;
                case "?": HelpCodes(); continue;
                case "m": if (Menu()) return true; continue;
                case "c": ConditionsMenu(); continue;
                case "k": ConsolePage(); continue;
                case "g": MenuConfig(); continue;
                case "p": ChangeProject(); continue;
            }
            var action = Operations.ByCode(t);
            if (action is null || (action.Requires is { } r && !_s.Ctx.Has(r)))
            {
                warning = $"Código desconocido o no disponible aquí: {token}  (? = lista de códigos)";
                continue;
            }
            RunAction(action);
        }
        return false;
    }

    /// <summary>El menú de flechas, agrupado por sección. Devuelve true si hay que salir de la herramienta.</summary>
    private bool Menu()
    {
        var titles = new Dictionary<string, string>();
        var prompt = new SelectionPrompt<string>()
            .Title("[bold]¿Qué hacemos?[/]")
            .PageSize(22)
            .MoreChoicesText("[grey](↑↓ para ver más)[/]")
            .Mode(SelectionMode.Leaf);

        foreach (var group in Operations.Available(_s).GroupBy(a => a.Section))
        {
            foreach (var a in group) titles[a.Code] = $"{a.Code,-3} {a.Title}";
            prompt.AddChoiceGroup(group.Key, group.Select(a => a.Code));
        }

        var other = new List<string>();
        void Other(string code, string text) { titles[code] = $"{code,-3} {text}"; other.Add(code); }
        if (_s.Ctx.Has(ProjectComponent.Mobile)) Other("c", "Condiciones del dispositivo (red, batería, tema…)");
        Other("k", "Consola de comandos");
        Other("g", "Configuración (dev-menu.json)");
        Other("p", "Cambiar de proyecto");
        Other("?", "Lista de códigos");
        Other("q", "Salir");
        prompt.AddChoiceGroup("OTROS", other);
        prompt.UseConverter(v => titles.TryGetValue(v, out var t) ? Markup.Escape(t) : $"[bold]{Markup.Escape(v)}[/]");

        AnsiConsole.Clear();
        var chosen = AnsiConsole.Prompt(prompt);
        return Dispatch(chosen, out _);
    }

    private void HelpCodes()
    {
        AnsiConsole.Clear();
        foreach (var group in Operations.Available(_s).GroupBy(a => a.Section))
        {
            AnsiConsole.Write(new Rule($"[bold]{group.Key}[/]").LeftJustified().RuleStyle("grey"));
            foreach (var a in group) AnsiConsole.MarkupLineInterpolated($"  [cyan]{a.Code,-3}[/] {a.Title}");
        }
        AnsiConsole.Write(new Rule("[bold]OTROS[/]").LeftJustified().RuleStyle("grey"));
        if (_s.Ctx.Has(ProjectComponent.Mobile)) AnsiConsole.MarkupLine("  [cyan]c  [/] Condiciones del dispositivo");
        AnsiConsole.MarkupLine("  [cyan]k  [/] Consola de comandos\n  [cyan]g  [/] Configuración\n  [cyan]p  [/] Cambiar de proyecto\n  [cyan]m  [/] Menú de flechas\n  [cyan]q  [/] Salir");
        AnsiConsole.MarkupLine("\n[grey]Puedes escribir varios códigos separados por espacio (p. ej. [/]d1 a1 w1[grey]).[/]");
        Pause();
    }

    // ------------------------------------------------------------ Ejecutar

    private void RunAction(Operation a)
    {
        _monitor.Pause();
        AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(a.Title)}[/] [grey]({a.Code})[/]").LeftJustified().RuleStyle("cyan"));
        var ok = false;
        var cancelled = false;
        try { ok = Cancellation.Run(_s, () => a.Run(_s), out cancelled); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Error inesperado:[/] {ex.Message}");
        }
        if (cancelled) AnsiConsole.MarkupLine("[yellow]Cancelado.[/]");
        else if (!ok) AnsiConsole.MarkupLine("[red]No terminó bien.[/]");

        if (cancelled || !ok || AlwaysPause.Contains(a.Name)) Pause();
        _monitor.Resume();
    }

    private static void Pause()
    {
        AnsiConsole.MarkupLine("\n[grey]Enter para volver al panel…[/]");
        try { while (Console.ReadKey(true).Key != ConsoleKey.Enter) { } }
        catch (InvalidOperationException) { }
    }

    private bool Exit()
    {
        _monitor.Pause();
        Snapshot snap;
        try { snap = AnsiConsole.Status().Start("Revisando qué sigue corriendo…", _ => Snapshot.Take(_s)); }
        finally { _monitor.Resume(); }
        if (!snap.AnythingRunning) return true;

        var i = _s.Ui.Choose("Hay cosas corriendo. ¿Qué hago?",
            new[] { "Detener todo y salir", "Salir y dejar todo corriendo" });
        switch (i)
        {
            case 0:
                _monitor.Pause();
                Cancellation.Run(_s, () => _s.All.Stop(), out _);
                return true;
            case 1:
                return true;
            default:
                return false;
        }
    }

    // ------------------------------------------------------------ Submenús

    private void ConditionsMenu()
    {
        if (!_s.Ctx.Has(ProjectComponent.Mobile)) { _s.Ui.Write("Este proyecto no tiene móvil.", Level.Warning); Pause(); return; }
        AnsiConsole.Clear();
        var i = _s.Ui.Choose($"Condiciones del dispositivo — ahora: {_s.Conditions.Text()}",
            new[] { "Elegir condiciones (varias a la vez)…", "Restablecer todo a normal", "Escribir números (p. ej. 3 13 17, 0 = restablecer)" });
        if (i < 0) return;

        _monitor.Pause();
        try
        {
            switch (i)
            {
                case 0:
                {
                    var prompt = new MultiSelectionPrompt<string>()
                        .Title("Marca con [bold]espacio[/] y confirma con [bold]Enter[/]")
                        .PageSize(20).Mode(SelectionMode.Leaf)
                        .InstructionsText("[grey](↑↓ moverse · espacio marcar · Enter aplicar)[/]");
                    var names = new Dictionary<string, string>();
                    foreach (var g in ConditionsService.Presets.GroupBy(p => p.Cat))
                    {
                        foreach (var p in g) names[p.N.ToString()] = $"{p.N,2}  {p.Txt}";
                        prompt.AddChoiceGroup(g.Key, g.Select(p => p.N.ToString()));
                    }
                    prompt.UseConverter(v => names.TryGetValue(v, out var t) ? Markup.Escape(t) : $"[bold]{Markup.Escape(v)}[/]");
                    var marked = AnsiConsole.Prompt(prompt);
                    if (marked.Count > 0) Cancellation.Run(_s, () => _s.Conditions.ApplyNumbers(marked), out _);
                    break;
                }
                case 1:
                    Cancellation.Run(_s, () => _s.Conditions.Reset(), out _);
                    break;
                case 2:
                {
                    var t = _s.Ui.Ask("Números");
                    if (t is not null)
                        Cancellation.Run(_s, () => _s.Conditions.ApplyNumbers(t.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)), out _);
                    break;
                }
            }
        }
        finally { _monitor.Resume(); }
        Pause();
    }

    private void ConsolePage()
    {
        var cc = new CommandConsole(_s);
        _monitor.Pause();
        try
        {
            while (true)
            {
                AnsiConsole.Clear();
                AnsiConsole.Write(new Rule("[bold]Consola de comandos[/]").LeftJustified().RuleStyle("cyan"));
                var items = cc.Items();
                for (var n = 0; n < items.Count; n++)
                    AnsiConsole.MarkupLineInterpolated($"  [cyan]{n + 1,2}[/] {(items[n].Task ? "★" : " ")} {items[n].Title}");
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(cc.Help())}[/]\n");

                var line = AnsiConsole.Prompt(new TextPrompt<string>(Markup.Escape(cc.Prompt)).AllowEmpty());
                var follow = true;
                try { Cancellation.Run(_s, () => { follow = cc.HandleInput(line); return true; }, out _); }
                catch (Exception ex) { AnsiConsole.MarkupLineInterpolated($"[red]Error:[/] {ex.Message}"); }
                if (!follow) return;
                if (!string.IsNullOrWhiteSpace(line)) Pause();
            }
        }
        finally { _monitor.Resume(); }
    }

    private void MenuConfig()
    {
        AnsiConsole.Clear();
        var i = _s.Ui.Choose($"Configuración — {_s.Store.FilePath}",
            new[] { "Editar dev-menu.json", "Ver cómo se lee (componentes, carpetas, puertos)", "Detectar de nuevo (guarda copia .bak)", "Traer el estado de flutter-dev otra vez" });
        switch (i)
        {
            case 0: _s.OpenConfigEditor(); Pause(); break;
            case 1: ConfigSummary(); Pause(); break;
            case 2:
                if (_s.Ui.Confirm("Esto reemplaza dev-menu.json (queda una copia .bak). ¿Seguir?", false))
                {
                    _monitor.Pause();
                    try { _s.Redetect(); } finally { _monitor.Resume(); }
                    Pause();
                }
                break;
            case 3:
            {
                var r = _s.State.Migrate(StateStore.DirPrevious(), force: true);
                _s.Ui.Write($"{r.Copied} archivos copiados, {r.Skipped} sin cambios.", Level.Ok);
                Pause();
                break;
            }
        }
    }

    private void ConfigSummary()
    {
        var c = _s.Ctx;
        var t = new Table().Border(TableBorder.Rounded).AddColumns("Componente", "Carpeta", "URL / puerto");
        if (c.Has(ProjectComponent.Postgres))
            t.AddRow("Postgres", "(Docker)", Markup.Escape($"{c.Config.Postgres!.EfContainer} · localhost:{c.Config.Postgres.EfPort} · {c.Config.Postgres.EfImage}"));
        foreach (var comp in new[] { ProjectComponent.Api, ProjectComponent.Web, ProjectComponent.Mobile })
            if (c.Has(comp))
                t.AddRow(Session.Names[comp], Markup.Escape(_s.Rel(c.Folder(comp))), Markup.Escape(c.Url(comp) ?? "—"));
        AnsiConsole.Write(t);
        if (c.Tasks.Count > 0) AnsiConsole.MarkupLineInterpolated($"Tareas: {string.Join(", ", c.Tasks.Select(x => x.Name))}");
        AnsiConsole.MarkupLineInterpolated($"Estado personal: {_s.State.Dir}");
        AnsiConsole.MarkupLineInterpolated($"Logs: {Path.GetDirectoryName(_s.Services.LogPath("api"))}");
        if (_s.Sdk is not null) AnsiConsole.MarkupLineInterpolated($"Android SDK: {_s.Sdk}");
    }

    private void ChangeProject()
    {
        AnsiConsole.Clear();
        var path = ProjectSelector.Choose(_s.State.LastProject);
        if (path is null) return;
        path = Project.Confirm(path, _s.State);
        if (string.Equals(Path.GetFullPath(path), _s.Root, StringComparison.OrdinalIgnoreCase)) return;

        _monitor.Dispose();
        var fresh = new Session(path, _s.State, _s.Ui, new SessionOptions { ArgsExtra = _s.Op.ArgsExtra, Mode = _s.Op.Mode, SameWindow = _s.Op.SameWindow, NoCenter = _s.Op.NoCenter });
        fresh.Start();
        _s = fresh;
        _monitor = new StatusMonitor(_s);
    }
}
