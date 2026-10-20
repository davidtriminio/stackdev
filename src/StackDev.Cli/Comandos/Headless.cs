using StackDev.Core.Monitoreo;
using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using StackDev.Cli.Vista;
using StackDev.Core;
using StackDev.Core.Android;
using StackDev.Core.Config;
using StackDev.Core.Orquestacion;
using StackDev.Core.Ui;
using StackDev.Core.Util;

namespace StackDev.Cli.Comandos;

// Comandos sin interfaz: hacen una cosa, imprimen texto plano y devuelven un código de salida
// (0 bien, 1 falló, 2 uso incorrecto, 130 cancelado con Ctrl+C). Sirven para scripts y para la línea de comandos.

public class GrupoSettings : ComandoSettings
{
    [CommandArgument(0, "<accion>")]
    [Description("Qué hacer (si te equivocas, el mensaje de error lista las que hay).")]
    public string Accion { get; init; } = "";
}

public sealed class EmuSettings : GrupoSettings
{
    [CommandOption("--cold")]
    [Description("Con «start»: arrancar desde cero, sin cargar el estado guardado (Quick Boot).")]
    public bool Cold { get; init; }
}

public sealed class DbSettings : GrupoSettings
{
    [CommandOption("--archivo <RUTA>")]
    [Description("Con «restore»: el respaldo a restaurar (por defecto el más reciente).")]
    public string? Archivo { get; init; }
}

public abstract class GrupoCommand<T> : Command<T> where T : GrupoSettings
{
    protected abstract string Grupo { get; }

    protected virtual Func<Sesion, bool>? Especial(T st, string accion) => null;

    public override int Execute(CommandContext context, T st)
    {
        var s = Arranque.AbrirSinInterfaz(st);
        if (s is null) return 1;

        var nombre = st.Accion.Trim().ToLowerInvariant();
        if (Grupo == "emu" && nombre == "start" && st is EmuSettings { Cold: true }) nombre = "cold";
        var accion = Acciones.PorNombre(Grupo, nombre);
        if (accion is null)
        {
            s.Ui.Escribir($"'{st.Accion}' no es una acción de {Grupo}. Hay: {string.Join(", ", Acciones.NombresDe(Grupo))}", Nivel.Error);
            return 2;
        }
        if (accion.Requiere is { } r && !s.Ctx.Tiene(r))
        {
            s.Ui.Escribir($"Este proyecto no tiene {Sesion.Nombres[r]} configurado.", Nivel.Error);
            return 1;
        }

        var f = Especial(st, nombre) ?? accion.Ejecutar;
        var codigo = Arranque.Ejecutar(s, () => f(s));
        // Ver logs termina siempre con Ctrl+C: eso no es un error.
        return nombre == "logs" && codigo == 130 ? 0 : codigo;
    }
}

public sealed class ApiCommand : GrupoCommand<GrupoSettings> { protected override string Grupo => "api"; }
public sealed class WebCommand : GrupoCommand<GrupoSettings> { protected override string Grupo => "web"; }
public sealed class AppCommand : GrupoCommand<GrupoSettings> { protected override string Grupo => "app"; }
public sealed class EmuCommand : GrupoCommand<EmuSettings> { protected override string Grupo => "emu"; }

public sealed class DbCommand : GrupoCommand<DbSettings>
{
    protected override string Grupo => "db";

    protected override Func<Sesion, bool>? Especial(DbSettings st, string accion) =>
        accion == "restore" && !string.IsNullOrWhiteSpace(st.Archivo) ? s => s.Postgres.Restaurar(st.Archivo) : null;
}

public sealed class TodoSettings : ComandoSettings { }

public sealed class UpCommand : Command<TodoSettings>
{
    public override int Execute(CommandContext context, TodoSettings st)
    {
        var s = Arranque.AbrirSinInterfaz(st);
        return s is null ? 1 : Arranque.Ejecutar(s, s.Todo.Iniciar);
    }
}

