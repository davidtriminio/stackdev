using StackDev.Core.Config;

namespace StackDev.Core.Orquestacion;

/// <summary>
/// Una acción del menú. <paramref name="Codigo"/> es el atajo de siempre (a1, w3, e1...); <paramref name="Grupo"/> y
/// <paramref name="Nombre"/> son para los comandos sin interfaz (<c>stackdev api start</c>).
/// </summary>
public sealed record Accion(string Codigo, string Seccion, string Grupo, string Nombre, string Titulo, Componente? Requiere,
    Func<Sesion, bool> Ejecutar);

public static class Acciones
{
    private static Accion A(string codigo, string seccion, string grupo, string nombre, string titulo, Componente? req, Func<Sesion, bool> f) =>
        new(codigo, seccion, grupo, nombre, titulo, req, f);

    private static bool Cod(int c) => c == 0;

    private static bool Logs(Sesion s, string clave)
    {
        s.Ui.Escribir($"Log de {clave} ({s.Servicios.RutaLog(clave)}). Ctrl+C para volver.", Ui.Nivel.Tenue);
        s.Servicios.Seguir(clave, 40, s.Ct, l => s.Ui.Escribir(l));
        return true;
    }

    public static readonly IReadOnlyList<Accion> Todas = new List<Accion>
    {
        A("1", "TODO",      "up",    "up",      "Levantar todo",                null,             s => s.Todo.Iniciar()),
        A("2", "TODO",      "down",  "down",    "Detener todo",                 null,             s => s.Todo.Detener()),
        A("3", "TODO",      "check", "check",   "Verificar todo (tests y análisis)", null,        s => s.Todo.Verificar().All(r => r.Codigo == 0)),

        A("d1", "POSTGRES", "db", "start",    "Iniciar",                 Componente.Postgres, s => s.Postgres.Iniciar()),
        A("d2", "POSTGRES", "db", "stop",     "Detener",                 Componente.Postgres, s => { s.Postgres.Detener(); return true; }),
        A("d3", "POSTGRES", "db", "psql",     "Consola psql",            Componente.Postgres, s => { s.Postgres.AbrirPsql(); return true; }),
        A("d4", "POSTGRES", "db", "logs",     "Logs",                    Componente.Postgres, s => { s.Postgres.AbrirLogs(); return true; }),
        A("d5", "POSTGRES", "db", "backup",   "Respaldo",                Componente.Postgres, s => s.Postgres.Respaldar() is not null),
        A("d6", "POSTGRES", "db", "restore",  "Restaurar",               Componente.Postgres, s => s.Postgres.Restaurar()),
        A("d7", "POSTGRES", "db", "recreate", "Recrear contenedor",      Componente.Postgres, s => s.Postgres.Recrear()),

        A("a1", "API", "api", "start",    "Iniciar",                     Componente.Api, s => s.Servicios.IniciarApi()),
        A("a2", "API", "api", "stop",     "Detener",                     Componente.Api, s => { s.Servicios.Detener(Componente.Api); return true; }),
        A("a3", "API", "api", "restart",  "Reiniciar",                   Componente.Api, s => { s.Servicios.Reiniciar(Componente.Api); return true; }),
        A("a4", "API", "api", "test",     "Tests",                       Componente.Api, s => Cod(s.Ejecutor.Paso(Componente.Api, "test"))),
        A("a5", "API", "api", "build",    "Build",                       Componente.Api, s => Cod(s.Ejecutor.Paso(Componente.Api, "build"))),
        A("a6", "API", "api", "open",     "Abrir en el navegador",       Componente.Api, s => { s.Servicios.AbrirEnNavegador(Componente.Api); return true; }),
        A("a7", "API", "api", "logs",     "Ver logs aquí",               Componente.Api, s => Logs(s, "api")),
        A("a8", "API", "api", "logs-tab", "Logs en otra pestaña",        Componente.Api, s => { s.Servicios.AbrirLogEnPestana("api"); return true; }),

        A("w1", "WEB", "web", "start",    "Iniciar",                     Componente.Web, s => s.Servicios.IniciarWeb()),
        A("w2", "WEB", "web", "stop",     "Detener",                     Componente.Web, s => { s.Servicios.Detener(Componente.Web); return true; }),
        A("w3", "WEB", "web", "test",     "Tests",                       Componente.Web, s => Cod(s.Ejecutor.Paso(Componente.Web, "test"))),
        A("w4", "WEB", "web", "build",    "Build",                       Componente.Web, s => Cod(s.Ejecutor.Paso(Componente.Web, "build"))),
        A("w5", "WEB", "web", "open",     "Abrir en el navegador",       Componente.Web, s => { s.Servicios.AbrirEnNavegador(Componente.Web); return true; }),
        A("w6", "WEB", "web", "install",  "Instalar dependencias",       Componente.Web, s => Cod(s.Ejecutor.Paso(Componente.Web, "instalar"))),
        A("w7", "WEB", "web", "logs",     "Ver logs aquí",               Componente.Web, s => Logs(s, "web")),
        A("w8", "WEB", "web", "logs-tab", "Logs en otra pestaña",        Componente.Web, s => { s.Servicios.AbrirLogEnPestana("web"); return true; }),

        A("e1", "EMULADOR", "emu", "start",  "Iniciar el último usado",  Componente.Movil, s => s.Emulador.Iniciar()),
        A("e2", "EMULADOR", "emu", "stop",   "Detener (guarda el estado)", Componente.Movil, s => { s.Emulador.Detener(); return true; }),
        A("e3", "EMULADOR", "emu", "switch", "Iniciar otro / cambiar",   Componente.Movil, s => s.Emulador.Cambiar()),
        A("e4", "EMULADOR", "emu", "kill",   "Forzar cierre",            Componente.Movil, s => { s.Emulador.Detener(forzar: true); return true; }),
        A("e5", "EMULADOR", "emu", "cold",   "Arrancar desde cero",      Componente.Movil, s => s.Emulador.IniciarFrio()),

        A("e6", "EMULADOR", "emu", "devices",    "Ver dispositivos Android",          Componente.Movil, s => s.Dispositivos.Mostrar()),
        A("e7", "EMULADOR", "emu", "connect",    "Conectar teléfono por red (Wi-Fi)", Componente.Movil, s => s.Dispositivos.Conectar()),
        A("e8", "EMULADOR", "emu", "pair",       "Emparejar teléfono (Android 11+)",  Componente.Movil, s => s.Dispositivos.Emparejar()),
        A("e9", "EMULADOR", "emu", "disconnect", "Desconectar teléfono de red",       Componente.Movil, s => s.Dispositivos.Desconectar()),

        A("e10", "EMULADOR", "emu", "reverse",   "Reenviar puertos API/web al dispositivo", Componente.Movil, s => s.Dispositivos.Reenviar()),

        A("f1", "FLUTTER", "app", "run",     "Ejecutar app (elige dispositivo)",             Componente.Movil, s => s.Flutter.Ejecutar()),
        A("f2", "FLUTTER", "app", "stop",    "Detener app",              Componente.Movil, s => s.Flutter.Detener()),
        A("f3", "FLUTTER", "app", "analyze", "analyze",                  Componente.Movil, s => Cod(s.Ejecutor.Paso(Componente.Movil, "analizar"))),
        A("f4", "FLUTTER", "app", "test",    "test",                     Componente.Movil, s => Cod(s.Ejecutor.Paso(Componente.Movil, "test"))),
        A("f5", "FLUTTER", "app", "format",  "format",                   Componente.Movil, s => Cod(s.Ejecutor.Paso(Componente.Movil, "formato"))),
        A("f6", "FLUTTER", "app", "gen",     "build_runner",             Componente.Movil, s => Cod(s.Ejecutor.Paso(Componente.Movil, "generar"))),
        A("f7", "FLUTTER", "app", "pub-get", "pub get",                  Componente.Movil, s => Cod(s.Flutter.PubGet())),
        A("f8", "FLUTTER", "app", "clean",   "clean + pub get",          Componente.Movil, s => Cod(s.Flutter.Limpiar())),
        A("b",  "FLUTTER", "app", "mode",    "Modo de build (debug/profile/release)", Componente.Movil, s => { s.Flutter.CambiarModo(); return true; }),
    };

    public static Accion? PorCodigo(string codigo) =>
        Todas.FirstOrDefault(a => string.Equals(a.Codigo, codigo, StringComparison.OrdinalIgnoreCase));

    public static Accion? PorNombre(string grupo, string nombre) =>
        Todas.FirstOrDefault(a => a.Grupo == grupo && string.Equals(a.Nombre, nombre, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<string> NombresDe(string grupo) => Todas.Where(a => a.Grupo == grupo).Select(a => a.Nombre);

    /// <summary>Acciones que se muestran en el menú: solo las de los componentes que el proyecto tiene.</summary>
    public static IEnumerable<Accion> Disponibles(Sesion s) =>
        Todas.Where(a => a.Requiere is null || s.Ctx.Tiene(a.Requiere.Value));
}
