using StackDev.Core.Android;
using StackDev.Core.Config;
using StackDev.Core.Detection;
using StackDev.Core.Docker;
using StackDev.Core.Flutter;
using StackDev.Core.Orquestacion;
using StackDev.Core.Servicios;
using StackDev.Core.State;
using StackDev.Core.Ui;
using StackDev.Core.Util;

namespace StackDev.Core;

/// <summary>Opciones que se pasan al abrir la herramienta (equivalen a los parámetros de flutter.ps1).</summary>
public sealed class OpcionesSesion
{
    public string? Avd { get; set; }
    public string ArgsExtra { get; set; } = "";
    public string Modo { get; set; } = "debug";
    public bool MismaVentana { get; set; }
    public bool NoCentrar { get; set; }
    /// <summary>Serial (o «todos») donde correr la app; sin él se pregunta si hay varios dispositivos.</summary>
    public string? Dispositivo { get; set; }
    /// <summary>Dirección «ip:puerto» para conectar o emparejar sin preguntar.</summary>
    public string? Direccion { get; set; }
    public string? CodigoEmparejar { get; set; }
}

/// <summary>Un proyecto abierto: su configuración, su estado personal y todos los servicios que lo manejan.</summary>
public sealed class Sesion
{
    public static readonly IReadOnlyDictionary<Componente, string> Nombres = new Dictionary<Componente, string>
    {
        [Componente.Postgres] = "Postgres", [Componente.Api] = "API", [Componente.Web] = "Web", [Componente.Movil] = "Móvil",
    };

    public Sesion(string raiz, StateStore estado, IConsolaUi ui, OpcionesSesion? opciones = null)
    {
        Raiz = Rutas.Normalizar(raiz);
        Estado = estado;
        Ui = ui;
        Op = opciones ?? new OpcionesSesion();
        Clave = StateStore.Clave(Raiz);
        Store = new ConfigStore(Raiz, estado.CarpetaConfig(Clave));
        ModoBuild = Op.Modo;

        Docker = new DockerService(this);
        Postgres = new PostgresService(this);
        Admin = new DockerAdmin(this);
        Libres = new Proc.PuertosService(this);
        Revisor = new StackDev.Core.Diagnostico.DiagnosticoService(this);
        Servicios = new ServiciosManager(this);
        Ejecutor = new EjecutorComandos(this);
        Condiciones = new CondicionesService(this);
        Emulador = new EmulatorService(this);
        Dispositivos = new DispositivosService(this);
        Flutter = new FlutterService(this);
        Todo = new OrquestadorTodo(this);
    }

    public string Raiz { get; }
    public string Clave { get; }
    public StateStore Estado { get; }
    public ConfigStore Store { get; }
    public IConsolaUi Ui { get; set; }
    public OpcionesSesion Op { get; }
    public ProyectoContexto Ctx => Store.Contexto;

    /// <summary>Se cancela con Ctrl+C: afecta solo al comando que está corriendo.</summary>
    public CancellationToken Ct { get; set; }

    /// <summary>Si la interfaz no tiene consola (la ventana gráfica), aquí llega la salida de los comandos que se ejecutan "aquí".</summary>
    public Action<string>? Captura { get; set; }

    /// <summary>Corre mostrando la salida: en la consola, o en <see cref="Captura"/> si hay.</summary>
    public int CorrerVisible(string exe, IEnumerable<string> args, string? cwd, IReadOnlyDictionary<string, string>? env) =>
        Captura is null
            ? Proc.ProcessRunner.RunHeredando(exe, args, cwd, env, Ct)
            : Proc.ProcessRunner.RunCapturando(exe, args, cwd, env, Ct, l => Captura(StackDev.Core.Servicios.ServiciosManager.LimpiarAnsi(l)));

    public string ModoBuild { get; set; }
    public string? Avd { get; set; }

    public string? Sdk { get; private set; }
    public string? EmulatorExe { get; private set; }
    public string? AdbExe { get; private set; }
    public AdbClient? Adb { get; private set; }
    public IReadOnlyDictionary<string, string>? EnvAndroid { get; private set; }

    public DockerService Docker { get; }
    public DockerAdmin Admin { get; }
    public Proc.PuertosService Libres { get; }
    public StackDev.Core.Diagnostico.DiagnosticoService Revisor { get; }
    public PostgresService Postgres { get; }
    public ServiciosManager Servicios { get; }
    public EjecutorComandos Ejecutor { get; }
    public CondicionesService Condiciones { get; }
    public EmulatorService Emulador { get; }
    public DispositivosService Dispositivos { get; }
    public FlutterService Flutter { get; }
    public OrquestadorTodo Todo { get; }

    /// <summary>Carga (o crea con la detección) dev-menu.json, prepara el Android SDK y elige el emulador por defecto.</summary>
    public void Iniciar()
    {
        if (!Store.Existe)
        {
            Ui.Escribir("Este proyecto aún no tiene configuración: la creo ahora.", Nivel.Info);
            Store.Guardar(new AsistenteConfig(this).Crear());
            Ui.Escribir($"\nGuardada en {Store.Ruta}", Nivel.Ok);
            Ui.Escribir("Revísala con la opción de configuración si algo no cuadra (comandos, puertos, tareas...).", Nivel.Tenue);
        }
        Store.Cargar();
        if (Store.Error is not null)
        {
            Ui.Escribir($"dev-menu.json tiene un error: {Store.Error}", Nivel.Error);
            Ui.Escribir("Corrígelo; mientras tanto el menú queda vacío.", Nivel.Aviso);
        }
        InicializarAndroid();
        Avd = Estado.ResolverAvd(Clave, Op.Avd, Ctx.Config.Movil?.Avd);
    }

