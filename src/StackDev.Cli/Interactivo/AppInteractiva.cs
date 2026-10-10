using StackDev.Core.Monitoreo;
using Spectre.Console;
using StackDev.Cli.Comandos;
using StackDev.Cli.Vista;
using StackDev.Cli.Ui;
using StackDev.Core;
using StackDev.Core.Android;
using StackDev.Core.Config;
using StackDev.Core.Orquestacion;
using StackDev.Core.State;
using StackDev.Core.Ui;

namespace StackDev.Cli.Interactivo;

/// <summary>
/// El modo interactivo: panel de estado en vivo; con un código + Enter (a1, w1, d5…) o con el menú de flechas se ejecuta
/// cualquier acción. Ctrl+C cancela solo la acción en curso.
/// </summary>
public sealed class AppInteractiva
{
    // Acciones que dejan algo que leer: se espera Enter antes de volver al panel.
    private static readonly HashSet<string> PausarSiempre = new()
    {
        "test", "build", "analyze", "format", "gen", "install", "pub-get", "clean", "backup", "restore", "recreate", "check",
    };

    private Sesion _s;
    private MonitorEstado _monitor;

    public AppInteractiva(Sesion s)
    {
        _s = s;
        _monitor = new MonitorEstado(s);
    }

    public int Correr()
    {
        Cancelacion.Enganchar();
        string? aviso = null;
        try
        {
            while (true)
            {
                if (_s.RecargarSiCambio()) aviso = "dev-menu.json recargado.";
                Console.Title = $"stackdev — {_s.Ctx.Nombre}";
                AnsiConsole.Clear();

                var entrada = EsperarEntrada(aviso);
                aviso = null;

                bool salir;
                if (entrada is null) salir = Menu();
                else salir = Despachar(entrada, out aviso);
                if (salir) return 0;
            }
        }
        finally { _monitor.Dispose(); }
    }

    // ------------------------------------------------------------ Panel en vivo

    /// <summary>Muestra el panel hasta que escriben un código + Enter (devuelve el texto) o piden el menú (devuelve null).</summary>
    private string? EsperarEntrada(string? aviso)
    {
        var codigo = "";
        string? resultado = null;
        _monitor.Reanudar();

        AnsiConsole.Live(Tablero.Construir(_s, _monitor.Ultima, codigo, aviso))
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .Start(ctx =>
            {
                Instantanea? mostrada = _monitor.Ultima;
                var cambio = false;
                var ultimoChequeo = DateTime.UtcNow;
                while (true)
                {
                    if (Console.KeyAvailable)
                    {
                        var k = Console.ReadKey(true);
                        switch (k.Key)
                        {
                            case ConsoleKey.Enter:
                                if (codigo.Length == 0) { resultado = null; return; }
                                resultado = codigo;
                                return;
                            case ConsoleKey.DownArrow:
                            case ConsoleKey.UpArrow:
                                if (codigo.Length == 0) { resultado = null; return; }
                                break;
                            case ConsoleKey.Escape:
                                codigo = "";
                                break;
                            case ConsoleKey.Backspace:
                                if (codigo.Length > 0) codigo = codigo[..^1];
                                break;
                            default:
                                if (!char.IsControl(k.KeyChar)) codigo += k.KeyChar;
                                break;
                        }
                        aviso = null;
                        cambio = true;
                    }
                    else
                    {
                        Thread.Sleep(40);
                    }

                    if (!ReferenceEquals(mostrada, _monitor.Ultima)) { mostrada = _monitor.Ultima; cambio = true; }

                    if ((DateTime.UtcNow - ultimoChequeo).TotalSeconds >= 1)
                    {
                        ultimoChequeo = DateTime.UtcNow;
                        if (_s.RecargarSiCambio()) { aviso = "dev-menu.json recargado."; _monitor.Refrescar(); cambio = true; }
                    }

                    if (cambio)
                    {
                        ctx.UpdateTarget(Tablero.Construir(_s, mostrada, codigo, aviso));
                        ctx.Refresh();
                        cambio = false;
                    }
                }
            });

        return resultado;
    }

