using StackDev.Core.Monitoring;
using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using StackDev.Cli.Views;
using StackDev.Core;
using StackDev.Core.Android;
using StackDev.Core.Config;
using StackDev.Core.Orchestration;
using StackDev.Core.Ui;
using StackDev.Core.Util;

namespace StackDev.Cli.Commands;

// Comandos sin interfaz: hacen una cosa, imprimen texto plano y devuelven un código de salida
// (0 bien, 1 falló, 2 uso incorrecto, 130 cancelado con Ctrl+C). Sirven para scripts y para la línea de comandos.

public class GroupSettings : CommonSettings
{
    [CommandArgument(0, "<accion>")]
    [Description("Qué hacer (si te equivocas, el mensaje de error lista las que hay).")]
    public string Operation { get; init; } = "";
}

public sealed class EmuSettings : GroupSettings
{
    [CommandOption("--cold")]
    [Description("Con «start»: arrancar desde cero, sin cargar el estado guardado (Quick Boot).")]
    public bool Cold { get; init; }
}

public sealed class DbSettings : GroupSettings
{
    [CommandOption("--archivo <RUTA>")]
    [Description("Con «restore»: el respaldo a restaurar (por defecto el más reciente).")]
    public string? FilePath { get; init; }
}

public abstract class GroupCommand<T> : Command<T> where T : GroupSettings
{
    protected abstract string Group { get; }

    protected virtual Func<Session, bool>? Special(T st, string action) => null;

    public override int Execute(CommandContext context, T st)
    {
        var s = Startup.OpenHeadless(st);
        if (s is null) return 1;

        var name = st.Operation.Trim().ToLowerInvariant();
        if (Group == "emu" && name == "start" && st is EmuSettings { Cold: true }) name = "cold";
        var action = Operations.ByName(Group, name);
        if (action is null)
        {
            s.Ui.Write($"'{st.Operation}' no es una acción de {Group}. Hay: {string.Join(", ", Operations.NamesOf(Group))}", Level.Error);
            return 2;
        }
        if (action.Requires is { } r && !s.Ctx.Has(r))
        {
            s.Ui.Write($"Este proyecto no tiene {Session.Names[r]} configurado.", Level.Error);
            return 1;
        }

        var f = Special(st, name) ?? action.Run;
        var code = Startup.Run(s, () => f(s));
        // Ver logs termina siempre con Ctrl+C: eso no es un error.
        return name == "logs" && code == 130 ? 0 : code;
    }
}

public sealed class ApiCommand : GroupCommand<GroupSettings> { protected override string Group => "api"; }
public sealed class WebCommand : GroupCommand<GroupSettings> { protected override string Group => "web"; }
public sealed class AppCommand : GroupCommand<GroupSettings> { protected override string Group => "app"; }
public sealed class EmuCommand : GroupCommand<EmuSettings> { protected override string Group => "emu"; }

public sealed class DbCommand : GroupCommand<DbSettings>
{
    protected override string Group => "db";

    protected override Func<Session, bool>? Special(DbSettings st, string action) =>
        action == "restore" && !string.IsNullOrWhiteSpace(st.FilePath) ? s => s.Postgres.Restore(st.FilePath) : null;
}

public sealed class StartAllSettings : CommonSettings { }

public sealed class UpCommand : Command<StartAllSettings>
{
    public override int Execute(CommandContext context, StartAllSettings st)
    {
        var s = Startup.OpenHeadless(st);
        return s is null ? 1 : Startup.Run(s, s.All.Start);
    }
}

public sealed class DownCommand : Command<StartAllSettings>
{
    public override int Execute(CommandContext context, StartAllSettings st)
    {
        var s = Startup.OpenHeadless(st);
        return s is null ? 1 : Startup.Run(s, s.All.Stop);
    }
}

public sealed class CheckCommand : Command<StartAllSettings>
{
    public override int Execute(CommandContext context, StartAllSettings st)
    {
        var s = Startup.OpenHeadless(st);
        if (s is null) return 1;
        List<StepResult> res = new();
        var code = Startup.Run(s, () =>
        {
            res = s.All.Verify();
            return res.All(r => r.Code == 0);
        });
        if (res.Count > 0)
        {
            var t = new Table().Border(TableBorder.Rounded).AddColumns("Paso", "Resultado", "Tiempo");
            foreach (var r in res)
                t.AddRow(Markup.Escape(r.Step), r.Code == 0 ? "[green]OK[/]" : $"[red]FALLÓ ({r.Code})[/]", $"{r.Seconds} s");
            AnsiConsole.Write(t);
        }
        return code;
    }
}

public sealed class RunSettings : CommonSettings
{
    [CommandArgument(0, "<comando>")]
    [Description("El comando a ejecutar (entre comillas).")]
    public string Command { get; init; } = "";

    [CommandOption("--in <DONDE>")]
    [Description("Dónde correrlo: api, web, movil, raiz o una carpeta (relativa a la raíz). Por defecto la raíz.")]
    public string? In { get; init; }
}

