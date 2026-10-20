using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using StackDev.Cli.Ui;
using StackDev.Core;
using StackDev.Core.Detection;
using StackDev.Core.State;
using StackDev.Core.Ui;

namespace StackDev.Cli.Comandos;

/// <summary>Para detectar y config: el proyecto puede ir como primer argumento.</summary>
public class RutaSettings : CommandSettings
{
    [CommandArgument(0, "[ruta]")]
    [Description("Raíz del proyecto. Sin ruta: la carpeta actual si parece un proyecto, o el último proyecto usado.")]
    public string? Ruta { get; init; }
}

/// <summary>Opciones que entienden todos los comandos que operan sobre un proyecto.</summary>
public class ComandoSettings : CommandSettings
{
    [CommandOption("-p|--proyecto <RUTA>")]
    [Description("Raíz del proyecto (por defecto: la carpeta actual o el último proyecto usado).")]
    public string? Proyecto { get; init; }

    [CommandOption("--avd <NOMBRE>")]
    [Description("Emulador a usar (por defecto el último usado en este proyecto).")]
    public string? Avd { get; init; }

    [CommandOption("--modo <MODO>")]
    [Description("Modo de flutter run: debug, profile o release.")]
    [DefaultValue("debug")]
    public string Modo { get; init; } = "debug";

    [CommandOption("--args-extra <ARGS>")]
    [Description("Argumentos extra para flutter run (p. ej. \"--flavor dev\").")]
    public string? ArgsExtra { get; init; }

    [CommandOption("--misma-ventana")]
    [Description("flutter run en esta misma consola, sin ventana propia.")]
    public bool MismaVentana { get; init; }

    [CommandOption("--no-centrar")]
    [Description("No mover la ventana del emulador.")]
    public bool NoCentrar { get; init; }

    [CommandOption("-y|--si")]
    [Description("Contesta «sí» a todas las preguntas (para scripts).")]
    public bool Si { get; init; }

    [CommandOption("-d|--dispositivo <SERIAL>")]
    [Description("Dispositivo donde correr la app: un serial de adb (emulator-5554, 192.168.1.20:5555…) o «todos».")]
    public string? Dispositivo { get; init; }

    [CommandOption("--direccion <IP:PUERTO>")]
    [Description("Con emu connect, pair o disconnect: dirección del teléfono (sin ella se pregunta o se usan las guardadas).")]
    public string? Direccion { get; init; }

    [CommandOption("--codigo-emparejar <CODIGO>")]
    [Description("Con emu pair: el código de 6 dígitos que muestra el teléfono.")]
    public string? CodigoEmparejar { get; init; }

    public OpcionesSesion ComoOpciones() => new()
    {
        Dispositivo = Dispositivo,
        Direccion = Direccion,
        CodigoEmparejar = CodigoEmparejar,
        Avd = Avd,
        ArgsExtra = ArgsExtra ?? "",
        Modo = Modo,
        MismaVentana = MismaVentana,
        NoCentrar = NoCentrar,
    };
}

public static class Proyecto
{
    /// <summary>Decide la raíz del proyecto: parámetro → carpeta actual → último usado (se pregunta) → pedir la ruta.</summary>
    public static string? Elegir(string? parametro, StateStore estado)
    {
        var r = RaizProyecto.Resolver(parametro, Directory.GetCurrentDirectory(), estado.UltimoProyecto);

        if (r.ParametroInvalido)
            AnsiConsole.MarkupLineInterpolated($"[yellow]'{parametro}' no existe.[/]");

        string? ruta = r.Origen switch
        {
            OrigenProyecto.Parametro or OrigenProyecto.CarpetaActual => r.Ruta,
            OrigenProyecto.UltimoUsado =>
                AnsiConsole.Confirm($"Último proyecto: {Markup.Escape(r.Ruta!)}. ¿Usarlo?") ? r.Ruta : Interactivo.SelectorProyecto.Elegir(estado.UltimoProyecto),
            _ => Interactivo.SelectorProyecto.Elegir(estado.UltimoProyecto),
        };
        if (ruta is null) return null;
        return Confirmar(ruta, estado);
    }

