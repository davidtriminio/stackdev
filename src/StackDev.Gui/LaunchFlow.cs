using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using StackDev.Core.Config;
using StackDev.Core.Deploy;
using StackDev.Core.Servers;
using StackDev.Core.Ui;

namespace StackDev.Gui;

public enum LaunchMode { Start, Restart, Deploy }

/// <summary>
/// The work behind the "check and start" window: look at the server, try the API with its real settings, fix what is missing
/// (creating keys and the database, asking only for what nobody but the user knows), then start it or upload the new version.
/// Everything is asked and shown through <see cref="LaunchWindow"/>.
/// </summary>
public sealed class LaunchFlow
{
    public static readonly string[] StepNames = { "Servidor", "Configuración de la API", "Arranque", "Comprobación final" };

    private readonly Server _sv;
    private readonly string _service;
    private readonly string _dll;
    private readonly string? _domain;
    private readonly bool _postgresProject;
    private readonly LaunchMode _mode;
    private readonly DeployRequest? _request;
    private readonly Func<IEnumerable<GeneratedCredential>, bool> _saveCredentials;
    private readonly List<GeneratedCredential> _made = new();

    public LaunchFlow(Server sv, string service, string dll, string? domain, bool postgresProject, LaunchMode mode, DeployRequest? request,
        Func<IEnumerable<GeneratedCredential>, bool> saveCredentials)
    {
        _sv = sv; _service = service; _dll = dll; _domain = domain; _postgresProject = postgresProject;
        _mode = mode; _request = request; _saveCredentials = saveCredentials;
    }

    /// <summary>The public domain the server's web configuration has for this API (empty when none was found).</summary>
    public string? FoundDomain { get; private set; }

    /// <summary>Runs the flow; whatever goes wrong (an exception, a server that stops answering) ends in a failed step and a message, never a spinner.</summary>
    public LaunchOutcome Run(LaunchWindow win, CancellationToken ct)
    {
        try { return RunSteps(win, ct); }
        catch (OperationCanceledException)
        {
            win.FailRunningSteps("Cancelado");
            return new(false, "Cancelado.");
        }
        catch (Exception ex)
        {
            win.Info("Error inesperado: " + ex.Message, Level.Error);
            win.FailRunningSteps(ex.Message);
            return new(false, "Error inesperado: " + ex.Message);
        }
    }

