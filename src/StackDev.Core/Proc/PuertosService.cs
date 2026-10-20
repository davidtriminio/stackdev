using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using StackDev.Core.Config;
using StackDev.Core.State;
using StackDev.Core.Ui;
using StackDev.Core.Util;

namespace StackDev.Core.Proc;

/// <summary>
/// Sugiere puertos que no choquen con nada: procesos que ya escuchan, puertos publicados por contenedores
/// (también detenidos: al iniciarlos volverían a ocuparlos), otros proyectos de stackdev, este mismo proyecto
/// y los rangos que Windows reserva (Hyper-V / WSL).
/// </summary>
public sealed class PuertosService
{
    private readonly Sesion _s;
    public PuertosService(Sesion s) { _s = s; }

    /// <summary>Una foto de lo ocupado; se toma una vez y sirve para varias preguntas.</summary>
    public sealed class Mapa
    {
        internal readonly Dictionary<int, string> Reservados = new();
        internal readonly List<(int Desde, int Hasta)> RangosWindows = new();

        /// <summary>Quién ocupa el puerto (texto para mostrar), o null si está libre.</summary>
        public string? Conflicto(int puerto)
        {
            if (puerto <= 0) return null;
            if (Puertos.EstaEscuchando(puerto)) return "ya lo usa " + Proceso(puerto);
            if (Reservados.TryGetValue(puerto, out var q)) return q;
            if (RangosWindows.Any(r => puerto >= r.Desde && puerto <= r.Hasta)) return "Windows lo tiene reservado (Hyper-V/WSL)";
            return null;
        }

        /// <summary>Solo lo reservado por configuración, contenedores o Windows (no cuenta lo que esté escuchando ahora, que puede ser el propio servicio).</summary>
        public string? Reservado(int puerto)
        {
            if (puerto <= 0) return null;
            if (Reservados.TryGetValue(puerto, out var q)) return q;
            return RangosWindows.Any(r => puerto >= r.Desde && puerto <= r.Hasta) ? "Windows lo tiene reservado (Hyper-V/WSL)" : null;
        }

        public bool Libre(int puerto) => Conflicto(puerto) is null;

        /// <summary>El primer puerto libre desde <paramref name="preferido"/> hacia arriba (hasta 300 intentos); null si no hay.</summary>
        public int? Sugerir(int preferido)
        {
            var p = preferido <= 0 ? 5432 : preferido;
            for (var i = 0; i < 300 && p <= 65535; i++, p++)
                if (Libre(p)) return p;
            return null;
        }

        private static string Proceso(int puerto)
        {
            try
            {
                var pid = Puertos.Pids(puerto).FirstOrDefault();
                if (pid > 0) return $"el proceso {Process.GetProcessById(pid).ProcessName} (PID {pid})";
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return "otro proceso";
        }
    }

