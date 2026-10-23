using StackDev.Core.Config;

namespace StackDev.Core.Orchestration;

/// <summary>
/// Una acción del menú. <paramref name="Code"/> es el atajo de siempre (a1, w3, e1...); <paramref name="Group"/> y
/// <paramref name="Name"/> son para los comandos sin interfaz (<c>stackdev api start</c>).
/// </summary>
public sealed record Operation(string Code, string Section, string Group, string Name, string Title, ProjectComponent? Requires,
    Func<Session, bool> Run);

public static class Operations
{
    private static Operation A(string code, string section, string group, string name, string title, ProjectComponent? req, Func<Session, bool> f) =>
        new(code, section, group, name, title, req, f);

    private static bool Code(int c) => c == 0;

    private static bool Logs(Session s, string key)
    {
        s.Ui.Write($"Log de {key} ({s.Services.LogPath(key)}). Ctrl+C para volver.", Ui.Level.Muted);
        s.Services.Follow(key, 40, s.Ct, l => s.Ui.Write(l));
        return true;
    }

    public static readonly IReadOnlyList<Operation> All = new List<Operation>
    {
        A("1", "TODO",      "up",    "up",      "Levantar todo",                null,             s => s.All.Start()),
        A("2", "TODO",      "down",  "down",    "Detener todo",                 null,             s => s.All.Stop()),
        A("3", "TODO",      "check", "check",   "Verificar todo (tests y análisis)", null,        s => s.All.Verify().All(r => r.Code == 0)),

        A("d1", "POSTGRES", "db", "start",    "Iniciar",                 ProjectComponent.Postgres, s => s.Postgres.Start()),
        A("d2", "POSTGRES", "db", "stop",     "Detener",                 ProjectComponent.Postgres, s => { s.Postgres.Stop(); return true; }),
        A("d3", "POSTGRES", "db", "psql",     "Consola psql",            ProjectComponent.Postgres, s => { s.Postgres.OpenPsql(); return true; }),
        A("d4", "POSTGRES", "db", "logs",     "Logs",                    ProjectComponent.Postgres, s => { s.Postgres.OpenLogs(); return true; }),
        A("d5", "POSTGRES", "db", "backup",   "Respaldo",                ProjectComponent.Postgres, s => s.Postgres.Back() is not null),
        A("d6", "POSTGRES", "db", "restore",  "Restaurar",               ProjectComponent.Postgres, s => s.Postgres.Restore()),
        A("d7", "POSTGRES", "db", "recreate", "Recrear contenedor",      ProjectComponent.Postgres, s => s.Postgres.Recreate()),

        A("a1", "API", "api", "start",    "Iniciar",                     ProjectComponent.Api, s => s.Services.StartApi()),
        A("a2", "API", "api", "stop",     "Detener",                     ProjectComponent.Api, s => { s.Services.Stop(ProjectComponent.Api); return true; }),
        A("a3", "API", "api", "restart",  "Reiniciar",                   ProjectComponent.Api, s => { s.Services.Restart(ProjectComponent.Api); return true; }),
        A("a4", "API", "api", "test",     "Tests",                       ProjectComponent.Api, s => Code(s.Executor.Step(ProjectComponent.Api, "test"))),
        A("a5", "API", "api", "build",    "Build",                       ProjectComponent.Api, s => Code(s.Executor.Step(ProjectComponent.Api, "build"))),
        A("a6", "API", "api", "open",     "Abrir en el navegador",       ProjectComponent.Api, s => { s.Services.OpenInBrowser(ProjectComponent.Api); return true; }),
        A("a7", "API", "api", "logs",     "Ver logs aquí",               ProjectComponent.Api, s => Logs(s, "api")),
        A("a8", "API", "api", "logs-tab", "Logs en otra pestaña",        ProjectComponent.Api, s => { s.Services.OpenLogInTab("api"); return true; }),

        A("w1", "WEB", "web", "start",    "Iniciar",                     ProjectComponent.Web, s => s.Services.StartWeb()),
        A("w2", "WEB", "web", "stop",     "Detener",                     ProjectComponent.Web, s => { s.Services.Stop(ProjectComponent.Web); return true; }),
        A("w3", "WEB", "web", "test",     "Tests",                       ProjectComponent.Web, s => Code(s.Executor.Step(ProjectComponent.Web, "test"))),
        A("w4", "WEB", "web", "build",    "Build",                       ProjectComponent.Web, s => Code(s.Executor.Step(ProjectComponent.Web, "build"))),
        A("w5", "WEB", "web", "open",     "Abrir en el navegador",       ProjectComponent.Web, s => { s.Services.OpenInBrowser(ProjectComponent.Web); return true; }),
        A("w6", "WEB", "web", "install",  "Instalar dependencias",       ProjectComponent.Web, s => Code(s.Executor.Step(ProjectComponent.Web, "instalar"))),
        A("w7", "WEB", "web", "logs",     "Ver logs aquí",               ProjectComponent.Web, s => Logs(s, "web")),
        A("w8", "WEB", "web", "logs-tab", "Logs en otra pestaña",        ProjectComponent.Web, s => { s.Services.OpenLogInTab("web"); return true; }),

        A("e1", "EMULADOR", "emu", "start",  "Iniciar el último usado",  ProjectComponent.Mobile, s => s.Emulator.Start()),
        A("e2", "EMULADOR", "emu", "stop",   "Detener (guarda el estado)", ProjectComponent.Mobile, s => { s.Emulator.Stop(); return true; }),
        A("e3", "EMULADOR", "emu", "switch", "Iniciar otro / cambiar",   ProjectComponent.Mobile, s => s.Emulator.Change()),
        A("e4", "EMULADOR", "emu", "kill",   "Forzar cierre",            ProjectComponent.Mobile, s => { s.Emulator.Stop(force: true); return true; }),
        A("e5", "EMULADOR", "emu", "cold",   "Arrancar desde cero",      ProjectComponent.Mobile, s => s.Emulator.StartCold()),

        A("e6", "EMULADOR", "emu", "devices",    "Ver dispositivos Android",          ProjectComponent.Mobile, s => s.Devices.Show()),
        A("e7", "EMULADOR", "emu", "connect",    "Conectar teléfono por red (Wi-Fi)", ProjectComponent.Mobile, s => s.Devices.Connect()),
        A("e8", "EMULADOR", "emu", "pair",       "Emparejar teléfono (Android 11+)",  ProjectComponent.Mobile, s => s.Devices.Pair()),
        A("e9", "EMULADOR", "emu", "disconnect", "Desconectar teléfono de red",       ProjectComponent.Mobile, s => s.Devices.Disconnect()),

        A("e10", "EMULADOR", "emu", "reverse",   "Reenviar puertos API/web al dispositivo", ProjectComponent.Mobile, s => s.Devices.Forward()),

        A("f1", "FLUTTER", "app", "run",     "Ejecutar app (elige dispositivo)",             ProjectComponent.Mobile, s => s.Flutter.Run()),
        A("f2", "FLUTTER", "app", "stop",    "Detener app",              ProjectComponent.Mobile, s => s.Flutter.Stop()),
        A("f3", "FLUTTER", "app", "analyze", "analyze",                  ProjectComponent.Mobile, s => Code(s.Executor.Step(ProjectComponent.Mobile, "analizar"))),
        A("f4", "FLUTTER", "app", "test",    "test",                     ProjectComponent.Mobile, s => Code(s.Executor.Step(ProjectComponent.Mobile, "test"))),
        A("f5", "FLUTTER", "app", "format",  "format",                   ProjectComponent.Mobile, s => Code(s.Executor.Step(ProjectComponent.Mobile, "formato"))),
        A("f6", "FLUTTER", "app", "gen",     "build_runner",             ProjectComponent.Mobile, s => Code(s.Executor.Step(ProjectComponent.Mobile, "generar"))),
        A("f7", "FLUTTER", "app", "pub-get", "pub get",                  ProjectComponent.Mobile, s => Code(s.Flutter.PubGet())),
        A("f8", "FLUTTER", "app", "clean",   "clean + pub get",          ProjectComponent.Mobile, s => Code(s.Flutter.Clean())),
        A("b",  "FLUTTER", "app", "mode",    "Modo de build (debug/profile/release)", ProjectComponent.Mobile, s => { s.Flutter.ChangeMode(); return true; }),
    };

    public static Operation? ByCode(string code) =>
        All.FirstOrDefault(a => string.Equals(a.Code, code, StringComparison.OrdinalIgnoreCase));

    public static Operation? ByName(string group, string name) =>
        All.FirstOrDefault(a => a.Group == group && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<string> NamesOf(string group) => All.Where(a => a.Group == group).Select(a => a.Name);

    /// <summary>Acciones que se muestran en el menú: solo las de los componentes que el proyecto tiene.</summary>
    public static IEnumerable<Operation> Available(Session s) =>
        All.Where(a => a.Requires is null || s.Ctx.Has(a.Requires.Value));
}