    /// <summary>Recarga dev-menu.json si el usuario lo editó. Devuelve true si cambió.</summary>
    public bool RecargarSiCambio()
    {
        if (!Store.RecargarSiCambio()) return false;
        InicializarAndroid();
        return true;
    }

    /// <summary>Guarda una configuración editada a mano (deja copia .bak) y la aplica sin reiniciar.</summary>
    public void AplicarConfig(ProyectoConfig nueva)
    {
        Store.Reemplazar(nueva);
        InicializarAndroid();
        Avd = Estado.ResolverAvd(Clave, Op.Avd, Ctx.Config.Movil?.Avd);
    }

    /// <summary>Detecta de nuevo (deja una copia dev-menu.json.bak) y recarga.</summary>
    public void Redetectar()
    {
        Store.Reemplazar(new AsistenteConfig(this).Crear());
        InicializarAndroid();
        Avd = Estado.ResolverAvd(Clave, Op.Avd, Ctx.Config.Movil?.Avd);
        Ui.Escribir("Configuración regenerada (la anterior quedó en dev-menu.json.bak).", Nivel.Ok);
    }

    /// <summary>Abre dev-menu.json en VS Code o, si no está, en el Bloc de notas. Al guardar, la herramienta lo recarga sola.</summary>
    public void AbrirEditorConfig()
    {
        var code = Proc.Shell.BuscarEnPath("code");
        try
        {
            if (code is not null)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(code, Util.Args.Quote(Store.Ruta)) { UseShellExecute = true, CreateNoWindow = true });
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", Util.Args.Quote(Store.Ruta)) { UseShellExecute = true });
            Ui.Escribir("Al guardar el archivo, la herramienta lo recarga sola.", Nivel.Tenue);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Ui.Escribir($"No pude abrir el editor: {ex.Message}. El archivo está en {Store.Ruta}", Nivel.Error);
        }
    }

    private void InicializarAndroid()
    {
        Sdk = EmulatorExe = AdbExe = null;
        Adb = null;
        EnvAndroid = null;
        if (!Ctx.Tiene(Componente.Movil)) return;

        string? flutterSettings = null;
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrEmpty(appData))
        {
            try { var f = Path.Combine(appData, ".flutter_settings"); if (File.Exists(f)) flutterSettings = File.ReadAllText(f); }
            catch (IOException) { }
        }
        var sdk = AndroidSdkLocator.Buscar(Ctx.Config.Movil?.AndroidSdk, Environment.GetEnvironmentVariable, flutterSettings,
            Environment.GetEnvironmentVariable("LOCALAPPDATA"));
        if (sdk is null) return;

        // Solo para los procesos que lanzamos (emulador, adb, flutter): no se tocan las variables globales
        Sdk = sdk;
        EnvAndroid = AndroidSdkLocator.VariablesDeProceso(sdk);
        EmulatorExe = AndroidSdkLocator.EmulatorExe(sdk);
        var adb = AndroidSdkLocator.AdbExe(sdk);
        if (!File.Exists(adb)) return;
        AdbExe = adb;
        Adb = new AdbClient(adb, EnvAndroid);
        Adb.IniciarServidor();
    }

    /// <summary>Variables de entorno para un comando en <paramref name="carpeta"/>: Android SDK + las de la API si corresponde.</summary>
    public IReadOnlyDictionary<string, string>? EnvPara(string carpeta)
    {
        var propias = Ctx.EnvParaCarpeta(carpeta);
        if (EnvAndroid is null) return propias;
        if (propias is null) return EnvAndroid;
        var res = new Dictionary<string, string>(EnvAndroid);
        foreach (var (k, v) in propias) res[k] = v;
        return res;
    }

    public bool AfirmarComp(Componente c)
    {
        if (!Ctx.Tiene(c))
        {
            Ui.Escribir($"Este proyecto no tiene {Nombres[c]} configurado (edita dev-menu.json).", Nivel.Aviso);
            return false;
        }
        if (c != Componente.Postgres && !Directory.Exists(Ctx.Carpeta(c)))
        {
            Ui.Escribir($"No existe la carpeta de {Nombres[c]}: {Ctx.Carpeta(c)}", Nivel.Error);
            return false;
        }
        return true;
    }

    /// <summary>Un comando de la configuración (api.test, web.build, movil.analizar...), o null si no está.</summary>
    public string? Comando(Componente c, string clave)
    {
        string? v = c switch
        {
            Componente.Api => clave switch { "iniciar" => Ctx.Config.Api?.Iniciar, "test" => Ctx.Config.Api?.Test, "build" => Ctx.Config.Api?.Build, _ => null },
            Componente.Web => clave switch { "iniciar" => Ctx.Config.Web?.Iniciar, "test" => Ctx.Config.Web?.Test, "build" => Ctx.Config.Web?.Build, "instalar" => Ctx.Config.Web?.Instalar, _ => null },
            Componente.Movil => clave switch { "analizar" => Ctx.Config.Movil?.Analizar, "test" => Ctx.Config.Movil?.Test, "formato" => Ctx.Config.Movil?.Formato, "generar" => Ctx.Config.Movil?.Generar, _ => null },
            _ => null,
        };
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    public string Rel(string ruta) => Rutas.Relativa(Raiz, ruta);
}