    /// <param name="ignorarContenedor">Contenedor cuyos puertos no cuentan (el propio que se va a recrear).</param>
    /// <param name="ignorarComponente">Componente de este proyecto cuyo puerto no cuenta (el que se está cambiando).</param>
    public Mapa Tomar(string? ignorarContenedor = null, Componente? ignorarComponente = null)
    {
        var m = new Mapa();

        // Los otros componentes de este proyecto
        var ctx = _s.Ctx;
        var mios = new List<(int Puerto, string Texto)>();   // se agregan al final: no deben tapar a otros proyectos o contenedores
        void Mia(Componente c, int puerto, string nombre)
        {
            if (c == ignorarComponente || puerto <= 0) return;
            mios.Add((puerto, $"lo usa {nombre} de este proyecto"));
        }
        if (ctx.Tiene(Componente.Postgres)) Mia(Componente.Postgres, ctx.Config.Postgres!.PuertoEf, "Postgres");
        if (ctx.Tiene(Componente.Api)) Mia(Componente.Api, ctx.Puerto(Componente.Api), "la API");
        if (ctx.Tiene(Componente.Web)) Mia(Componente.Web, ctx.Puerto(Componente.Web), "la web");

        // Otros proyectos de stackdev
        try
        {
            foreach (var raiz in _s.Estado.LeerProyectos())
            {
                if (string.Equals(Path.GetFullPath(raiz), _s.Raiz, StringComparison.OrdinalIgnoreCase)) continue;
                var otro = new ConfigStore(raiz, _s.Estado.CarpetaConfig(StateStore.Clave(raiz)));
                if (!otro.Existe) continue;
                otro.Cargar();
                var c = otro.Contexto;
                var nombre = c.Nombre;
                void Ajeno(int puerto, string que)
                {
                    if (puerto > 0) m.Reservados.TryAdd(puerto, $"lo usa {que} del proyecto «{nombre}»");
                }
                if (c.Tiene(Componente.Postgres)) Ajeno(c.Config.Postgres!.PuertoEf, "Postgres");
                if (c.Tiene(Componente.Api)) Ajeno(c.Puerto(Componente.Api), "la API");
                if (c.Tiene(Componente.Web)) Ajeno(c.Puerto(Componente.Web), "la web");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }

        // Contenedores (también detenidos)
        if (_s.Docker.Instalado)
        {
            var ids = _s.Docker.Run(new[] { "ps", "-a", "-q" }, 8);
            var lista = ids.Ok ? ids.Salida.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList() : new();
            if (lista.Count > 0)
            {
                var args = new List<string> { "inspect", "--format", "{{.Name}}\t{{json .HostConfig.PortBindings}}" };
                args.AddRange(lista);
                var r = _s.Docker.Run(args, 12);
                foreach (var linea in r.Salida.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var tab = linea.IndexOf('\t');
                    if (tab <= 0) continue;
                    var nombre = linea[..tab].Trim().TrimStart('/');
                    if (ignorarContenedor is not null && nombre == ignorarContenedor) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(linea[(tab + 1)..].Trim());
                        if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                        foreach (var prop in doc.RootElement.EnumerateObject())
                            if (prop.Value.ValueKind == JsonValueKind.Array)
                                foreach (var b in prop.Value.EnumerateArray())
                                    if (b.TryGetProperty("HostPort", out var hp) && int.TryParse(hp.GetString(), out var puerto) && puerto > 0)
                                        m.Reservados.TryAdd(puerto, $"lo publica el contenedor «{nombre}»");
                    }
                    catch (JsonException) { }
                }
            }
        }

        foreach (var (puerto, texto) in mios) m.Reservados.TryAdd(puerto, texto);

        // Rangos reservados por Windows
        if (OperatingSystem.IsWindows())
        {
            var r = ProcessRunner.Run("netsh.exe", new[] { "interface", "ipv4", "show", "excludedportrange", "protocol=tcp" }, 8);
            foreach (Match x in Regex.Matches(r.Salida, @"^\s*(\d+)\s+(\d+)", RegexOptions.Multiline))
                m.RangosWindows.Add((int.Parse(x.Groups[1].Value), int.Parse(x.Groups[2].Value)));
        }
        return m;
    }

    // ------------------------------------------------------------ Plan de puertos del proyecto

    public sealed record CambioPuerto(Componente Componente, int Actual, int Nuevo, string Motivo);

    /// <summary>
    /// Qué puertos de este proyecto habría que mover para que no choquen con otros proyectos, contenedores ni reservas de Windows,
    /// aunque nada esté corriendo. Lo que ya está bien no se toca (el puerto de un proyecto es fijo mientras no choque).
    /// Postgres solo entra si su contenedor aún no existe (si existe, cambiar el puerto obliga a recrearlo).
    /// </summary>
    public List<CambioPuerto> Plan()
    {
        var ctx = _s.Ctx;
        var cambios = new List<CambioPuerto>();
        var pgExiste = false;
        if (ctx.Tiene(Componente.Postgres) && _s.Docker.Instalado)
            pgExiste = _s.Docker.Run(new[] { "inspect", "-f", "{{.Id}}", ctx.Config.Postgres!.ContenedorEf }, 6).Ok;

        var mapa = Tomar(ignorarContenedor: ctx.Tiene(Componente.Postgres) ? ctx.Config.Postgres!.ContenedorEf : null);
        // Los puertos de este proyecto ya están en el mapa como "este proyecto": se evalúa cada uno sin el suyo propio
        var propios = new List<(Componente C, int Puerto)>();
        if (ctx.Tiene(Componente.Postgres) && !pgExiste) propios.Add((Componente.Postgres, ctx.Config.Postgres!.PuertoEf));
        if (ctx.Tiene(Componente.Api) && ctx.Puerto(Componente.Api) > 0) propios.Add((Componente.Api, ctx.Puerto(Componente.Api)));
        if (ctx.Tiene(Componente.Web) && ctx.Puerto(Componente.Web) > 0) propios.Add((Componente.Web, ctx.Puerto(Componente.Web)));

        foreach (var (comp, puerto) in propios)
        {
            var motivo = mapa.Reservado(puerto);
            // "lo usa X de este proyecto" apunta a otro componente del mismo proyecto, salvo que sea el mismo puerto compartido
            if (motivo is not null && motivo.Contains("de este proyecto") && propios.Count(x => x.Puerto == puerto) < 2) motivo = null;
            if (motivo is null) continue;
            var nuevo = mapa.Sugerir(puerto + 1);
            if (nuevo is null) continue;
            mapa.Reservados[nuevo.Value] = "lo usa este proyecto";
            cambios.Add(new CambioPuerto(comp, puerto, nuevo.Value, motivo));
        }
        return cambios;
    }