    // ------------------------------------------------------------ Códigos y menú

    private bool Despachar(string texto, out string? aviso)
    {
        aviso = null;
        foreach (var token in texto.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.ToLowerInvariant();
            switch (t)
            {
                case "q": if (Salir()) return true; continue;
                case "?": AyudaCodigos(); continue;
                case "m": if (Menu()) return true; continue;
                case "c": MenuCondiciones(); continue;
                case "k": Consola(); continue;
                case "g": MenuConfig(); continue;
                case "p": CambiarProyecto(); continue;
            }
            var accion = Acciones.PorCodigo(t);
            if (accion is null || (accion.Requiere is { } r && !_s.Ctx.Tiene(r)))
            {
                aviso = $"Código desconocido o no disponible aquí: {token}  (? = lista de códigos)";
                continue;
            }
            EjecutarAccion(accion);
        }
        return false;
    }

    /// <summary>El menú de flechas, agrupado por sección. Devuelve true si hay que salir de la herramienta.</summary>
    private bool Menu()
    {
        var titulos = new Dictionary<string, string>();
        var prompt = new SelectionPrompt<string>()
            .Title("[bold]¿Qué hacemos?[/]")
            .PageSize(22)
            .MoreChoicesText("[grey](↑↓ para ver más)[/]")
            .Mode(SelectionMode.Leaf);

        foreach (var grupo in Acciones.Disponibles(_s).GroupBy(a => a.Seccion))
        {
            foreach (var a in grupo) titulos[a.Codigo] = $"{a.Codigo,-3} {a.Titulo}";
            prompt.AddChoiceGroup(grupo.Key, grupo.Select(a => a.Codigo));
        }

        var otros = new List<string>();
        void Otro(string cod, string texto) { titulos[cod] = $"{cod,-3} {texto}"; otros.Add(cod); }
        if (_s.Ctx.Tiene(Componente.Movil)) Otro("c", "Condiciones del dispositivo (red, batería, tema…)");
        Otro("k", "Consola de comandos");
        Otro("g", "Configuración (dev-menu.json)");
        Otro("p", "Cambiar de proyecto");
        Otro("?", "Lista de códigos");
        Otro("q", "Salir");
        prompt.AddChoiceGroup("OTROS", otros);
        prompt.UseConverter(v => titulos.TryGetValue(v, out var t) ? Markup.Escape(t) : $"[bold]{Markup.Escape(v)}[/]");

        AnsiConsole.Clear();
        var elegido = AnsiConsole.Prompt(prompt);
        return Despachar(elegido, out _);
    }

    private void AyudaCodigos()
    {
        AnsiConsole.Clear();
        foreach (var grupo in Acciones.Disponibles(_s).GroupBy(a => a.Seccion))
        {
            AnsiConsole.Write(new Rule($"[bold]{grupo.Key}[/]").LeftJustified().RuleStyle("grey"));
            foreach (var a in grupo) AnsiConsole.MarkupLineInterpolated($"  [cyan]{a.Codigo,-3}[/] {a.Titulo}");
        }
        AnsiConsole.Write(new Rule("[bold]OTROS[/]").LeftJustified().RuleStyle("grey"));
        if (_s.Ctx.Tiene(Componente.Movil)) AnsiConsole.MarkupLine("  [cyan]c  [/] Condiciones del dispositivo");
        AnsiConsole.MarkupLine("  [cyan]k  [/] Consola de comandos\n  [cyan]g  [/] Configuración\n  [cyan]p  [/] Cambiar de proyecto\n  [cyan]m  [/] Menú de flechas\n  [cyan]q  [/] Salir");
        AnsiConsole.MarkupLine("\n[grey]Puedes escribir varios códigos separados por espacio (p. ej. [/]d1 a1 w1[grey]).[/]");
        Pausa();
    }

    // ------------------------------------------------------------ Ejecutar