    private LaunchOutcome RunSteps(LaunchWindow win, CancellationToken ct)
    {
        // ---- 1) the server and the service
        win.Step(0, StepState.Running);
        win.Info("Revisando el servidor…", Level.Title);
        win.Info($"Conectando con {_sv.User}@{_sv.Host}:{(_sv.Port is > 0 ? _sv.Port : 22)} por SSH (máximo {DeployService.PreflightLimitSec} s)…", Level.Muted);
        // The facts come one by one; showing them tells which one the server is stuck on if it ever stops answering.
        var pre = DeployService.Preflight(_sv, _service, ct, e =>
        {
            if (e.Kind == RemoteEventKind.Kv && e.Key is not (null or "deployed_content")) win.Info($"  {e.Key} = {e.Text}", Level.Muted);
            else DeployService.Relay(e, 0, 0, win.Report);
        });
        if (ct.IsCancellationRequested) { win.Step(0, StepState.Failed, "Cancelado"); return new(false, "Cancelado."); }
        if (!pre.Ok)
        {
            var why = pre.LaunchError ?? pre.FailMessage ?? "No se pudo consultar el servidor.";
            win.Info(why, Level.Error);
            win.Step(0, StepState.Failed, why);
            return new(false, "No pude revisar el servidor: " + why);
        }
        if (pre.Value("domain").Length > 0) FoundDomain = pre.Value("domain");
        if (pre.Value("service_exists") != "yes")
        {
            win.Step(0, StepState.Warning, $"«{_service}» todavía no existe en este servidor");
            var a = win.Ask($"La API «{_service}» todavía no existe en «{_sv.Name}».\n\nPrimero hay que crearla: reviso el servidor, preparo lo que falte y la creo.", "Crear la API ahora", "Cancelar");
            return a == 0 ? new(false, "Voy a crear la API.", CreateFirst: true) : new(false, "Cancelado.");
        }
        win.Step(0, StepState.Ok, $"{_sv.Name} responde y «{_service}» existe");

        // ---- 2) the settings of the API
        win.Step(1, StepState.Running);
        win.Info("Probando la API con su configuración real…", Level.Title);
        var check = DeployService.Check(_sv, _service, _dll, ct, e => DeployService.Relay(e, 0, 0, win.Report));
        if (ct.IsCancellationRequested) { win.Step(1, StepState.Failed, "Cancelado"); return new(false, "Cancelado."); }
        if (check.Ok) win.Step(1, StepState.Ok, "La API arranca con su configuración actual");
        else if (check.Outcome == DeployOutcome.NotReady && check.Message.Contains("Todavía no hay una API", StringComparison.Ordinal))
        {
            if (_mode != LaunchMode.Deploy)
            {
                win.Step(1, StepState.Failed, "Todavía no hay una API subida");
                return new(false, "Todavía no hay una API subida a este servicio. Usa «Subir API» primero.");
            }
            win.Step(1, StepState.Ok, "Primera subida: se probará al subir");
        }
        else if (check.Outcome == DeployOutcome.NotReady && check.Missing is not { Count: > 0 })
        {
            win.Step(1, StepState.Failed, "No arranca y no sé qué falta");
            return new(false, "La API no arranca con su configuración actual y no pude saber qué falta. Mira el registro de arriba (las líneas con «|») o «Logs en vivo». No se cambió nada.");
        }
        else if (check.Outcome == DeployOutcome.NotReady)
        {
            if (!Fix(win, check.Missing!, restart: _mode != LaunchMode.Deploy, ct, out var stopped))
                return new(false, stopped);
        }
        else
        {
            win.Step(1, StepState.Failed, check.Message);
            return new(false, check.Message);
        }

        // ---- 3) start, or upload the new version
        win.Step(2, StepState.Running);
        if (_mode == LaunchMode.Deploy)
        {
            if (_request is null) return new(false, "Falta el proyecto que se sube.");
            for (var attempt = 0; attempt < 4; attempt++)
            {
                win.Info($"Subiendo {_service} a {_sv.Name}…", Level.Title);
                var r = DeployService.Deploy(_request, new DeployDialogs.GuiDecisions(win), win.Report, ct);
                if (r.Ok) { win.Step(2, StepState.Ok, "Versión nueva en marcha"); break; }
                if (r.Outcome == DeployOutcome.AlreadyDeployed)
                {
                    win.Info(r.Message, Level.Ok);
                    if (pre.Value("active") != "active")
                    {
                        win.Info("Iniciando la API que ya estaba subida…", Level.Title);
                        var started = ServerService.Operation(_sv, _service, "start");
                        if (!started.Ok) { win.Step(2, StepState.Failed, started.Message); return new(false, started.Message); }
                    }
                    win.Step(2, StepState.Ok, "Ya estaba subida: no se volvió a subir");
                    break;
                }
                win.Info(r.Message, r.Outcome == DeployOutcome.Cancelled ? Level.Warning : Level.Error);
                var fixable = r.Missing is { Count: > 0 } && r.Outcome is DeployOutcome.NotReady or DeployOutcome.RolledBack or DeployOutcome.RollbackFailed or DeployOutcome.StartFailed;
                if (fixable && attempt < 3)
                {
                    win.Step(2, StepState.Warning, "Faltan variables: las completo y vuelvo a subir");
                    if (!Fix(win, r.Missing!, restart: false, ct, out var why)) return new(false, why);
                    win.Step(2, StepState.Running);
                    continue;
                }
                win.Step(2, StepState.Failed, r.Message);
                return new(false, r.Message);
            }
        }
        else
        {
            var action = _mode == LaunchMode.Restart ? "restart" : "start";
            win.Info(_mode == LaunchMode.Restart ? "Reiniciando la API…" : "Iniciando la API…", Level.Title);
            var r = ServerService.Operation(_sv, _service, action);
            if (!r.Ok)
            {
                win.Step(2, StepState.Failed, r.Message);
                return new(false, r.Message);
            }
            win.Step(2, StepState.Ok, r.Message);
        }

        // ---- 4) where it runs
        win.Step(3, StepState.Running);
        win.Info("Comprobando que responde…", Level.Title);
        ApiStatus? st = null;
        try { st = ServerService.State(_sv, _service, _domain); } catch (ArgumentException) { }
        var url = string.IsNullOrEmpty(_domain) ? null : "https://" + _domain;
        if (st is null || st.Problem is not null || !st.Active)
        {
            win.Step(3, StepState.Warning, st?.Problem ?? "El servicio no figura activo todavía");
            return new(false, "La API se inició pero no la veo activa todavía. Pulsa «Actualizar estado» en unos segundos o mira «Logs en vivo».", url);
        }
        win.Step(3, StepState.Ok, url is null ? $"Activa en {_sv.Name}" : $"Activa: {url}");
        var where = url is null ? $"en {_sv.Name} ({_sv.Host})" : $"en {url}, servidor {_sv.Name} ({_sv.Host})";
        var tail = _made.Count > 0 ? $"\n\nGuardé {_made.Count} clave(s) en «Credenciales» (cifradas en este equipo)." : "";
        return new(true, $"La API «{_service}» está corriendo {where}." + tail, url);
    }