    private static string CambiarPuertoEnTexto(string? texto, int viejo, int nuevo) =>
        texto is null ? "" : Regex.Replace(texto, $@"(?<=:|--port[ =]|-p )(?:{viejo})(?!\d)", nuevo.ToString());

    /// <summary>Guarda los cambios: puerto de Postgres, URL de API/web y las veces que el puerto viejo aparece en sus comandos y variables.</summary>
    public void Aplicar(IReadOnlyList<CambioPuerto> cambios)
    {
        if (cambios.Count == 0) return;
        var copia = ConfigJson.Leer(ConfigJson.Escribir(_s.Ctx.Config));
        foreach (var c in cambios)
        {
            switch (c.Componente)
            {
                case Componente.Postgres when copia.Postgres is not null:
                    copia.Postgres.Puerto = c.Nuevo;
                    break;
                case Componente.Api when copia.Api is not null:
                    copia.Api.Url = Regex.Replace(copia.Api.Url ?? "", @"(://[^/:]+):\d+", $"$1:{c.Nuevo}");
                    copia.Api.Iniciar = CambiarPuertoEnTexto(copia.Api.Iniciar, c.Actual, c.Nuevo) is { Length: > 0 } i1 ? i1 : copia.Api.Iniciar;
                    if (copia.Api.Env is not null)
                        foreach (var k in copia.Api.Env.Keys.ToList())
                            copia.Api.Env[k] = CambiarPuertoEnTexto(copia.Api.Env[k], c.Actual, c.Nuevo);
                    break;
                case Componente.Web when copia.Web is not null:
                    copia.Web.Url = Regex.Replace(copia.Web.Url ?? "", @"(://[^/:]+):\d+", $"$1:{c.Nuevo}");
                    copia.Web.Iniciar = CambiarPuertoEnTexto(copia.Web.Iniciar, c.Actual, c.Nuevo) is { Length: > 0 } i2 ? i2 : copia.Web.Iniciar;
                    break;
            }
        }
        _s.AplicarConfig(copia);
        foreach (var c in cambios)
        {
            _s.Ui.Escribir($"{Sesion.Nombres[c.Componente]}: puerto {c.Actual} → {c.Nuevo}", Nivel.Ok);
            if (c.Componente == Componente.Postgres) continue;
            var refs = Referencias(c.Actual);
            if (refs.Count > 0)
            {
                _s.Ui.Escribir($"  El puerto {c.Actual} aparece todavía en estos archivos del proyecto (revísalos a mano):", Nivel.Aviso);
                foreach (var r in refs) _s.Ui.Escribir($"    {r}", Nivel.Tenue);
            }
        }
    }

    private static readonly string[] Extensiones = { ".json", ".ts", ".js", ".env", ".dart", ".cs", ".yml", ".yaml", ".html", ".config", ".properties" };
    private static readonly string[] Excluidas = { "node_modules", "bin", "obj", ".git", ".dart_tool", "build", "dist", ".angular", ".idea", ".vs", ".gradle", "Pods" };

    /// <summary>Archivos del proyecto donde aparece <c>:puerto</c> (launchSettings, environment.ts, CORS...). Máximo 10.</summary>
    public List<string> Referencias(int puerto)
    {
        var res = new List<string>();
        var patron = new Regex($@":{puerto}(?!\d)");
        foreach (var comp in new[] { Componente.Api, Componente.Web, Componente.Movil })
        {
            if (!_s.Ctx.Tiene(comp)) continue;
            var dir = _s.Ctx.Carpeta(comp);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Archivos(dir))
            {
                if (res.Count >= 10) return res;
                try
                {
                    if (new FileInfo(f).Length > 300_000) continue;
                    var lineas = File.ReadLines(f).Take(4000).ToList();
                    var n = lineas.FindIndex(l => patron.IsMatch(l));
                    if (n >= 0) res.Add($"{_s.Rel(f)}:{n + 1}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return res;
    }

    private static IEnumerable<string> Archivos(string dir)
    {
        var pila = new Stack<string>();
        pila.Push(dir);
        while (pila.Count > 0)
        {
            var d = pila.Pop();
            string[] subs, files;
            try { subs = Directory.GetDirectories(d); files = Directory.GetFiles(d); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files)
                if (Extensiones.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) || Path.GetFileName(f).StartsWith(".env")) yield return f;
            foreach (var sd in subs)
                if (!Excluidas.Contains(Path.GetFileName(sd), StringComparer.OrdinalIgnoreCase)) pila.Push(sd);
        }
    }
}