    /// <summary>Si eligieron una subcarpeta (p. ej. mobile/) y la de arriba tiene .git o dev-menu.json, ofrece la raíz.</summary>
    public static string Confirmar(string ruta, StateStore estado)
    {
        ruta = Path.GetFullPath(ruta);
        var padre = RaizProyecto.SugerirPadre(ruta);
        if (padre is not null)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]'{Path.GetFileName(ruta)}' parece ser parte del proyecto '{padre}'.[/]");
            if (AnsiConsole.Confirm("¿Usar la carpeta raíz del proyecto?")) ruta = padre;
        }
        estado.UltimoProyecto = ruta;
        return ruta;
    }

    /// <summary>Para scripts: nunca pregunta. Parámetro → carpeta actual → último usado.</summary>
    public static string? ElegirSinPreguntar(string? parametro, StateStore estado, IConsolaUi ui)
    {
        var r = RaizProyecto.Resolver(parametro, Directory.GetCurrentDirectory(), estado.UltimoProyecto);
        switch (r.Origen)
        {
            case OrigenProyecto.UltimoUsado:
                ui.Escribir($"Usando el último proyecto: {r.Ruta}", Nivel.Tenue);
                break;
            case OrigenProyecto.Selector:
                ui.Escribir(r.ParametroInvalido ? $"'{parametro}' no existe." : "No hay un proyecto aquí. Usa -p <ruta>.", Nivel.Error);
                return null;
        }
        estado.UltimoProyecto = r.Ruta;
        return r.Ruta;
    }
}

/// <summary>Cancelación por comando: Ctrl+C cancela solo lo que está corriendo y devuelve el control.</summary>
public static class Cancelacion
{
    private static CancellationTokenSource? _actual;
    private static bool _enganchado;

    public static void Enganchar()
    {
        if (_enganchado) return;
        _enganchado = true;
        Console.CancelKeyPress += (_, e) =>
        {
            var cts = _actual;
            if (cts is not null && !cts.IsCancellationRequested)
            {
                e.Cancel = true;   // no cerrar la herramienta: solo cancelar el comando en curso
                cts.Cancel();
            }
        };
    }

    /// <summary>Corre <paramref name="f"/> con un token que se cancela con Ctrl+C. Devuelve false si falló o se canceló.</summary>
    public static bool Correr(Sesion s, Func<bool> f, out bool cancelado)
    {
        Enganchar();
        using var cts = new CancellationTokenSource();
        _actual = cts;
        s.Ct = cts.Token;
        try { return f(); }
        finally
        {
            cancelado = cts.IsCancellationRequested;
            _actual = null;
            s.Ct = CancellationToken.None;
        }
    }
}

public static class Arranque
{
    /// <summary>Abre el proyecto para un comando sin interfaz (nunca hace preguntas de más).</summary>
    public static Sesion? AbrirSinInterfaz(ComandoSettings st)
    {
        var estado = StateStore.Abrir();
        var ui = new ConsolaSimple(!Console.IsInputRedirected, st.Si);
        var raiz = Proyecto.ElegirSinPreguntar(st.Proyecto, estado, ui);
        if (raiz is null) return null;
        var s = new Sesion(raiz, estado, ui, st.ComoOpciones());
        s.Iniciar();
        return s;
    }

    /// <summary>Corre una acción y devuelve el código de salida del proceso (0 bien, 1 falló, 130 cancelada).</summary>
    public static int Ejecutar(Sesion s, Func<bool> f)
    {
        var ok = Cancelacion.Correr(s, f, out var cancelado);
        if (cancelado) { s.Ui.Escribir("Cancelado.", Nivel.Aviso); return 130; }
        return ok ? 0 : 1;
    }
}
