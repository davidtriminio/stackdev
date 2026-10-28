using StackDev.Core.Android;
using StackDev.Core.Config;
using StackDev.Core.Detection;
using StackDev.Core.Docker;
using StackDev.Core.Flutter;
using StackDev.Core.Orchestration;
using StackDev.Core.Services;
using StackDev.Core.State;
using StackDev.Core.Ui;
using StackDev.Core.Util;

namespace StackDev.Core;

/// <summary>Options passed when the tool is opened.</summary>
public sealed class SessionOptions
{
    public string? Avd { get; set; }
    public string ArgsExtra { get; set; } = "";
    public string Mode { get; set; } = "debug";
    public bool SameWindow { get; set; }
    public bool NoCenter { get; set; }
    /// <summary>Serial (or "all") to run the app on; without it, it asks when there are several devices.</summary>
    public string? Device { get; set; }
    /// <summary>"ip:port" address to connect or pair without asking.</summary>
    public string? Address { get; set; }
    public string? PairingCode { get; set; }
}

/// <summary>An open project: its configuration, its personal state and all the services that handle it.</summary>
public sealed class Session
{
    public static readonly IReadOnlyDictionary<ProjectComponent, string> Names = new Dictionary<ProjectComponent, string>
    {
        [ProjectComponent.Postgres] = "Postgres", [ProjectComponent.Api] = "API", [ProjectComponent.Web] = "Web", [ProjectComponent.Mobile] = "Móvil",
    };

    public Session(string root, StateStore state, IConsoleUi ui, SessionOptions? options = null)
    {
        Root = Paths.Normalize(root);
        State = state;
        Ui = ui;
        Op = options ?? new SessionOptions();
        Key = StateStore.Key(Root);
        Store = new ConfigStore(Root, state.ConfigFolder(Key));
        BuildMode = Op.Mode;

        Docker = new DockerService(this);
        Postgres = new PostgresService(this);
        Admin = new DockerAdmin(this);
        Free = new Proc.PortsService(this);
        Checker = new StackDev.Core.Diagnostics.DiagnosticsService(this);
        Services = new ServiceManager(this);
        Executor = new CommandExecutor(this);
        Conditions = new ConditionsService(this);
        Emulator = new EmulatorService(this);
        Devices = new DeviceService(this);
        Flutter = new FlutterService(this);
        All = new StartAllOrchestrator(this);
    }

    public string Root { get; }
    public string Key { get; }
    public StateStore State { get; }
    public ConfigStore Store { get; }
    public IConsoleUi Ui { get; set; }
    public SessionOptions Op { get; }
    public ProjectContext Ctx => Store.Context;

    /// <summary>Cancelled with Ctrl+C: it affects only the command that is running.</summary>
    public CancellationToken Ct { get; set; }

    /// <summary>If the interface has no console (the graphical window), the output of the commands run "here" arrives here.</summary>
    public Action<string>? Capture { get; set; }

    /// <summary>Runs showing the output: in the console, or in <see cref="Capture"/> if set.</summary>
    public int RunVisible(string exe, IEnumerable<string> args, string? cwd, IReadOnlyDictionary<string, string>? env) =>
        Capture is null
            ? Proc.ProcessRunner.RunInheriting(exe, args, cwd, env, Ct)
            : Proc.ProcessRunner.RunCapturing(exe, args, cwd, env, Ct, l => Capture(StackDev.Core.Services.ServiceManager.StripAnsi(l)));

    public string BuildMode { get; set; }
    public string? Avd { get; set; }

    public string? Sdk { get; private set; }
    public string? EmulatorExe { get; private set; }
    public string? AdbExe { get; private set; }
    public AdbClient? Adb { get; private set; }
    public IReadOnlyDictionary<string, string>? EnvAndroid { get; private set; }

    public DockerService Docker { get; }
    public DockerAdmin Admin { get; }
    public Proc.PortsService Free { get; }
    public StackDev.Core.Diagnostics.DiagnosticsService Checker { get; }
    public PostgresService Postgres { get; }
    public ServiceManager Services { get; }
    public CommandExecutor Executor { get; }
    public ConditionsService Conditions { get; }
    public EmulatorService Emulator { get; }
    public DeviceService Devices { get; }
    public FlutterService Flutter { get; }
    public StartAllOrchestrator All { get; }

    /// <summary>Loads (or creates through detection) dev-menu.json, prepares the Android SDK and picks the default emulator.</summary>
    public void Start()
    {
        if (!Store.Exists)
        {
            Ui.Write("Este proyecto aún no tiene configuración: la creo ahora.", Level.Info);
            Store.Save(new ConfigWizard(this).Create());
            Ui.Write($"\nGuardada en {Store.FilePath}", Level.Ok);
            Ui.Write("Revísala con la opción de configuración si algo no cuadra (comandos, puertos, tareas...).", Level.Muted);
        }
        Store.Load();
        if (Store.Error is not null)
        {
            Ui.Write($"dev-menu.json tiene un error: {Store.Error}", Level.Error);
            Ui.Write("Corrígelo; mientras tanto el menú queda vacío.", Level.Warning);
        }
        InitializeAndroid();
        Avd = State.ResolveAvd(Key, Op.Avd, Ctx.Config.Mobile?.Avd);
    }

