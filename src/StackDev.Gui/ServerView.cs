using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StackDev.Core;
using StackDev.Core.Config;
using StackDev.Core.Deploy;
using StackDev.Core.Servers;
using StackDev.Core.Ui;
using StackDev.Core.Util;

namespace StackDev.Gui;

/// <summary>
/// «Servidor»: lista global de servidores (cifrada, fuera del repo), prueba de conexión SSH,
/// estado del servicio de la API de este proyecto y logs en vivo en una pestaña de terminal.
/// </summary>
public sealed class ServerView : ScrollViewer
{
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    private readonly Window _owner;
    private readonly Func<Session?> _session;
    private readonly Func<ServerStore> _storeFactory;
    private ServerStore _store => _storeFactory();
    private readonly Action<string, string> _terminal;
    private readonly Action<DeploymentConfig?> _saveDeployment;
    private readonly Action<string, Level> _log;
    private readonly StackPanel _root = new() { Margin = new Thickness(0, 0, 0, 24) };
    private readonly Dictionary<string, string> _probes = new();
    private readonly Dictionary<string, bool> _trusted = new();
    private ApiStatus? _state;
    private string _stateOf = "";
    private bool _querying;

    public ServerView(Window owner, Func<Session?> session, Func<ServerStore> store,
        Action<string, string> terminal, Action<DeploymentConfig?> saveDeployment, Action<string, Level> log)
    {
        _owner = owner;
        _session = session;
        _storeFactory = store;
        _terminal = terminal;
        _saveDeployment = saveDeployment;
        _log = log;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Content = _root;
    }

    private static Brush P(string key) => (Brush)Application.Current.FindResource(key);
    private static Style GetStyle(string key) => (Style)Application.Current.FindResource(key);

    /// <summary>Se llama al abrir la sección y después de cada cambio.</summary>
    public void Update()
    {
        foreach (var sv in _store.Read()) _trusted[sv.Id] = HostKeys.IsTrusted(sv);
        Draw();
        _ = QueryStatusAsync();
    }

    // ------------------------------------------------------------ piezas

    private static TextBlock Text(string t, double size = 13, Brush? color = null, bool mono = false, FontWeight? weight = null) => new()
    {
        Text = t,
        FontSize = size,
        Foreground = color ?? P("TextBrush"),
        FontFamily = mono ? new FontFamily("Cascadia Mono, Consolas") : new FontFamily("Segoe UI"),
        FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap,
    };

    private static Button Button(string text, Action action, bool primary = false, bool danger = false)
    {
        var b = new Button { Content = text, Style = GetStyle(primary ? "BtnPrimary" : danger ? "BtnDanger" : "BtnChip") };
        b.Click += (_, _) => action();
        return b;
    }