public sealed class DownCommand : Command<TodoSettings>
{
    public override int Execute(CommandContext context, TodoSettings st)
    {
        var s = Arranque.AbrirSinInterfaz(st);
        return s is null ? 1 : Arranque.Ejecutar(s, s.Todo.Detener);
    }
}

public sealed class CheckCommand : Command<TodoSettings>
{
    public override int Execute(CommandContext context, TodoSettings st)
    {
        var s = Arranque.AbrirSinInterfaz(st);
        if (s is null) return 1;
        List<ResultadoPaso> res = new();
        var codigo = Arranque.Ejecutar(s, () =>
        {
            res = s.Todo.Verificar();
            return res.All(r => r.Codigo == 0);
        });
        if (res.Count > 0)
        {
            var t = new Table().Border(TableBorder.Rounded).AddColumns("Paso", "Resultado", "Tiempo");
            foreach (var r in res)
                t.AddRow(Markup.Escape(r.Paso), r.Codigo == 0 ? "[green]OK[/]" : $"[red]FALLÓ ({r.Codigo})[/]", $"{r.Segundos} s");
            AnsiConsole.Write(t);
        }
        return codigo;
    }
}

public sealed class RunSettings : ComandoSettings
{
    [CommandArgument(0, "<comando>")]
    [Description("El comando a ejecutar (entre comillas).")]
    public string Comando { get; init; } = "";

    [CommandOption("--in <DONDE>")]
    [Description("Dónde correrlo: api, web, movil, raiz o una carpeta (relativa a la raíz). Por defecto la raíz.")]
    public string? En { get; init; }
}

public sealed class RunCommand : Command<RunSettings>
{
    public override int Execute(CommandContext context, RunSettings st)
    {
        var s = Arranque.AbrirSinInterfaz(st);
        if (s is null) return 1;

        string carpeta;
        switch ((st.En ?? "raiz").ToLowerInvariant())
        {
            case "raiz" or "root" or ".": carpeta = s.Raiz; break;
            case "api": carpeta = Comp(s, Componente.Api, out var ok1) ; if (!ok1) return 1; break;
            case "web": carpeta = Comp(s, Componente.Web, out var ok2); if (!ok2) return 1; break;
            case "movil" or "mobile": carpeta = Comp(s, Componente.Movil, out var ok3); if (!ok3) return 1; break;
            default: carpeta = Rutas.Resolver(s.Raiz, st.En); break;
        }
        if (!Directory.Exists(carpeta)) { s.Ui.Escribir($"No existe la carpeta: {carpeta}", Nivel.Error); return 1; }

        var cmd = Preguntas.Expandir(st.Comando, t => s.Ui.Preguntar(t));
        if (cmd is null) { s.Ui.Escribir("Cancelado.", Nivel.Aviso); return 1; }

        var codigo = 0;
        var salida = Arranque.Ejecutar(s, () => { codigo = s.Ejecutor.Aqui(cmd, carpeta); return codigo == 0; });
        return salida == 1 ? (codigo == 0 ? 1 : codigo) : salida;
    }

    private static string Comp(Sesion s, Componente c, out bool ok)
    {
        ok = s.Ctx.Tiene(c);
        if (!ok) s.Ui.Escribir($"Este proyecto no tiene {Sesion.Nombres[c]} configurado.", Nivel.Error);
        return ok ? s.Ctx.Carpeta(c) : "";
    }
}

public sealed class TareaSettings : ComandoSettings
{
    [CommandArgument(0, "[nombre]")]
    [Description("La tarea de dev-menu.json (o parte de su nombre). Sin nombre: lista las que hay.")]
    public string? Nombre { get; init; }
}