    /// <summary>
    /// Completes the settings the API needs. Keys are generated, a missing PostgreSQL connection gets a database of its own,
    /// and the values only the user knows are asked in the window. False (with the reason) when the user stops or it cannot be done.
    /// </summary>
    private bool Fix(LaunchWindow win, IReadOnlyList<string> missing, bool restart, CancellationToken ct, out string stopped)
    {
        stopped = "";
        win.Step(1, StepState.Warning, "Faltan variables: " + string.Join(", ", missing));
        string Label(string n) => MissingSettings.CanGenerate(n) ? "la genero"
            : MissingSettings.CanCreateDatabase(n, _postgresProject) ? "creo una base PostgreSQL propia" : "me la das tú";
        if (missing.Count > 0)
        {
            var list = string.Join("\n", missing.Select(m => $"  • {m}  ({Label(m)})"));
            var a = win.Ask($"La API «{_service}» no arranca porque le faltan estas variables:\n\n{list}\n\nLas que puedo resolver las creo y las guardo en «Credenciales». ¿Sigo?", "Arreglar", "Cancelar");
            if (a != 0) { stopped = "Cancelado. No se cambió nada en el servidor."; win.Step(1, StepState.Failed, "Cancelado"); return false; }
        }

        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> queue = missing;
        for (var round = 0; round < 6; round++)
        {
            var fresh = queue.Where(tried.Add).ToList();
            if (fresh.Count == 0) break;
            win.Info("Leyendo las variables del servidor…", Level.Title);
            var read = EnvService.Read(_sv, _service, ct);
            if (!read.Ok || read.File is null) { stopped = "No pude leer el .env del servidor: " + read.Message; win.Step(1, StepState.Failed, read.Message); return false; }
            var env = read.File;
            var manual = new List<string>();
            var changed = false;
            foreach (var n in fresh)
            {
                if (!string.IsNullOrEmpty(env.Get(n))) continue;
                if (MissingSettings.CanGenerate(n))
                {
                    env.Set(n, MissingSettings.RandomSecret());
                    _made.Add(new($"{_service} @ {_sv.Name} · {n}", "", env.Get(n)!, $"Variable {n}, creada automáticamente porque la API no arrancaba sin ella."));
                    win.Info($"Creé {n}.", Level.Ok);
                    changed = true;
                }
                else if (MissingSettings.CanCreateDatabase(n, _postgresProject))
                {
                    win.Info("Creando la base de datos propia de esta API…", Level.Title);
                    var db = DatabaseService.Create(_sv, _service, win.Report, ct);
                    if (!db.Ok) { win.Info(db.Message, Level.Error); manual.Add(n); continue; }
                    env.Set(n, db.Connection!);
                    _made.Add(new($"{_service} @ {_sv.Name} · base de datos", db.User!, db.Password!, $"PostgreSQL en el servidor, base «{db.Database}» (variable {n})."));
                    win.Info($"Base «{db.Database}» lista.", Level.Ok);
                    changed = true;
                }
                else manual.Add(n);
            }
            if (manual.Count > 0)
            {
                var values = win.AskValues($"Estos valores solo los puedes dar tú. Escríbelos aquí y los guardo en el .env del servidor de «{_service}».",
                    manual, MissingSettings.Hint, "Guardar", "Cancelar");
                if (values is null)
                {
                    if (_made.Count > 0) _saveCredentials(_made);
                    stopped = "Quedó pendiente: " + string.Join(", ", manual) + ". Puedes ponerlos en «Variables (.env)».";
                    win.Step(1, StepState.Failed, "Faltan " + string.Join(", ", manual));
                    return false;
                }
                foreach (var (name, value) in values)
                {
                    env.Set(name, value);
                    if (SecretMask.IsSecretName(name)) _made.Add(new($"{_service} @ {_sv.Name} · {name}", "", value, $"Variable {name}, escrita por ti."));
                    changed = true;
                }
            }
            if (!changed) break;

            win.Info("Guardando las variables en el servidor…", Level.Title);
            var write = EnvService.Write(_sv, _service, env, restart, win.Report, ct);
            if (write.Ok) break;
            if (write.Outcome != DeployOutcome.StartFailed)
            {
                if (_made.Count > 0) _saveCredentials(_made);
                stopped = write.Message;
                win.Step(1, StepState.Failed, write.Message);
                return false;
            }
            win.Info("Todavía no arranca: " + (write.Missing is { Count: > 0 } ? "falta " + string.Join(", ", write.Missing) : "reviso qué más pide."), Level.Warning);
            queue = write.Missing ?? Array.Empty<string>();
            if (queue.Count == 0) break;
        }

        if (_made.Count > 0) _saveCredentials(_made);
        if (_made.Any(g => g.Name.Contains("Webhook", StringComparison.OrdinalIgnoreCase)))
            win.Info("Ojo: el secreto del webhook de pagos es inventado. Cuando conectes el proveedor de pagos, pon el valor real en «Variables (.env)».", Level.Warning);
        win.Step(1, StepState.Ok, _made.Count > 0 ? $"Completé la configuración ({_made.Count} clave(s) nuevas guardadas en «Credenciales»)" : "Configuración completa");
        return true;
    }
}