    /// <summary>Reloads dev-menu.json if the user edited it. Returns true if it changed.</summary>
    public bool ReloadIfChanged()
    {
        if (!Store.ReloadIfChanged()) return false;
        InitializeAndroid();
        return true;
    }

    /// <summary>Saves a hand-edited configuration (leaves a .bak copy) and applies it without restarting.</summary>
    public void ApplyConfig(ProjectConfig fresh)
    {
        Store.Replace(fresh);
        InitializeAndroid();
        Avd = State.ResolveAvd(Key, Op.Avd, Ctx.Config.Mobile?.Avd);
    }

    /// <summary>Detects again (leaves a dev-menu.json.bak copy) and reloads.</summary>
    public void Redetect()
    {
        Store.Replace(new ConfigWizard(this).Create());
        InitializeAndroid();
        Avd = State.ResolveAvd(Key, Op.Avd, Ctx.Config.Mobile?.Avd);
        Ui.Write("Configuración regenerada (la anterior quedó en dev-menu.json.bak).", Level.Ok);
    }

    /// <summary>Opens dev-menu.json in VS Code or, failing that, in Notepad. When saved, the tool reloads it by itself.</summary>
    public void OpenConfigEditor()
    {
        var code = Proc.Shell.SearchInPath("code");
        try
        {
            if (code is not null)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(code, Util.Args.Quote(Store.FilePath)) { UseShellExecute = true, CreateNoWindow = true });
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", Util.Args.Quote(Store.FilePath)) { UseShellExecute = true });
            Ui.Write("Al guardar el archivo, la herramienta lo recarga sola.", Level.Muted);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Ui.Write($"No pude abrir el editor: {ex.Message}. El archivo está en {Store.FilePath}", Level.Error);
        }
    }

    private void InitializeAndroid()
    {
        Sdk = EmulatorExe = AdbExe = null;
        Adb = null;
        EnvAndroid = null;
        if (!Ctx.Has(ProjectComponent.Mobile)) return;

        string? flutterSettings = null;
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrEmpty(appData))
        {
            try { var f = Path.Combine(appData, ".flutter_settings"); if (File.Exists(f)) flutterSettings = File.ReadAllText(f); }
            catch (IOException) { }
        }
        var sdk = AndroidSdkLocator.Search(Ctx.Config.Mobile?.AndroidSdk, Environment.GetEnvironmentVariable, flutterSettings,
            Environment.GetEnvironmentVariable("LOCALAPPDATA"));
        if (sdk is null) return;

        // Only for the processes we launch (emulator, adb, flutter): the global variables are not touched
        Sdk = sdk;
        EnvAndroid = AndroidSdkLocator.ProcessVariables(sdk);
        EmulatorExe = AndroidSdkLocator.EmulatorExe(sdk);
        var adb = AndroidSdkLocator.AdbExe(sdk);
        if (!File.Exists(adb)) return;
        AdbExe = adb;
        Adb = new AdbClient(adb, EnvAndroid);
        Adb.StartServer();
    }

    /// <summary>Environment variables for a command in <paramref name="folder"/>: Android SDK + the API's if applicable.</summary>
    public IReadOnlyDictionary<string, string>? EnvFor(string folder)
    {
        var own = Ctx.EnvForFolder(folder);
        if (EnvAndroid is null) return own;
        if (own is null) return EnvAndroid;
        var res = new Dictionary<string, string>(EnvAndroid);
        foreach (var (k, v) in own) res[k] = v;
        return res;
    }

    public bool AssertComp(ProjectComponent c)
    {
        if (!Ctx.Has(c))
        {
            Ui.Write($"Este proyecto no tiene {Names[c]} configurado (edita dev-menu.json).", Level.Warning);
            return false;
        }
        if (c != ProjectComponent.Postgres && !Directory.Exists(Ctx.Folder(c)))
        {
            Ui.Write($"No existe la carpeta de {Names[c]}: {Ctx.Folder(c)}", Level.Error);
            return false;
        }
        return true;
    }

    /// <summary>A configuration command (api.test, web.build, mobile.test...), or null if it is not there.</summary>
    public string? Command(ProjectComponent c, string key)
    {
        string? v = c switch
        {
            ProjectComponent.Api => key switch { "iniciar" => Ctx.Config.Api?.Start, "test" => Ctx.Config.Api?.Test, "build" => Ctx.Config.Api?.Build, _ => null },
            ProjectComponent.Web => key switch { "iniciar" => Ctx.Config.Web?.Start, "test" => Ctx.Config.Web?.Test, "build" => Ctx.Config.Web?.Build, "instalar" => Ctx.Config.Web?.Install, _ => null },
            ProjectComponent.Mobile => key switch { "analizar" => Ctx.Config.Mobile?.Parse, "test" => Ctx.Config.Mobile?.Test, "formato" => Ctx.Config.Mobile?.Format, "generar" => Ctx.Config.Mobile?.Generate, _ => null },
            _ => null,
        };
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    public string Rel(string path) => Paths.Relative(Root, path);
}