public sealed class TareaCommand : Command<TareaSettings>
{
    public override int Execute(CommandContext context, TareaSettings st)
    {
        var s = Arranque.AbrirSinInterfaz(st);
        if (s is null) return 1;
        if (string.IsNullOrWhiteSpace(st.Nombre))
        {
            if (s.Ctx.Tareas.Count == 0) { s.Ui.Escribir("Este proyecto no tiene tareas en dev-menu.json.", Nivel.Aviso); return 0; }
            foreach (var t in s.Ctx.Tareas)
                s.Ui.Escribir($"  {t.Nombre,-34} [{(string.IsNullOrEmpty(t.Carpeta) ? "." : t.Carpeta)}]  {t.Comando}");
            return 0;
        }
        var codigo = 0;
        var salida = Arranque.Ejecutar(s, () => { codigo = new ConsolaComandos(s).EjecutarTarea(st.Nombre); return codigo == 0; });
        return salida == 1 ? (codigo == 0 ? 1 : codigo) : salida;
    }
}

public sealed class CondSettings : ComandoSettings
{
    [CommandArgument(0, "[numeros]")]
    [Description("Números de las condiciones (3 13 17); 0 = restablecer todo. Sin números: lista las que hay.")]
    public string[] Numeros { get; init; } = Array.Empty<string>();
}

public sealed class CondCommand : Command<CondSettings>
{
    public override int Execute(CommandContext context, CondSettings st)
    {
        var s = Arranque.AbrirSinInterfaz(st);
        if (s is null) return 1;
        if (!s.Ctx.Tiene(Componente.Movil)) { s.Ui.Escribir("Este proyecto no tiene móvil.", Nivel.Error); return 1; }
        if (st.Numeros.Length == 0)
        {
            s.Ui.Escribir($"Activas: {s.Condiciones.Texto()}", Nivel.Info);
            s.Ui.Escribir("  0  Restablecer todo");
            foreach (var p in CondicionesService.Presets) s.Ui.Escribir($"  {p.N,2}  {CondicionesService.Titulo(p)}");
            return 0;
        }
        return Arranque.Ejecutar(s, () => s.Condiciones.AplicarNumeros(st.Numeros));
    }
}

public sealed class StatusCommand : Command<ComandoSettings>
{
    public override int Execute(CommandContext context, ComandoSettings st)
    {
        var s = Arranque.AbrirSinInterfaz(st);
        if (s is null) return 1;
        var foto = Instantanea.Tomar(s);
        AnsiConsole.Write(Tablero.Construir(s, foto, "", conPie: false));
        return 0;
    }
}

public sealed class LogsSettings : ComandoSettings
{
    [CommandArgument(0, "<cual>")]
    [Description("api, web o db.")]
    public string Cual { get; init; } = "";

    [CommandOption("-n|--lineas <N>")]
    [Description("Cuántas líneas mostrar.")]
    [DefaultValue(40)]
    public int Lineas { get; init; } = 40;

    [CommandOption("-f|--seguir")]
    [Description("Seguir el log en vivo (Ctrl+C para salir).")]
    public bool Seguir { get; init; }
}

public sealed class LogsCommand : Command<LogsSettings>
{
    public override int Execute(CommandContext context, LogsSettings st)
    {
        var s = Arranque.AbrirSinInterfaz(st);
        if (s is null) return 1;
        var cual = st.Cual.ToLowerInvariant();
        if (cual is "db" or "postgres")
        {
            foreach (var l in s.Postgres.UltimasLineasLog(st.Lineas)) Console.WriteLine(l);
            return 0;
        }
        if (cual is not ("api" or "web")) { s.Ui.Escribir("Usa: api, web o db.", Nivel.Error); return 2; }
        if (!st.Seguir)
        {
            foreach (var l in s.Servicios.UltimasLineas(cual, st.Lineas)) Console.WriteLine(l);
            return 0;
        }
        Arranque.Ejecutar(s, () => { s.Servicios.Seguir(cual, st.Lineas, s.Ct, l => Console.WriteLine(l)); return true; });
        return 0;
    }
}