public sealed class RunCommand : Command<RunSettings>
{
    public override int Execute(CommandContext context, RunSettings st)
    {
        var s = Startup.OpenHeadless(st);
        if (s is null) return 1;

        string folder;
        switch ((st.In ?? "raiz").ToLowerInvariant())
        {
            case "raiz" or "root" or ".": folder = s.Root; break;
            case "api": folder = Comp(s, ProjectComponent.Api, out var ok1) ; if (!ok1) return 1; break;
            case "web": folder = Comp(s, ProjectComponent.Web, out var ok2); if (!ok2) return 1; break;
            case "movil" or "mobile": folder = Comp(s, ProjectComponent.Mobile, out var ok3); if (!ok3) return 1; break;
            default: folder = Paths.Resolve(s.Root, st.In); break;
        }
        if (!Directory.Exists(folder)) { s.Ui.Write($"No existe la carpeta: {folder}", Level.Error); return 1; }

        var cmd = Prompts.Expand(st.Command, t => s.Ui.Ask(t));
        if (cmd is null) { s.Ui.Write("Cancelado.", Level.Warning); return 1; }

        var code = 0;
        var output = Startup.Run(s, () => { code = s.Executor.Here(cmd, folder); return code == 0; });
        return output == 1 ? (code == 0 ? 1 : code) : output;
    }

    private static string Comp(Session s, ProjectComponent c, out bool ok)
    {
        ok = s.Ctx.Has(c);
        if (!ok) s.Ui.Write($"Este proyecto no tiene {Session.Names[c]} configurado.", Level.Error);
        return ok ? s.Ctx.Folder(c) : "";
    }
}

public sealed class TaskSettings : CommonSettings
{
    [CommandArgument(0, "[nombre]")]
    [Description("La tarea de dev-menu.json (o parte de su nombre). Sin nombre: lista las que hay.")]
    public string? Name { get; init; }
}

public sealed class TaskCommand : Command<TaskSettings>
{
    public override int Execute(CommandContext context, TaskSettings st)
    {
        var s = Startup.OpenHeadless(st);
        if (s is null) return 1;
        if (string.IsNullOrWhiteSpace(st.Name))
        {
            if (s.Ctx.Tasks.Count == 0) { s.Ui.Write("Este proyecto no tiene tareas en dev-menu.json.", Level.Warning); return 0; }
            foreach (var t in s.Ctx.Tasks)
                s.Ui.Write($"  {t.Name,-34} [{(string.IsNullOrEmpty(t.Folder) ? "." : t.Folder)}]  {t.Command}");
            return 0;
        }
        var code = 0;
        var output = Startup.Run(s, () => { code = new CommandConsole(s).RunTask(st.Name); return code == 0; });
        return output == 1 ? (code == 0 ? 1 : code) : output;
    }
}

public sealed class CondSettings : CommonSettings
{
    [CommandArgument(0, "[numeros]")]
    [Description("Números de las condiciones (3 13 17); 0 = restablecer todo. Sin números: lista las que hay.")]
    public string[] Numbers { get; init; } = Array.Empty<string>();
}

public sealed class CondCommand : Command<CondSettings>
{
    public override int Execute(CommandContext context, CondSettings st)
    {
        var s = Startup.OpenHeadless(st);
        if (s is null) return 1;
        if (!s.Ctx.Has(ProjectComponent.Mobile)) { s.Ui.Write("Este proyecto no tiene móvil.", Level.Error); return 1; }
        if (st.Numbers.Length == 0)
        {
            s.Ui.Write($"Activas: {s.Conditions.Text()}", Level.Info);
            s.Ui.Write("  0  Restablecer todo");
            foreach (var p in ConditionsService.Presets) s.Ui.Write($"  {p.N,2}  {ConditionsService.Title(p)}");
            return 0;
        }
        return Startup.Run(s, () => s.Conditions.ApplyNumbers(st.Numbers));
    }
}

public sealed class StatusCommand : Command<CommonSettings>
{
    public override int Execute(CommandContext context, CommonSettings st)
    {
        var s = Startup.OpenHeadless(st);
        if (s is null) return 1;
        var photo = Snapshot.Take(s);
        AnsiConsole.Write(Board.Build(s, photo, "", withFooter: false));
        return 0;
    }
}

public sealed class LogsSettings : CommonSettings
{
    [CommandArgument(0, "<cual>")]
    [Description("api, web o db.")]
    public string Which { get; init; } = "";

    [CommandOption("-n|--lineas <N>")]
    [Description("Cuántas líneas mostrar.")]
    [DefaultValue(40)]
    public int Lines { get; init; } = 40;

    [CommandOption("-f|--seguir")]
    [Description("Seguir el log en vivo (Ctrl+C para salir).")]
    public bool Follow { get; init; }
}

public sealed class LogsCommand : Command<LogsSettings>
{
    public override int Execute(CommandContext context, LogsSettings st)
    {
        var s = Startup.OpenHeadless(st);
        if (s is null) return 1;
        var which = st.Which.ToLowerInvariant();
        if (which is "db" or "postgres")
        {
            foreach (var l in s.Postgres.LastLogLines(st.Lines)) Console.WriteLine(l);
            return 0;
        }
        if (which is not ("api" or "web")) { s.Ui.Write("Usa: api, web o db.", Level.Error); return 2; }
        if (!st.Follow)
        {
            foreach (var l in s.Services.LastLines(which, st.Lines)) Console.WriteLine(l);
            return 0;
        }
        Startup.Run(s, () => { s.Services.Follow(which, st.Lines, s.Ct, l => Console.WriteLine(l)); return true; });
        return 0;
    }
}