    private void EjecutarAccion(Accion a)
    {
        _monitor.Pausar();
        AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(a.Titulo)}[/] [grey]({a.Codigo})[/]").LeftJustified().RuleStyle("cyan"));
        var ok = false;
        var cancelado = false;
        try { ok = Cancelacion.Correr(_s, () => a.Ejecutar(_s), out cancelado); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Error inesperado:[/] {ex.Message}");
        }
        if (cancelado) AnsiConsole.MarkupLine("[yellow]Cancelado.[/]");
        else if (!ok) AnsiConsole.MarkupLine("[red]No terminó bien.[/]");

        if (cancelado || !ok || PausarSiempre.Contains(a.Nombre)) Pausa();
        _monitor.Reanudar();
    }

    private static void Pausa()
    {
        AnsiConsole.MarkupLine("\n[grey]Enter para volver al panel…[/]");
        try { while (Console.ReadKey(true).Key != ConsoleKey.Enter) { } }
        catch (InvalidOperationException) { }
    }

    private bool Salir()
    {
        _monitor.Pausar();
        Instantanea snap;
        try { snap = AnsiConsole.Status().Start("Revisando qué sigue corriendo…", _ => Instantanea.Tomar(_s)); }
        finally { _monitor.Reanudar(); }
        if (!snap.HayAlgoCorriendo) return true;

        var i = _s.Ui.Elegir("Hay cosas corriendo. ¿Qué hago?",
            new[] { "Detener todo y salir", "Salir y dejar todo corriendo" });
        switch (i)
        {
            case 0:
                _monitor.Pausar();
                Cancelacion.Correr(_s, () => _s.Todo.Detener(), out _);
                return true;
            case 1:
                return true;
            default:
                return false;
        }
    }

    // ------------------------------------------------------------ Submenús

    private void MenuCondiciones()
    {
        if (!_s.Ctx.Tiene(Componente.Movil)) { _s.Ui.Escribir("Este proyecto no tiene móvil.", Nivel.Aviso); Pausa(); return; }
        AnsiConsole.Clear();
        var i = _s.Ui.Elegir($"Condiciones del dispositivo — ahora: {_s.Condiciones.Texto()}",
            new[] { "Elegir condiciones (varias a la vez)…", "Restablecer todo a normal", "Escribir números (p. ej. 3 13 17, 0 = restablecer)" });
        if (i < 0) return;

        _monitor.Pausar();
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
                    var nombres = new Dictionary<string, string>();
                    foreach (var g in CondicionesService.Presets.GroupBy(p => p.Cat))
                    {
                        foreach (var p in g) nombres[p.N.ToString()] = $"{p.N,2}  {p.Txt}";
                        prompt.AddChoiceGroup(g.Key, g.Select(p => p.N.ToString()));
                    }
                    prompt.UseConverter(v => nombres.TryGetValue(v, out var t) ? Markup.Escape(t) : $"[bold]{Markup.Escape(v)}[/]");
                    var marcadas = AnsiConsole.Prompt(prompt);
                    if (marcadas.Count > 0) Cancelacion.Correr(_s, () => _s.Condiciones.AplicarNumeros(marcadas), out _);
                    break;
                }
                case 1:
                    Cancelacion.Correr(_s, () => _s.Condiciones.Restablecer(), out _);
                    break;
                case 2:
                {
                    var t = _s.Ui.Preguntar("Números");
                    if (t is not null)
                        Cancelacion.Correr(_s, () => _s.Condiciones.AplicarNumeros(t.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)), out _);
                    break;
                }
            }
        }
        finally { _monitor.Reanudar(); }
        Pausa();
    }

    private void Consola()
    {
        var cc = new ConsolaComandos(_s);
        _monitor.Pausar();
        try
        {
            while (true)
            {
                AnsiConsole.Clear();
                AnsiConsole.Write(new Rule("[bold]Consola de comandos[/]").LeftJustified().RuleStyle("cyan"));
                var items = cc.Items();
                for (var n = 0; n < items.Count; n++)
                    AnsiConsole.MarkupLineInterpolated($"  [cyan]{n + 1,2}[/] {(items[n].Tarea ? "★" : " ")} {items[n].Titulo}");
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(cc.Ayuda())}[/]\n");

                var linea = AnsiConsole.Prompt(new TextPrompt<string>(Markup.Escape(cc.Prompt)).AllowEmpty());
                var seguir = true;
                try { Cancelacion.Correr(_s, () => { seguir = cc.Procesar(linea); return true; }, out _); }
                catch (Exception ex) { AnsiConsole.MarkupLineInterpolated($"[red]Error:[/] {ex.Message}"); }
                if (!seguir) return;
                if (!string.IsNullOrWhiteSpace(linea)) Pausa();
            }
        }
        finally { _monitor.Reanudar(); }
    }

    private void MenuConfig()
    {
        AnsiConsole.Clear();
        var i = _s.Ui.Elegir($"Configuración — {_s.Store.Ruta}",
            new[] { "Editar dev-menu.json", "Ver cómo se lee (componentes, carpetas, puertos)", "Detectar de nuevo (guarda copia .bak)", "Traer el estado de previous-state otra vez" });
        switch (i)
        {
            case 0: _s.AbrirEditorConfig(); Pausa(); break;
            case 1: ResumenConfig(); Pausa(); break;
            case 2:
                if (_s.Ui.Confirmar("Esto reemplaza dev-menu.json (queda una copia .bak). ¿Seguir?", false))
                {
                    _monitor.Pausar();
                    try { _s.Redetectar(); } finally { _monitor.Reanudar(); }
                    Pausa();
                }
                break;
            case 3:
            {
                var r = _s.Estado.Migrar(StateStore.DirAnterior(), forzar: true);
                _s.Ui.Escribir($"{r.Copiados} archivos copiados, {r.Omitidos} sin cambios.", Nivel.Ok);
                Pausa();
                break;
            }
        }
    }

    private void ResumenConfig()
    {
        var c = _s.Ctx;
        var t = new Table().Border(TableBorder.Rounded).AddColumns("Componente", "Carpeta", "URL / puerto");
        if (c.Tiene(Componente.Postgres))
            t.AddRow("Postgres", "(Docker)", Markup.Escape($"{c.Config.Postgres!.ContenedorEf} · localhost:{c.Config.Postgres.PuertoEf} · {c.Config.Postgres.ImagenEf}"));
        foreach (var comp in new[] { Componente.Api, Componente.Web, Componente.Movil })
            if (c.Tiene(comp))
                t.AddRow(Sesion.Nombres[comp], Markup.Escape(_s.Rel(c.Carpeta(comp))), Markup.Escape(c.Url(comp) ?? "—"));
        AnsiConsole.Write(t);
        if (c.Tareas.Count > 0) AnsiConsole.MarkupLineInterpolated($"Tareas: {string.Join(", ", c.Tareas.Select(x => x.Nombre))}");
        AnsiConsole.MarkupLineInterpolated($"Estado personal: {_s.Estado.Dir}");
        AnsiConsole.MarkupLineInterpolated($"Logs: {Path.GetDirectoryName(_s.Servicios.RutaLog("api"))}");
        if (_s.Sdk is not null) AnsiConsole.MarkupLineInterpolated($"Android SDK: {_s.Sdk}");
    }

    private void CambiarProyecto()
    {
        AnsiConsole.Clear();
        var ruta = SelectorProyecto.Elegir(_s.Estado.UltimoProyecto);
        if (ruta is null) return;
        ruta = Proyecto.Confirmar(ruta, _s.Estado);
        if (string.Equals(Path.GetFullPath(ruta), _s.Raiz, StringComparison.OrdinalIgnoreCase)) return;

        _monitor.Dispose();
        var nueva = new Sesion(ruta, _s.Estado, _s.Ui, new OpcionesSesion { ArgsExtra = _s.Op.ArgsExtra, Modo = _s.Op.Modo, MismaVentana = _s.Op.MismaVentana, NoCentrar = _s.Op.NoCentrar });
        nueva.Iniciar();
        _s = nueva;
        _monitor = new MonitorEstado(_s);
    }
}
