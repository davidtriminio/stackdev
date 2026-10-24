using System.Text;
using Spectre.Console.Cli;
using StackDev.Cli.Commands;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

var app = new CommandApp<HomeCommand>();
app.Configure(c =>
{
    c.SetApplicationName("stackdev");

    c.AddCommand<UpCommand>("up").WithDescription("Levanta todo lo que dice startAll (Postgres, API, web, emulador…).");
    c.AddCommand<DownCommand>("down").WithDescription("Detiene todo.");
    c.AddCommand<CheckCommand>("check").WithDescription("Tests y análisis de todo el proyecto (código de salida distinto de 0 si algo falla).");
    c.AddCommand<ApiCommand>("api").WithDescription("API: start, stop, restart, test, build, open, logs, logs-tab.");
    c.AddCommand<WebCommand>("web").WithDescription("Web: start, stop, test, build, open, install, logs, logs-tab.");
    c.AddCommand<DbCommand>("db").WithDescription("Postgres: start, stop, psql, logs, backup, restore [--archivo], recreate.");
    c.AddCommand<EmuCommand>("emu").WithDescription("Emulador: start [--avd X] [--cold], stop, switch, kill, cold.");
    c.AddCommand<AppCommand>("app").WithDescription("Flutter: run, stop, analyze, test, format, gen, pub-get, clean, mode.");
    c.AddCommand<RunCommand>("run").WithDescription("Ejecuta un comando en una carpeta: stackdev run \"dotnet ef migrations list\" --in api");
    c.AddCommand<TaskCommand>("tarea").WithDescription("Ejecuta una tarea de dev-menu.json (sin nombre: lista las que hay).");
    c.AddCommand<CondCommand>("cond").WithDescription("Condiciones del dispositivo: stackdev cond 3 13 (0 = restablecer; sin números: lista).");
    c.AddCommand<StatusCommand>("status").WithDescription("Muestra una vez el estado de todo, sin panel en vivo.");
    c.AddCommand<LogsCommand>("logs").WithDescription("Últimas líneas del log de api, web o db (-f para seguirlo).");

    c.AddCommand<DetectCommand>("detectar")
        .WithDescription("Detecta la API, la web, Flutter y Postgres del proyecto (y opcionalmente crea dev-menu.json).");
    c.AddCommand<ConfigCommand>("config")
        .WithDescription("Muestra cómo se lee dev-menu.json: componentes, carpetas, puertos y variables de entorno.");
    c.AddCommand<StatusBoardCommand>("estado")
        .WithDescription("Muestra la carpeta del estado personal (%APPDATA%\\stackdev) y la migración desde previous-state.");
});
return app.Run(args);
