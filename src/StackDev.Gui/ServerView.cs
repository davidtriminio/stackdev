using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StackDev.Core;
using StackDev.Core.Config;
using StackDev.Core.Servers;
using StackDev.Core.Ui;

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
        buttons.Children.Add(Button("Probar conexión", () => _ = TestAsync(sv), primary: true));
        buttons.Children.Add(Button("Terminal SSH", () => _terminal($"ssh {sv.Name}", Ssh.TerminalLine(sv, ""))));
        buttons.Children.Add(Button("Editar", () => EditServer(sv)));
        buttons.Children.Add(Button("Eliminar", () => DeleteServer(sv), danger: true));
        body.Children.Add(buttons);

        var chips = new StackPanel { Orientation = Orientation.Horizontal };
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
                ? "Este proyecto aún no tiene un servidor. Elige a cuál se sube su API y cómo se llama el servicio."
                : "El servidor que usaba este proyecto ya no está en la lista. Elige otro.", 12.5, P("MutedBrush")));
            var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            row.Children.Add(Button("Configurar despliegue", () => ConfigureDeployment(servers, depl), primary: true));
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
        buttons.Children.Add(Button("Logs en vivo", () => _terminal($"logs {svc}", Ssh.TerminalLine(sv, $"sudo journalctl -u {svc} -f -n 100 --no-pager")), primary: true));
        buttons.Children.Add(Button("Actualizar estado", () => _ = QueryStatusAsync(force: true)));
        buttons.Children.Add(Button("Reiniciar", () => _ = ActionAsync(sv, svc, "restart", "reiniciar")));
        buttons.Children.Add(Button("Detener", () => _ = ActionAsync(sv, svc, "stop", "detener")));
        buttons.Children.Add(Button("Iniciar", () => _ = ActionAsync(sv, svc, "start", "iniciar")));
        buttons.Children.Add(Button("Cambiar", () => ConfigureDeployment(servers, depl)));
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

    private async Task TestAsync(Server sv)
    {
        _probes[sv.Id] = "Conectando…";
        Draw();
        var r = await Task.Run(() => ServerService.Test(sv));
        _probes[sv.Id] = (r.Ok ? "✔ " : "✘ ") + r.Message;
        Draw();
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
        var suggestion = new string(s.Ctx.Name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : '-').ToArray()).Trim('-');
        if (suggestion.Length == 0 || !char.IsLetter(suggestion[0])) suggestion = "api";
        var fresh = ServerDialogs.EditDeployment(_owner, servers, current, suggestion + "-api", "");
        if (fresh is null) return;
        _saveDeployment(fresh);
        _state = null;
        _stateOf = "";
        Update();
    }
}
