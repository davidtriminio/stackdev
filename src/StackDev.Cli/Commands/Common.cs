using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using StackDev.Cli.Ui;
using StackDev.Core;
using StackDev.Core.Detection;
using StackDev.Core.State;
using StackDev.Core.Ui;

namespace StackDev.Cli.Commands;

/// <summary>For detect and config: the project can be given as the first argument.</summary>
public class PathSettings : CommandSettings
{
    [CommandArgument(0, "[ruta]")]
    [Description("Raíz del proyecto. Sin ruta: la carpeta actual si parece un proyecto, o el último proyecto usado.")]
    public string? FilePath { get; init; }
}

/// <summary>Options understood by every command that works on a project.</summary>
public class CommonSettings : CommandSettings
{
    [CommandOption("-p|--proyecto <RUTA>")]
    [Description("Raíz del proyecto (por defecto: la carpeta actual o el último proyecto usado).")]
    public string? Project { get; init; }

    [CommandOption("--avd <NOMBRE>")]
    [Description("Emulador a usar (por defecto el último usado en este proyecto).")]
    public string? Avd { get; init; }

    [CommandOption("--modo <MODO>")]
    [Description("Modo de flutter run: debug, profile o release.")]
    [DefaultValue("debug")]
    public string Mode { get; init; } = "debug";

    [CommandOption("--args-extra <ARGS>")]
    [Description("Argumentos extra para flutter run (p. ej. \"--flavor dev\").")]
    public string? ArgsExtra { get; init; }

    [CommandOption("--misma-ventana")]
    [Description("flutter run en esta misma consola, sin ventana propia.")]
    public bool SameWindow { get; init; }

    [CommandOption("--no-centrar")]
    [Description("No mover la ventana del emulador.")]
    public bool NoCenter { get; init; }

    [CommandOption("-y|--si")]
    [Description("Contesta «sí» a todas las preguntas (para scripts).")]
    public bool Yes { get; init; }

    [CommandOption("-d|--dispositivo <SERIAL>")]
    [Description("Dispositivo donde correr la app: un serial de adb (emulator-5554, 192.168.1.20:5555…) o «todos».")]
    public string? Device { get; init; }

    [CommandOption("--direccion <IP_PUERTO>")]
    [Description("Con emu connect, pair o disconnect: dirección del teléfono (sin ella se pregunta o se usan las guardadas).")]
    public string? Address { get; init; }

    [CommandOption("--codigo-emparejar <CODIGO>")]
    [Description("Con emu pair: el código de 6 dígitos que muestra el teléfono.")]
    public string? PairingCode { get; init; }

    public SessionOptions AsOptions() => new()
    {
        Device = Device,
        Address = Address,
        PairingCode = PairingCode,
        Avd = Avd,
        ArgsExtra = ArgsExtra ?? "",
        Mode = Mode,
        SameWindow = SameWindow,
        NoCenter = NoCenter,
    };
}

public static class Project
{
    /// <summary>Decides the project root: argument → current folder → last used (asks) → ask for the path.</summary>
    public static string? Choose(string? parameter, StateStore state)
    {
        var r = ProjectRoot.Resolve(parameter, Directory.GetCurrentDirectory(), state.LastProject);

        if (r.ParameterInvalid)
            AnsiConsole.MarkupLineInterpolated($"[yellow]'{parameter}' no existe.[/]");

        string? path = r.Origin switch
        {
            ProjectOrigin.Parameter or ProjectOrigin.CurrentFolder => r.FilePath,
            ProjectOrigin.LastUsed =>
                AnsiConsole.Confirm($"Último proyecto: {Markup.Escape(r.FilePath!)}. ¿Usarlo?") ? r.FilePath : Interactive.ProjectSelector.Choose(state.LastProject),
            _ => Interactive.ProjectSelector.Choose(state.LastProject),
        };
        if (path is null) return null;
        return Confirm(path, state);
    }

    /// <summary>If a subfolder was chosen (e.g. mobile/) and the parent has .git or dev-menu.json, offers the root.</summary>
    public static string Confirm(string path, StateStore state)
    {
        path = Path.GetFullPath(path);
        var parent = ProjectRoot.SuggestParent(path);
        if (parent is not null)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]'{Path.GetFileName(path)}' parece ser parte del proyecto '{parent}'.[/]");
            if (AnsiConsole.Confirm("¿Usar la carpeta raíz del proyecto?")) path = parent;
        }
        state.LastProject = path;
        return path;
    }

    /// <summary>For scripts: never asks. Argument → current folder → last used.</summary>
    public static string? ChooseWithoutAsking(string? parameter, StateStore state, IConsoleUi ui)
    {
        var r = ProjectRoot.Resolve(parameter, Directory.GetCurrentDirectory(), state.LastProject);
        switch (r.Origin)
        {
            case ProjectOrigin.LastUsed:
                ui.Write($"Usando el último proyecto: {r.FilePath}", Level.Muted);
                break;
            case ProjectOrigin.Selector:
                ui.Write(r.ParameterInvalid ? $"'{parameter}' no existe." : "No hay un proyecto aquí. Usa -p <ruta>.", Level.Error);
                return null;
        }
        state.LastProject = r.FilePath;
        return r.FilePath;
    }
}

/// <summary>Per-command cancellation: Ctrl+C cancels only what is running and gives control back.</summary>
public static class Cancellation
{
    private static CancellationTokenSource? _current;
    private static bool _hooked;

    public static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        Console.CancelKeyPress += (_, e) =>
        {
            var cts = _current;
            if (cts is not null && !cts.IsCancellationRequested)
            {
                e.Cancel = true;   // do not close the tool: only cancel the command in progress
                cts.Cancel();
            }
        };
    }

    /// <summary>Runs <paramref name="f"/> with a token that Ctrl+C cancels. Returns false if it failed or was cancelled.</summary>
    public static bool Run(Session s, Func<bool> f, out bool cancelled)
    {
        Hook();
        using var cts = new CancellationTokenSource();
        _current = cts;
        s.Ct = cts.Token;
        try { return f(); }
        finally
        {
            cancelled = cts.IsCancellationRequested;
            _current = null;
            s.Ct = CancellationToken.None;
        }
    }
}

public static class Startup
{
    /// <summary>Opens the project for a headless command (never asks more than needed).</summary>
    public static Session? OpenHeadless(CommonSettings st)
    {
        var state = StateStore.Open();
        var ui = new SimpleConsole(!Console.IsInputRedirected, st.Yes);
        var root = Project.ChooseWithoutAsking(st.Project, state, ui);
        if (root is null) return null;
        var s = new Session(root, state, ui, st.AsOptions());
        s.Start();
        return s;
    }

    /// <summary>Runs an action and returns the process exit code (0 ok, 1 failed, 130 cancelled).</summary>
    public static int Run(Session s, Func<bool> f)
    {
        var ok = Cancellation.Run(s, f, out var cancelled);
        if (cancelled) { s.Ui.Write("Cancelado.", Level.Warning); return 130; }
        return ok ? 0 : 1;
    }
}