    private static Border Card(string glyph, string colorGlyph, string title, string? subtitle, UIElement body, UIElement? right = null)
    {
        var tile = new Border
        {
            Style = GetStyle("GlyphTile"),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorGlyph)) { Opacity = 0.14 },
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily(IconFont),
                FontSize = 16,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorGlyph)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var cab = new Grid();
        cab.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cab.ColumnDefinitions.Add(new ColumnDefinition());
        cab.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tile.Margin = new Thickness(0, 0, 12, 0);
        cab.Children.Add(tile);
        var tit = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        tit.Children.Add(Text(title, 15, null, false, FontWeights.SemiBold));
        if (subtitle is not null) tit.Children.Add(Text(subtitle, 11.5, P("MutedBrush"), true));
        Grid.SetColumn(tit, 1);
        cab.Children.Add(tit);
        if (right is not null) { Grid.SetColumn(right, 2); cab.Children.Add(right); }

        var stack = new StackPanel();
        stack.Children.Add(cab);
        if (body is FrameworkElement fe) fe.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(body);
        return new Border { Style = GetStyle("Card"), Child = stack, Margin = new Thickness(0, 0, 0, 12) };
    }

    private static Border Chip(string text, Brush color) => new()
    {
        Background = new SolidColorBrush(((SolidColorBrush)color).Color) { Opacity = 0.16 },
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(8, 2, 8, 2),
        Margin = new Thickness(0, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = Text(text, 11, color, true),
    };

    // ------------------------------------------------------------ dibujo

    private void Draw()
    {
        _root.Children.Clear();
        var servers = _store.Read();
        var s = _session();
        var depl = s?.Ctx.Config.Deployment;

        // --- Este proyecto
        if (s is not null)
            _root.Children.Add(ProjectCard(s, servers, depl));

        // --- Servidores
        var header = new Grid { Margin = new Thickness(0, 6, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { Text = "SERVIDORES", Style = GetStyle("Label"), VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(title);
        var add = Button("Agregar servidor", AddServer, primary: true);
        Grid.SetColumn(add, 1);
        header.Children.Add(add);
        _root.Children.Add(header);

        if (servers.Count == 0)
        {
            _root.Children.Add(Card("\uE753", "#6FA8FF", "Aún no tienes servidores",
                "se guardan cifrados, fuera del repositorio",
                Text("Agrega el primero con su IP, usuario y clave SSH. Puedes tener varios (Oracle, VPS…) y cada proyecto elige a cuál se sube.", 12.5, P("MutedBrush"))));
            return;
        }
        foreach (var sv in servers) _root.Children.Add(ServerCard(sv));
    }

    private UIElement ServerCard(Server sv)
    {
        var body = new StackPanel();
        _probes.TryGetValue(sv.Id, out var test);
        if (!string.IsNullOrEmpty(test))
        {
            var ok = test.StartsWith("✔");
            var t = Text(test, 12, ok ? P("OkBrush") : P("WarnBrush"));
            t.Margin = new Thickness(0, 0, 0, 10);
            body.Children.Add(t);
        }
        var buttons = new WrapPanel();
        var verified = _trusted.TryGetValue(sv.Id, out var isTrusted) && isTrusted;
        buttons.Children.Add(Button(verified ? "Probar conexión" : "Verificar y probar", () => _ = TestAsync(sv), primary: true));
        if (verified) buttons.Children.Add(Button("Terminal SSH", () => _terminal($"ssh {sv.Name}", Ssh.TerminalLine(sv, ""))));
        if (verified) buttons.Children.Add(Button("Crear API nueva", () => _ = ProvisionApiAsync(sv)));
        buttons.Children.Add(Button("Editar", () => EditServer(sv)));
        if (verified) buttons.Children.Add(Button("Quitar huella", () => ForgetHostKey(sv)));
        buttons.Children.Add(Button("Eliminar", () => DeleteServer(sv), danger: true));
        body.Children.Add(buttons);

        var chips = new StackPanel { Orientation = Orientation.Horizontal };
        chips.Children.Add(Chip(verified ? "huella verificada" : "sin verificar", verified ? P("OkBrush") : P("WarnBrush")));
        chips.Children.Add(Chip(sv.WebName, P("AccentBrush")));
        return Card("\uE753", "#6FA8FF", sv.Name, sv.Destination, body, chips);
    }

    private UIElement ProjectCard(Session s, List<Server> servers, DeploymentConfig? depl)
    {
        var name = s.Ctx.Name;
        var sv = servers.FirstOrDefault(x => x.Id == depl?.Server);
        var body = new StackPanel();

        if (depl is null || sv is null)
        {
            body.Children.Add(Text(depl is null
                ? "Este proyecto aún no sube a ningún servidor. El asistente busca tu API, revisa qué APIs ya hay en el servidor y, si no hay ninguna, te ayuda a crear una."
                : "El servidor que usaba este proyecto ya no está en la lista. Elige otro.", 12.5, P("MutedBrush")));
            var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            row.Children.Add(Button("Subir al servidor (asistente)", () => _ = GuidedSetupAsync(servers, depl), primary: true));
            row.Children.Add(Button("Configurar a mano", () => ConfigureDeployment(servers, depl)));
            body.Children.Add(row);
            return Card("\uE8B7", "#6EE787", $"Este proyecto · {name}", null, body);
        }

        var data = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        data.Children.Add(Chip(sv.Name, P("AccentBrush")));
        data.Children.Add(Chip(depl.Service ?? "?", P("MutedBrush")));
        if (!string.IsNullOrEmpty(depl.Domain)) data.Children.Add(Chip(depl.Domain!, P("MutedBrush")));
        body.Children.Add(data);

        body.Children.Add(StatusPanel());

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var svc = depl.Service ?? "";
        buttons.Children.Add(Button("Subir API", () => _ = DeployApiAsync(sv, depl), primary: true));
        buttons.Children.Add(Button("Logs en vivo", () => _terminal($"logs {svc}", Ssh.TerminalLine(sv, $"sudo journalctl -u {svc} -f -n 100 --no-pager"))));
        buttons.Children.Add(Button("Variables (.env)", () => _ = EditVariablesAsync(sv, svc)));
        buttons.Children.Add(Button("Versiones anteriores", () => _ = VersionsAsync(sv, svc)));
        buttons.Children.Add(Button("Actualizar estado", () => _ = QueryStatusAsync(force: true)));
        buttons.Children.Add(Button("Reiniciar", () => _ = GuardedStartAsync(sv, depl, "restart", "reiniciar")));
        buttons.Children.Add(Button("Detener", () => _ = ActionAsync(sv, svc, "stop", "detener")));
        buttons.Children.Add(Button("Iniciar", () => _ = GuardedStartAsync(sv, depl, "start", "iniciar")));
        buttons.Children.Add(Button("Cambiar de API o servidor", () => _ = GuidedSetupAsync(servers, depl)));
        buttons.Children.Add(Button("Editar a mano", () => ConfigureDeployment(servers, depl)));
        body.Children.Add(buttons);

        return Card("\uE8B7", "#6EE787", $"Este proyecto · {name}", $"{sv.Destination}  ·  {sv.WebName}", body);
    }

    private UIElement StatusPanel()
    {
        var frame = new Border { Background = P("Surface2Brush"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10) };
        var stack = new StackPanel();
        frame.Child = stack;

        if (_querying && _state is null)
        {
            stack.Children.Add(Text("Consultando el servidor…", 12.5, P("MutedBrush"), true));
            return frame;
        }
        if (_state is null)
        {
            stack.Children.Add(Text("Sin consultar todavía.", 12.5, P("MutedBrush"), true));
            return frame;
        }
        var e = _state;
        if (e.Problem is not null)
        {
            stack.Children.Add(Text("✘ " + e.Problem, 12.5, P("ErrBrush")));
            return frame;
        }
        var color = e.Active ? P("OkBrush") : P("ErrBrush");
        var line = new WrapPanel();
        line.Children.Add(Chip((e.Active ? "● " : "○ ") + e.State, color));
        if (!string.IsNullOrEmpty(e.From)) line.Children.Add(Chip("desde " + e.From, P("MutedBrush")));
        if (e.Http is int h) line.Children.Add(Chip("HTTPS " + h, h is >= 200 and < 400 ? P("OkBrush") : P("WarnBrush")));
        if (e.CertExpires is DateTime v)
        {
            var days = (int)Math.Floor((v - DateTime.Now).TotalDays);
            line.Children.Add(Chip($"certificado: {days} días", days < 15 ? P("WarnBrush") : P("OkBrush")));
        }
        stack.Children.Add(line);
        if (!string.IsNullOrWhiteSpace(e.Errors))
        {
            var t = Text("Últimos errores del servicio:", 11.5, P("WarnBrush"));
            t.Margin = new Thickness(0, 8, 0, 4);
            stack.Children.Add(t);
            stack.Children.Add(Text(e.Errors.Trim(), 11.5, P("MutedBrush"), true));
        }
        return frame;
    }

    // ------------------------------------------------------------ acciones

    private async Task QueryStatusAsync(bool force = false)
    {
        var s = _session();
        var depl = s?.Ctx.Config.Deployment;
        var sv = _store.Search(depl?.Server);
        if (s is null || depl is null || sv is null || string.IsNullOrEmpty(depl.Service) || _querying) return;
        var key = s.Root + "|" + sv.Id + "|" + depl.Service;
        if (!force && _state is not null && _stateOf == key) return;

        _querying = true;
        if (_stateOf != key) _state = null;
        Draw();
        var service = depl.Service;
        var domain = depl.Domain;
        var r = await Task.Run(() => ServerService.State(sv, service, domain));
        _state = r;
        _stateOf = key;
        _querying = false;
        Draw();
    }

    /// <summary>Probes the server (asking to confirm its fingerprint the first time). Null when the user declines the fingerprint.</summary>
    private async Task<SshResult?> TestAsync(Server sv)
    {
        _probes[sv.Id] = "Conectando…";
        Draw();
        var r = await Task.Run(() => ServerService.Test(sv));
        if (r.Untrusted is { } key)
        {
            if (DeployDialogs.TrustHostKey(_owner, sv, key))
            {
                HostKeys.Trust(sv, key);
                _trusted[sv.Id] = true;
                _log($"Huella de {sv.Name} verificada ({key.Fingerprint}).", Level.Ok);
                _probes[sv.Id] = "Conectando…";
                Draw();
                r = await Task.Run(() => ServerService.Test(sv));
            }
            else
            {
                _probes[sv.Id] = "Sin verificar: no se conectó.";
                Draw();
                return null;
            }
        }
        _probes[sv.Id] = (r.Ok ? "✔ " : "✘ ") + r.Message;
        Draw();
        return r;
    }

    private bool IsTrusted(Server sv) => _trusted.TryGetValue(sv.Id, out var t) && t;

    private void ForgetHostKey(Server sv)
    {
        if (!Dialogs.Confirm(_owner, $"¿Quitar la huella guardada de «{sv.Name}»? La próxima conexión pedirá verificarla de nuevo.", false)) return;
        HostKeys.Forget(sv);
        _trusted[sv.Id] = false;
        _probes.Remove(sv.Id);
        Draw();
    }

    /// <summary>
    /// Start and restart only go ahead after the application was tried with its real settings; otherwise the missing pieces are
    /// fixed first, instead of leaving the service crash-looping.
    /// </summary>
    private async Task GuardedStartAsync(Server sv, DeploymentConfig depl, string action, string verb)
    {
        var service = depl.Service ?? "";
        if (string.IsNullOrEmpty(depl.Dll))
        {
            await ActionAsync(sv, service, action, verb);
            return;
        }
        _log($"Probando que {service} arranca antes de {verb}la…", Level.Info);
        var check = await Task.Run(() => DeployService.Check(sv, service, depl.Dll!));
        if (check.Ok) { await ActionAsync(sv, service, action, verb); return; }
        _log("⚠ " + check.Message, Level.Warning);
        if (check.Outcome == DeployOutcome.NotReady && check.Missing is { Count: > 0 } missing)
        {
            await FixMissingSettingsAsync(sv, depl, missing, DeployOutcome.StartFailed);
            return;
        }
        if (check.Outcome == DeployOutcome.NotReady && check.Message.Contains("Todavía no hay una API", StringComparison.Ordinal))
        {
            Dialogs.Confirm(_owner, "Todavía no hay una API subida a este servicio. Pulsa «Subir API» primero.", true);
            return;
        }
        var did = verb == "iniciar" ? "inicié" : "reinicié";
        Dialogs.Confirm(_owner, $"No {did} «{service}» porque no arranca con su configuración actual.\n\n{check.Message}\n\nAbre «Variables (.env)» o mira «Logs en vivo» para ver el motivo.", true);
    }

    private async Task ActionAsync(Server sv, string service, string action, string verb)
    {
        _log($"Servidor: {verb} {service} en {sv.Name}…", Level.Info);
        var r = await Task.Run(() => ServerService.Operation(sv, service, action));
        _log(r.Ok ? $"✔ {r.Message}" : $"✘ {r.Message}", r.Ok ? Level.Ok : Level.Error);
        await QueryStatusAsync(force: true);
    }

    private void AddServer()
    {
        var fresh = ServerDialogs.EditServer(_owner, null);
        if (fresh is null) return;
        var list = _store.Read();
        list.Add(fresh);
        _store.Save(list);
        Update();
    }

    private void EditServer(Server sv)
    {
        var edited = ServerDialogs.EditServer(_owner, sv);
        if (edited is null) return;
        var list = _store.Read();
        var i = list.FindIndex(x => x.Id == sv.Id);
        if (i >= 0) list[i] = edited;
        _store.Save(list);
        _probes.Remove(sv.Id);
        _stateOf = "";
        Update();
    }

    private void DeleteServer(Server sv)
    {
        if (!Dialogs.Confirm(_owner, $"¿Quitar «{sv.Name}» de la lista? No toca nada en el servidor.", false)) return;
        _store.Save(_store.Read().Where(x => x.Id != sv.Id));
        _probes.Remove(sv.Id);
        Update();
    }

    private void ConfigureDeployment(List<Server> servers, DeploymentConfig? current)
    {
        var s = _session();
        if (s is null) return;
        var api = PickApi(s, current?.Project);
        var suggestion = api?.SuggestedService;
        if (suggestion is null)
        {
            suggestion = new string(s.Ctx.Name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : '-').ToArray()).Trim('-');
            if (suggestion.Length == 0 || !char.IsLetter(suggestion[0])) suggestion = "api";
            suggestion += "-api";
        }
        var fresh = ServerDialogs.EditDeployment(_owner, servers, current, suggestion, api?.Dll ?? "");
        if (fresh is null) return;
        fresh.Project = api is null ? current?.Project : Paths.Relative(s.Root, api.ProjectPath);
        _saveDeployment(fresh);
        _state = null;
        _stateOf = "";
        Update();
    }

    // ------------------------------------------------------------ guided: pick the server, find the API, link or create

    private static bool LooksLikeApi(RemoteApi remote, ApiCandidate local) =>
        string.Equals(remote.Dll, local.Dll, StringComparison.OrdinalIgnoreCase)
        || string.Equals(remote.Service, local.SuggestedService, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// «Subir al servidor»: choose the server, confirm its fingerprint, find this project's API, look at what is already installed
    /// on the server and either link to one of those or help create a new one.
    /// </summary>
    private async Task GuidedSetupAsync(List<Server> servers, DeploymentConfig? current)
    {
        var s = _session();
        if (s is null) return;
        if (servers.Count == 0)
        {
            if (Dialogs.Confirm(_owner, "Primero necesitas un servidor. ¿Lo agrego ahora?", true)) AddServer();
            return;
        }

        var index = Math.Max(0, servers.FindIndex(x => x.Id == current?.Server));
        if (servers.Count > 1)
        {
            index = Dialogs.Choose(_owner, "¿A qué servidor quieres subir este proyecto?",
                servers.Select(x => $"{x.Name}   ·   {x.Destination}").ToList(), index);
            if (index < 0) return;
        }
        var sv = servers[index];

        if (!IsTrusted(sv))
        {
            _log($"Verificando {sv.Name}…", Level.Info);
            var test = await TestAsync(sv);
            if (test is null || !IsTrusted(sv)) return;
            if (!test.Ok)
            {
                Dialogs.Confirm(_owner, $"No pude conectar con «{sv.Name}»:\n{test.Message}\n\nCorrige los datos del servidor (botón «Editar») y vuelve a intentarlo.", true);
                return;
            }
        }

        var api = PickApi(s, current?.Project);
        if (api is null)
        {
            Dialogs.Confirm(_owner, "No encontré un proyecto web (ASP.NET Core) en esta carpeta, así que no hay una API que subir. Revisa la sección «api» de dev-menu.json.", true);
            return;
        }

        _log($"Buscando APIs ya instaladas en {sv.Name}…", Level.Info);
        string? err = null;
        var found = await Task.Run(() => DeployService.DiscoverApis(sv, out err));
        if (err is not null)
        {
            _log("✘ " + err, Level.Error);
            Dialogs.Confirm(_owner, "No pude consultar el servidor:\n" + err, true);
            return;
        }

        if (found.Count == 0)
        {
            if (!Dialogs.Confirm(_owner,
                    $"En «{sv.Name}» no hay ninguna API .NET instalada todavía.\n\nTe ayudo a crear una para «{api.ProjectName}»: se preparan el usuario, la carpeta, el servicio, el proxy {sv.WebName} y el HTTPS. Después subimos tu código. ¿Continúo?", true))
                return;
            await ProvisionApiAsync(sv, deployAfter: true);
            return;
        }

        var ordered = found.OrderByDescending(a => LooksLikeApi(a, api)).ThenBy(a => a.Service).ToList();
        var labels = ordered.Select(a => $"{(LooksLikeApi(a, api) ? "★ " : "")}{a.Service}   ·   {(a.Dll.Length > 0 ? a.Dll : "?")}   ·   {a.State}").ToList();
        labels.Add("➕  Crear una API nueva en este servidor");
        var pick = Dialogs.Choose(_owner, $"APIs instaladas en {sv.Name}. ¿A cuál se sube «{api.ProjectName}»?  (★ = parece ser esta)", labels, 0);
        if (pick < 0) return;
        if (pick == ordered.Count) { await ProvisionApiAsync(sv, deployAfter: true); return; }

        var chosen = ordered[pick];
        if (!LooksLikeApi(chosen, api) && !Dialogs.Confirm(_owner,
                $"«{chosen.Service}» ejecuta {(chosen.Dll.Length > 0 ? chosen.Dll : "otra aplicación")}, y tu proyecto genera {api.Dll}.\n\nSi subes, el contenido de ese servicio se reemplaza por esta API (queda una copia para volver atrás). ¿Es la misma API?", false))
            return;

        var cfg = new DeploymentConfig
        {
            Server = sv.Id,
            Service = chosen.Service,
            Dll = chosen.Dll.Length > 0 ? chosen.Dll : api.Dll,
            Domain = current?.Domain,
            Project = Paths.Relative(s.Root, api.ProjectPath),
            Extra = current?.Extra,
        };
        _saveDeployment(cfg);
        _state = null;
        _stateOf = "";
        Update();
        _log($"Este proyecto sube ahora a «{chosen.Service}» en {sv.Name}.", Level.Ok);
        if (Dialogs.Confirm(_owner, $"Listo: este proyecto sube a «{chosen.Service}» en {sv.Name}. ¿Subo la API ahora?", true))
            await DeployApiAsync(sv, cfg);
    }

    // ------------------------------------------------------------ finding the API of this project

    /// <summary>The web project to deploy: the one saved in the config, or found by looking (asking when there are several).</summary>
    private ApiCandidate? PickApi(Session s, string? saved)
    {
        if (!string.IsNullOrEmpty(saved))
        {
            var abs = Paths.Resolve(s.Root, saved);
            if (File.Exists(abs) && ApiDetector.Analyze(abs) is { } known) return known;
        }
        var root = s.Ctx.Has(ProjectComponent.Api) ? s.Ctx.Folder(ProjectComponent.Api) : s.Root;
        var all = ApiDetector.Find(root);
        if (all.Count == 0 && !string.Equals(root, s.Root, StringComparison.OrdinalIgnoreCase)) all = ApiDetector.Find(s.Root);
        if (all.Count == 0) return null;
        if (all.Count == 1) return all[0];
        var i = Dialogs.Choose(_owner, "Hay varios proyectos web. ¿Cuál es la API principal?",
            all.Select(c => $"{c.ProjectName}   ·   {Paths.Relative(s.Root, c.ProjectPath)}").ToList(), 0);
        return i < 0 ? null : all[i];
    }

    // ------------------------------------------------------------ deploy

    private static string Describe(DeployResult r) => r.Outcome switch
    {
        DeployOutcome.Success => "Despliegue terminado.",
        DeployOutcome.RolledBack => "↩ " + r.Message,
        DeployOutcome.RollbackFailed => "‼ " + r.Message,
        DeployOutcome.StartFailed => "⚠ " + r.Message,
        DeployOutcome.NotReady => "⚠ " + r.Message,
        DeployOutcome.NeedsProvision => r.Message,
        DeployOutcome.Cancelled => "Cancelado: " + r.Message,
        _ => r.Message,
    };

    private async Task DeployApiAsync(Server sv, DeploymentConfig depl)
    {
        var s = _session();
        if (s is null || string.IsNullOrEmpty(depl.Service)) return;
        if (!IsTrusted(sv) && (await TestAsync(sv) is null || !IsTrusted(sv))) return;
        var api = PickApi(s, depl.Project);
        if (api is null)
        {
            Dialogs.Confirm(_owner, "No encontré un proyecto web (ASP.NET Core) en esta carpeta. Revisa la sección «api» de dev-menu.json.", true);
            return;
        }
        var project = Paths.Relative(s.Root, api.ProjectPath);
        var dll = string.IsNullOrEmpty(depl.Dll) ? api.Dll : depl.Dll!;
        if (depl.Project != project || string.IsNullOrEmpty(depl.Dll))
        {
            depl.Project = project;
            depl.Dll = dll;
            _saveDeployment(depl);
        }
        _log($"Revisando {sv.Name} antes de compilar…", Level.Info);
        var check = await Task.Run(() => DeployService.Preflight(sv, depl.Service!));
        if (!check.Ok)
        {
            var why = check.LaunchError ?? check.FailMessage ?? "No se pudo consultar el servidor.";
            _log("✘ " + why, Level.Error);
            Dialogs.Confirm(_owner, "No pude revisar el servidor:\n" + why, true);
            return;
        }
        if (check.Value("service_exists") != "yes")
        {
            if (Dialogs.Confirm(_owner, $"La API «{depl.Service}» todavía no existe en «{sv.Name}».\n\n¿La creo ahora? Reviso el servidor, preparo lo que falte, la creo y después subo tu código.", true))
                await ProvisionApiAsync(sv, deployAfter: true);
            return;
        }
        var req = new DeployRequest { Server = sv, Project = api.ProjectPath, Service = depl.Service!, Dll = dll, Domain = depl.Domain };

        _log($"Subiendo {depl.Service} a {sv.Name}…", Level.Info);
        var result = OperationWindow.Run(_owner, $"Subir {depl.Service} a {sv.Name}", canCancel: true,
            (win, report, ct) => DeployService.Deploy(req, new DeployDialogs.GuiDecisions(win), report, ct),
            r => (r.Ok, Describe(r)));
        if (result is null) return;
        _log(Describe(result), result.Ok ? Level.Ok : result.Outcome is DeployOutcome.Cancelled or DeployOutcome.RolledBack or DeployOutcome.StartFailed or DeployOutcome.NotReady ? Level.Warning : Level.Error);
        _state = null;
        await QueryStatusAsync(force: true);

        if (result.Missing is { Count: > 0 } missing && result.Outcome is DeployOutcome.StartFailed or DeployOutcome.RolledBack or DeployOutcome.RollbackFailed or DeployOutcome.NotReady)
        {
            await FixMissingSettingsAsync(sv, depl, missing, result.Outcome);
            return;
        }
        if (result.Outcome == DeployOutcome.NotReady)
        {
            Dialogs.Confirm(_owner, "La versión nueva no arranca con la configuración actual del servidor, y no pude saber qué falta.\n\nNo se cambió nada: la versión que estaba sigue como estaba.\nRevisa el registro de la operación (las líneas con «|» son lo que dijo la API) o abre «Variables (.env)».", true);
            return;
        }
        if (result.Outcome == DeployOutcome.StartFailed)
        {
            Dialogs.Confirm(_owner, "Los archivos quedaron en el servidor, pero el servicio no arrancó y no pude saber por qué.\n\nRevisa el registro de la operación (las líneas con «|» son lo que dijo el servicio) o abre «Variables (.env)» para ver qué falta.", true);
            return;
        }
        if (result.Outcome == DeployOutcome.NeedsProvision && Dialogs.Confirm(_owner, $"La API «{depl.Service}» no existe en {sv.Name}. ¿La creo ahora y la subo después?", true))
            await ProvisionApiAsync(sv, deployAfter: true);
    }

    /// <summary>Keeps generated keys in the project's Credentials (encrypted on this computer). False when they could not be saved.</summary>
    private bool SaveCredentials(IEnumerable<GeneratedCredential> items)
    {
        var s = _session();
        if (s is null) return false;
        try
        {
            new StackDev.Core.Credentials.CredentialsStore(s.State.ConfigFolder(s.Key)).Upsert(
                items.Select(c => new StackDev.Core.Credentials.Credential { Name = c.Name, User = c.User, Key = c.Key, Note = c.Note }));
            _log($"Guardé {items.Count()} clave(s) en «Credenciales».", Level.Ok);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log("⚠ No pude guardar las claves en «Credenciales»: " + ex.Message, Level.Warning);
            return false;
        }
    }

    /// <summary>
    /// The API refused to start because some settings are missing. Offers to create them (random values for keys and secrets),
    /// restart the service, and upload again when the failed upload had been undone.
    /// </summary>
    private async Task FixMissingSettingsAsync(Server sv, DeploymentConfig depl, IReadOnlyList<string> missing, DeployOutcome outcome)
    {
        var service = depl.Service!;
        var undone = outcome is DeployOutcome.RolledBack or DeployOutcome.RollbackFailed or DeployOutcome.NotReady;
        var s = _session();
        var pgProject = s is not null && !string.IsNullOrEmpty(depl.Project)
            && ProjectHints.DetectDatabase(Paths.Resolve(s.Root, depl.Project)).Kind == DatabaseKind.Postgres;

        string Label(string n) => MissingSettings.CanGenerate(n) ? n + "  (la genero)"
            : MissingSettings.CanCreateDatabase(n, pgProject) ? n + "  (creo una base PostgreSQL propia)" : n + "  (tú me das el valor)";
        var ask = $"La API «{service}» no arranca porque le faltan estas variables:\n\n{string.Join("\n", missing.Select(m => "  • " + Label(m)))}\n\n"
            + "Las que puedo resolver las creo y las guardo en «Credenciales»; " + (undone ? "después vuelvo a subir." : "después reinicio la API.") + " ¿Sigo?";
        if (!Dialogs.Confirm(_owner, ask, true)) return;

        var made = new List<GeneratedCredential>();
        var manual = new List<string>();
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> queue = missing;
        var started = false;
        for (var round = 0; round < 6; round++)
        {
            var fresh = queue.Where(tried.Add).ToList();
            if (fresh.Count == 0) break;
            var read = await Task.Run(() => EnvService.Read(sv, service));
            if (!read.Ok || read.File is null)
            {
                _log("✘ " + read.Message, Level.Error);
                Dialogs.Confirm(_owner, "No pude leer el .env del servidor:\n" + read.Message, true);
                return;
            }
            var env = read.File;
            var changed = false;
            foreach (var n in fresh)
            {
                if (!string.IsNullOrEmpty(env.Get(n))) continue;
                if (MissingSettings.CanGenerate(n))
                {
                    env.Set(n, MissingSettings.RandomSecret());
                    made.Add(new($"{service} @ {sv.Name} · {n}", "", env.Get(n)!, $"Variable {n}, creada automáticamente porque la API no arrancaba sin ella."));
                    changed = true;
                }
                else if (MissingSettings.CanCreateDatabase(n, pgProject))
                {
                    var db = OperationWindow.Run(_owner, $"Crear la base de datos de {service}", canCancel: false,
                        (win, report, ct) => DatabaseService.Create(sv, service, report, ct), r => (r.Ok, r.Message));
                    if (db is not { Ok: true }) { manual.Add(n); continue; }
                    env.Set(n, db.Connection!);
                    made.Add(new($"{service} @ {sv.Name} · base de datos", db.User!, db.Password!, $"PostgreSQL en el servidor, base «{db.Database}» (variable {n})."));
                    changed = true;
                }
                else manual.Add(n);
            }
            if (!changed) break;
            var restart = !undone && manual.Count == 0;
            var write = OperationWindow.Run(_owner, $"Agregar variables a {service}", canCancel: false,
                (win, report, ct) => EnvService.Write(sv, service, env, restart, report, ct), r => (r.Ok, r.Message));
            if (write is null) return;
            _log(Describe(write), write.Ok ? Level.Ok : Level.Warning);
            if (write.Ok) { started = restart; break; }
            if (write.Outcome != DeployOutcome.StartFailed)
            {
                if (made.Count > 0) SaveCredentials(made);
                Dialogs.Confirm(_owner, write.Message, true);
                return;
            }
            queue = write.Missing ?? Array.Empty<string>();
        }

        var notes = new List<string>();
        if (made.Count > 0)
        {
            var saved = SaveCredentials(made);
            notes.Add("Creé: " + string.Join(", ", made.Select(m => m.Name.Split('·').Last().Trim())) + (saved ? ". Quedaron guardadas en «Credenciales»." : "."));
        }
        if (made.Any(g => g.Name.Contains("Webhook", StringComparison.OrdinalIgnoreCase)))
            notes.Add("Ojo: el secreto del webhook de pagos es inventado. Cuando conectes el proveedor de pagos, pon el valor real en «Variables (.env)».");
        if (manual.Count > 0) notes.Add(MissingSettings.Guide(service, manual));
        _log(string.Join(" ", notes), manual.Count > 0 ? Level.Warning : Level.Info);
        if (manual.Count > 0 || (!started && !undone))
        {
            if (manual.Count == 0) notes.Add("La API todavía no arranca. Revisa el registro de la operación (las líneas con «|») o abre «Variables (.env)».");
            Dialogs.Confirm(_owner, string.Join("\n\n", notes), true);
            return;
        }
        if (undone) await DeployApiAsync(sv, depl);
        else
        {
            _state = null;
            await QueryStatusAsync(force: true);
            Dialogs.Confirm(_owner, $"Listo: «{service}» arrancó con las variables nuevas.\n\n" + string.Join("\n\n", notes), true);
        }
    }

    // ------------------------------------------------------------ provision

    private static Server WithWeb(Server sv, string web) => new()
    {
        Id = sv.Id, Name = sv.Name, Host = sv.Host, User = sv.User, Port = sv.Port, Key = sv.Key, Web = web,
    };

    /// <summary>
    /// Creates a new API on the server: first a check of what the server has (installing what is missing when the user agrees),
    /// then a short form, then the setup. With <paramref name="deployAfter"/> it goes on to upload the code without asking again.
    /// </summary>
    private async Task ProvisionApiAsync(Server sv, bool deployAfter = false)
    {
        if (!IsTrusted(sv) && (await TestAsync(sv) is null || !IsTrusted(sv))) return;
        var s = _session();
        var depl = s?.Ctx.Config.Deployment;
        var api = s is null ? null : PickApi(s, depl?.Project);
        var service = depl?.Service ?? api?.SuggestedService ?? "api";
        var dll = depl?.Dll ?? api?.Dll ?? "";
        var auto = ProvisionService.AutoDomain(service, sv.Host);
        var domain = string.IsNullOrWhiteSpace(depl?.Domain) ? auto ?? "" : depl!.Domain!;
        var database = api is null ? null : ProjectHints.DetectDatabase(api.ProjectPath);
        var apiLabel = api is null ? null : $"{api.ProjectName}" + (api.RuntimeMajor is int rm ? $"  ·  .NET {rm}" : "") + $"  ·  {Paths.Relative(s!.Root, api.ProjectPath)}";

        // 1) look at the server, and fix what is missing if the user wants
        RemoteResult pre;
        while (true)
        {
            _log($"Revisando {sv.Name}…", Level.Info);
            var service1 = service;
            pre = await Task.Run(() => DeployService.Preflight(sv, service1));
            if (!pre.Ok)
            {
                var why = pre.LaunchError ?? pre.FailMessage ?? "No se pudo consultar el servidor.";
                _log("✘ " + why, Level.Error);
                Dialogs.Confirm(_owner, "No pude revisar el servidor:\n" + why, true);
                return;
            }
            var domain1 = domain;
            var dns = string.IsNullOrWhiteSpace(domain1) ? null : await Task.Run(() => ProvisionService.CheckDns(ServerService.CleanDomain(domain1) ?? domain1, sv.Host));
            var ports = await Task.Run(() => ProvisionService.CheckPorts(sv.Host));
            var items = ServerReadiness.Evaluate(new(pre.Values, sv.Web, api?.RuntimeMajor, ServerService.CleanDomain(domain), dns, WantHttps: true, PortsProblem: ports));
            var choice = DeployDialogs.Readiness(_owner, sv, items);
            if (choice == DeployDialogs.ReadinessChoice.Cancel) return;
            if (choice == DeployDialogs.ReadinessChoice.Continue) break;

            var fixes = ServerReadiness.Fixable(items);
            var webNow = ServerReadiness.RecommendedWeb(pre.Values, sv.Web);
            var runtime = api?.RuntimeMajor;
            _log($"Instalando en {sv.Name}: {ServerReadiness.InstallList(fixes)}…", Level.Info);
            var prepared = OperationWindow.Run(_owner, $"Preparar {sv.Name}", canCancel: false,
                (win, report, ct) => ProvisionService.Prepare(sv, fixes, webNow, runtime, report, ct),
                r => (r.Ok, r.Message));
            if (prepared is not null) _log(prepared.Message, prepared.Ok ? Level.Ok : Level.Error);
            // check again: shows what is fine now and what still is not
        }

        // the web server found on the machine decides which one to configure
        var web = ServerReadiness.RecommendedWeb(pre.Values, sv.Web);
        if (!string.Equals(web, sv.Web, StringComparison.OrdinalIgnoreCase))
        {
            sv = WithWeb(sv, web);
            var list = _store.Read();
            var i = list.FindIndex(x => x.Id == sv.Id);
            if (i >= 0) { list[i].Web = web; _store.Save(list); }
            _log($"{sv.Name} usa {sv.WebName}: lo dejo anotado.", Level.Info);
        }
        var port = int.TryParse(pre.Value("suggested_port"), out var p) ? p : 5000;

        // 2) the form
        var req = DeployDialogs.ProvisionForm(_owner, sv, service, dll, domain, port, api?.RuntimeMajor, auto, database, apiLabel);
        if (req is null) return;

        // 3) create it
        _log($"Creando {req.Service} en {sv.Name}…", Level.Info);
        var result = OperationWindow.Run(_owner, $"Crear {req.Service} en {sv.Name}", canCancel: false,
            (win, report, ct) => ProvisionService.Provision(req, report, ct),
            r => (r.Ok, r.Message));
        if (result is null) return;
        _log(result.Message, result.Ok ? Level.Ok : Level.Error);
        if (!result.Ok) return;

        if (result.Generated is { Count: > 0 } made)
            DeployDialogs.ShowGenerated(_owner, made, req.Service, SaveCredentials(made));
        else if (result.Secrets is { } secrets) DeployDialogs.ShowSecrets(_owner, secrets, req.Service);
        if (result.HttpsPending && req.IssueCertificate) await FinishHttpsAsync(sv, req);
        else if (req.IssueCertificate) _log($"✔ La API quedará en https://{req.Domain}", Level.Ok);
        if (s is not null && (depl is null || depl.Server != sv.Id || depl.Service != req.Service))
        {
            if (deployAfter || Dialogs.Confirm(_owner, $"¿Dejo este proyecto apuntando a «{req.Service}» en {sv.Name}?", true))
            {
                _saveDeployment(new DeploymentConfig
                {
                    Server = sv.Id, Service = req.Service, Dll = req.Dll, Domain = req.Domain,
                    Project = api is null ? depl?.Project : Paths.Relative(s.Root, api.ProjectPath),
                    Extra = depl?.Extra,
                });
                Update();
            }
        }
        if (s?.Ctx.Config.Deployment is { } linked && linked.Service == req.Service && linked.Server == sv.Id
            && (deployAfter || Dialogs.Confirm(_owner, "La API está creada. ¿Subo ahora tu código?", true)))
            await DeployApiAsync(sv, linked);
    }

    /// <summary>The certificate did not come out: say why in plain words and offer to try again once it is fixed, with no console.</summary>
    private async Task FinishHttpsAsync(Server sv, ProvisionRequest req)
    {
        while (true)
        {
            var ports = await Task.Run(() => ProvisionService.CheckPorts(sv.Host));
            var dns = await Task.Run(() => ProvisionService.CheckDns(req.Domain, sv.Host));
            var why = ports ?? dns
                ?? "Let's Encrypt no pudo validar «" + req.Domain + "». Revisa que la dirección apunte a este servidor y que no pase por un proxy (en Cloudflare, nube gris).";
            _log("⚠ El certificado HTTPS quedó pendiente.", Level.Warning);
            if (!Dialogs.Confirm(_owner, $"La API ya está creada, pero el certificado HTTPS quedó pendiente.\n\n{why}\n\nCuando lo hayas resuelto, ¿lo intento otra vez ahora?", true))
            {
                _log("Puedes pedir el certificado más tarde con «Crear API nueva» o desde el servidor.", Level.Info);
                return;
            }
            var retry = OperationWindow.Run(_owner, $"Certificado HTTPS de {req.Domain}", canCancel: false,
                (win, report, ct) => ProvisionService.RetryHttps(sv, req.Domain, req.CertificateEmail, sv.Web, report, ct),
                r => (r.Ok && !r.HttpsPending, r.Message));
            if (retry is { Ok: true, HttpsPending: false })
            {
                _log($"✔ Certificado emitido: https://{req.Domain}", Level.Ok);
                return;
            }
        }
    }

    // ------------------------------------------------------------ variables and versions

    private async Task EditVariablesAsync(Server sv, string service)
    {
        _log($"Leyendo las variables de {service}…", Level.Info);
        var read = await Task.Run(() => EnvService.Read(sv, service));
        if (!read.Ok || read.File is null)
        {
            _log("✘ " + read.Message, Level.Error);
            Dialogs.Confirm(_owner, read.Message, true);
            return;
        }
        if (!DeployDialogs.EditEnv(_owner, service, read.File, out var restart)) return;

        var env = read.File;
        var result = OperationWindow.Run(_owner, $"Guardar variables de {service}", canCancel: false,
            (win, report, ct) => EnvService.Write(sv, service, env, restart, report, ct),
            r => (r.Ok, Describe(r)));
        if (result is null) return;
        _log(Describe(result), result.Ok ? Level.Ok : Level.Error);
        await QueryStatusAsync(force: true);
    }

    private async Task VersionsAsync(Server sv, string service)
    {
        _log($"Buscando versiones guardadas de {service}…", Level.Info);
        string? error = null;
        var list = await Task.Run(() => DeployService.ListBackups(sv, service, out error));
        if (error is not null) { _log("✘ " + error, Level.Error); return; }
        if (list.Count == 0)
        {
            Dialogs.Confirm(_owner, "Todavía no hay versiones guardadas: se crea una cada vez que subes la API.", true);
            return;
        }
        var i = Dialogs.Choose(_owner, "¿A qué versión quieres volver? (la actual se guarda antes)",
            list.Select(b => $"{b.Date}   ·   {b.SizeKb / 1024.0:0.0} MB").ToList(), 0);
        if (i < 0) return;
        var chosen = list[i];
        if (!Dialogs.Confirm(_owner, $"¿Volver a la versión del {chosen.Date}? El servicio se reinicia unos segundos.", false)) return;

        var result = OperationWindow.Run(_owner, $"Volver a la versión anterior de {service}", canCancel: false,
            (win, report, ct) => DeployService.Rollback(sv, service, chosen.Id, report, ct),
            r => (r.Ok, r.Message));
        if (result is null) return;
        _log(result.Message, result.Ok ? Level.Ok : Level.Error);
        await QueryStatusAsync(force: true);
    }
}
