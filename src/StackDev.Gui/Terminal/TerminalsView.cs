using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StackDev.Core;
using StackDev.Core.Config;

namespace StackDev.Gui.Terminal;

public enum ShellType { Default, WindowsPowerShell, Cmd }

/// <summary>
/// La sección «Terminales»: pestañas con terminales reales (cada una con su propio shell), un botón para abrir más,
/// otro para elegir carpeta y shell, y otro para dividir la pestaña en paneles (hasta tres).
/// </summary>
public sealed class TerminalsView : Grid
{
    private const string IconFont = "Segoe MDL2 Assets";
    private const int MaxPanels = 3;

    private sealed class TabInfo
    {
        public Border Header = null!;
        public TextBlock Title = null!;
        public string Base = "";
        public string? FixedName;
        public Grid Content = null!;
        public readonly List<TerminalView> Panels = new();
        public readonly List<GridSplitter> Dividers = new();
        public TerminalView? Active;
    }

    private readonly Window _owner;
    private readonly Func<Session?> _session;
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal };
    private readonly Grid _body = new();
    private readonly List<TabInfo> _tabs = new();
    private readonly UIElement _empty;
    private TabInfo? _current;
    private Button? _btnFull;

    /// <summary>Pide al contenedor ocultar el panel de terminales (las sesiones siguen vivas).</summary>
    public event Action? AskHide;
    /// <summary>Pide alternar entre media pantalla y pantalla completa.</summary>
    public event Action? AskToggleFull;
    /// <summary>Atajos que afectan al contenedor (mostrar/ocultar) y llegaron desde dentro de una terminal.</summary>
    public event Action<string, int>? AskShortcut;

    /// <summary>Atajos de pestañas: next, prev, tab (n = 1..9) y new.</summary>
    public void Shortcut(string action, int n)
    {
        switch (action)
        {
            case "new": Fresh(); return;
            case "tab":
                if (n >= 1 && n <= _tabs.Count) Select(_tabs[n - 1]);
                return;
            case "next":
            case "prev":
                if (_tabs.Count == 0) { Fresh(); return; }
                var i = _current is null ? 0 : _tabs.IndexOf(_current);
                var step = action == "next" ? 1 : -1;
                Select(_tabs[(i + step + _tabs.Count) % _tabs.Count]);
                return;
        }
    }

    public TerminalsView(Window owner, Func<Session?> session)
    {
        _owner = owner;
        _session = session;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Barra de pestañas
        var bar = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _strip,
        };
        bar.Children.Add(scroll);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
        buttons.Children.Add(Icon("", "Nueva terminal", () => Fresh()));
        buttons.Children.Add(Icon("", "Elegir carpeta y shell…", ChooseAndOpen));
        buttons.Children.Add(Icon("\u25EB", "Dividir esta pestaña", Split, "Segoe UI Symbol"));
        _btnFull = Icon("\uE740", "Pantalla completa", () => AskToggleFull?.Invoke());
        buttons.Children.Add(_btnFull);
        buttons.Children.Add(Icon("\uE8BB", "Ocultar terminales", () => AskHide?.Invoke()));
        Grid.SetColumn(buttons, 1);
        bar.Children.Add(buttons);
        Children.Add(bar);

        // Cuerpo: el fondo es el mismo de la terminal, así no parpadea al abrir
        var frame = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x08, 0x09, 0x0A)),
            BorderBrush = P("LineBrush"),
            BorderThickness = new Thickness(1),
            Child = _body,
        };
        Grid.SetRow(frame, 1);
        Children.Add(frame);

        var open = new Button { Content = "Nueva terminal", Style = (Style)Application.Current.FindResource("BtnPrimary"), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        open.Click += (_, _) => Fresh();
        var empty = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
        empty.Children.Add(new TextBlock { Text = "No hay terminales abiertas.", Foreground = P("MutedBrush") });
        empty.Children.Add(open);
        _empty = empty;
        _body.Children.Add(empty);
    }

    private static Brush P(string key) => (Brush)Application.Current.FindResource(key);

    private static Button Icon(string glyph, string help, Action toClick, string font = IconFont)
    {
        var b = new Button
        {
            Content = glyph,
            FontFamily = new FontFamily(font),
            FontSize = 14,
            Style = (Style)Application.Current.FindResource("BtnIcon"),
            ToolTip = help,
        };
        b.Click += (_, _) => toClick();
        return b;
    }

    // ------------------------------------------------------------ Shells y carpetas

    private static (string Name, string CommandLine) ShellFor(ShellType type)
    {
        var sys = Environment.SystemDirectory;
        switch (type)
        {
            case ShellType.Cmd:
                return ("cmd", $"\"{Path.Combine(sys, "cmd.exe")}\"");
            case ShellType.WindowsPowerShell:
                return ("powershell", $"\"{Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe")}\" -NoLogo");
            default:
                var exe = StackDev.Core.Proc.Shell.PowerShellExe;
                var name = Path.GetFileNameWithoutExtension(exe).Equals("pwsh", StringComparison.OrdinalIgnoreCase) ? "pwsh" : "powershell";
                return (name, $"\"{exe}\" -NoLogo");
        }
    }

    private List<(string Name, string Folder)> Folders()
    {
        var list = new List<(string, string)>();
        var s = _session();
        if (s is null)
        {
            list.Add(("inicio", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            return list;
        }
        list.Add(("raíz", s.Root));
        foreach (var (comp, name) in new[] { (ProjectComponent.Api, "api"), (ProjectComponent.Web, "web"), (ProjectComponent.Mobile, "móvil") })
        {
            if (!s.Ctx.Has(comp)) continue;
            var dir = s.Ctx.Folder(comp);
            if (Directory.Exists(dir)) list.Add((name, dir));
        }
        return list;
    }

    // ------------------------------------------------------------ Pestañas

    /// <summary>Al mostrar la sección: si no hay ninguna terminal, abre una; si hay, devuelve el foco a la activa.</summary>
    public void OnShow()
    {
        if (_tabs.Count == 0) Fresh();
        else if (_current is not null) Select(_current);
    }

    /// <summary>Cambia el glifo y la ayuda del botón según el modo actual.</summary>
    public void MarkComplete(bool full)
    {
        if (_btnFull is null) return;
        _btnFull.Content = full ? "\uE73F" : "\uE740";
        _btnFull.ToolTip = full ? "Volver a media pantalla" : "Pantalla completa";
    }

    /// <summary>Muestra la pestaña de esa carpeta si ya existe; si no, abre una terminal nueva ahí.</summary>
    public void Open(string label, string folder)
    {
        var existing = _tabs.FirstOrDefault(x => x.Base == label && x.Panels.Count > 0
            && string.Equals(x.Panels[0].Folder, folder, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) Select(existing);
        else Fresh(ShellType.Default, folder, label);
    }

    /// <summary>Abre una pestaña que ejecuta una línea de comando propia (por ejemplo ssh) en lugar de la shell.</summary>
    public void OpenCommand(string label, string line) => Fresh(ShellType.Default, null, label, line);

    public void Fresh(ShellType type = ShellType.Default, string? folder = null, string? label = null, string? lineOwn = null)
    {
        var (nameShell, line) = ShellFor(type);
        if (lineOwn is not null) line = lineOwn;
        folder ??= Folders()[0].Folder;
        var baseTitle = label ?? nameShell;
        var repeated = _tabs.Count(x => x.Base == baseTitle);

        var p = new TabInfo { Base = baseTitle };
        p.Content = new Grid();
        _body.Children.Add(p.Content);
        BuildHeader(p, repeated == 0 ? baseTitle : $"{baseTitle} {repeated + 1}");
        _strip.Children.Add(p.Header);
        _tabs.Add(p);
        AddPanel(p, line, folder);
        Select(p);
    }

    private void BuildHeader(TabInfo p, string title)
    {
        var icon = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily(IconFont),
            FontSize = 12,
            Foreground = P("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        p.Title = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new Button
        {
            Content = "",
            FontFamily = new FontFamily(IconFont),
            FontSize = 10,
            Style = (Style)Application.Current.FindResource("BtnIcon"),
            ToolTip = "Cerrar",
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(0),
        };
        close.Click += (_, e) => { e.Handled = true; CloseTab(p); };

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        row.Children.Add(icon);
        row.Children.Add(close);
        row.Children.Add(p.Title);

        p.Header = new Border
        {
            Child = row,
            Padding = new Thickness(10, 6, 6, 6),
            Margin = new Thickness(0, 0, 4, 0),
            MinWidth = 120,
            MaxWidth = 240,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(0, 0, 0, 2),
            Cursor = Cursors.Hand,
            ToolTip = "Doble clic: cambiar el nombre · clic central: cerrar",
        };
        p.Header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) Rename(p); else Select(p);
        };
        p.Header.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) CloseTab(p);
        };
    }

    private void Rename(TabInfo p)
    {
        var fresh = Dialogs.Ask(_owner, "Nombre de la pestaña", p.Title.Text);
        if (fresh is null) return;
        p.FixedName = fresh;
        p.Title.Text = fresh;
    }

    private void Select(TabInfo p)
    {
        _current = p;
        foreach (var t in _tabs)
        {
            var active = t == p;
            t.Content.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            t.Header.Background = active ? P("Surface2Brush") : Brushes.Transparent;
            t.Header.BorderBrush = active ? P("AccentBrush") : Brushes.Transparent;
            t.Title.Foreground = active ? P("TextBrush") : P("MutedBrush");
        }
        _empty.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => p.Active?.Focus()));
    }

    private void CloseTab(TabInfo p)
    {
        var index = _tabs.IndexOf(p);
        if (index < 0) return;
        _tabs.RemoveAt(index);
        _strip.Children.Remove(p.Header);
        _body.Children.Remove(p.Content);
        foreach (var v in p.Panels.ToList())
        {
            p.Content.Children.Remove(v);
            v.Dispose();
        }
        p.Panels.Clear();

        if (_tabs.Count == 0)
        {
            _current = null;
            _empty.Visibility = Visibility.Visible;
        }
        else if (_current == p) Select(_tabs[Math.Min(index, _tabs.Count - 1)]);
    }

    /// <summary>Cierra todas las terminales (y los programas que corren en ellas). Al cerrar la aplicación.</summary>
    public void CloseAll()
    {
        foreach (var p in _tabs.ToList()) CloseTab(p);
    }

    // ------------------------------------------------------------ Paneles

    private TerminalView AddPanel(TabInfo p, string line, string folder)
    {
        var env = _session()?.EnvFor(folder);
        var v = new TerminalView(line, folder, env);
        v.Finished += (view, _) => OnFinish(p, view);
        v.Shortcut += (action, n) => { if (action is "next" or "prev" or "new" or "tab") Shortcut(action, n); else AskShortcut?.Invoke(action, n); };
        v.TitleChanged += (_, title) => OnTitleChanged(p, title);

        if (p.Panels.Count > 0)
        {
            var divider = new GridSplitter
            {
                Width = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = P("LineBrush"),
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ResizeDirection = GridResizeDirection.Columns,
            };
            p.Dividers.Add(divider);
            p.Content.Children.Add(divider);
        }
        p.Panels.Add(v);
        p.Content.Children.Add(v);
        p.Active = v;
        Place(p);
        return v;
    }

    /// <summary>Reparte las columnas: panel, divisor, panel... Cambiar las columnas no toca a los paneles, así sus terminales siguen vivas.</summary>
    private static void Place(TabInfo p)
    {
        p.Content.ColumnDefinitions.Clear();
        for (var i = 0; i < p.Panels.Count; i++)
        {
            if (i > 0) p.Content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            p.Content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        }
        for (var i = 0; i < p.Panels.Count; i++) Grid.SetColumn(p.Panels[i], i * 2);
        for (var i = 0; i < p.Dividers.Count; i++) Grid.SetColumn(p.Dividers[i], i * 2 + 1);
    }

    private void OnFinish(TabInfo p, TerminalView v)
    {
        if (p.Panels.Count > 1) RemovePanel(p, v);
        else CloseTab(p);
    }

    private void RemovePanel(TabInfo p, TerminalView v)
    {
        var i = p.Panels.IndexOf(v);
        if (i < 0) return;
        p.Content.Children.Remove(v);
        p.Panels.RemoveAt(i);
        if (p.Dividers.Count > 0)
        {
            var d = p.Dividers[Math.Max(0, i - 1)];
            p.Content.Children.Remove(d);
            p.Dividers.Remove(d);
        }
        v.Dispose();
        Place(p);
        p.Active = p.Panels.FirstOrDefault();
        if (_current == p) Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => p.Active?.Focus()));
    }

    private static void OnTitleChanged(TabInfo p, string title)
    {
        // Los shells ponen la ruta de su .exe como título: eso no sirve. Un programa que pone un título propio (vim, claude...) sí.
        if (p.FixedName is not null || string.IsNullOrWhiteSpace(title)) return;
        if (title.Contains('\\') || title.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
        p.Title.Text = title.Length > 40 ? title[..40] : title;
    }

    private void Split()
    {
        if (_current is null) { Fresh(); return; }
        if (_current.Panels.Count >= MaxPanels) return;
        var origin = _current.Active ?? _current.Panels[0];
        var v = AddPanel(_current, origin.CommandLine, origin.Folder);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(v.Focus));
    }

    // ------------------------------------------------------------ Elegir carpeta y shell

    private void ChooseAndOpen()
    {
        var options = new List<(string Text, ShellType Tipo, string Folder, string Label)>();
        var folders = Folders();
        foreach (var (name, dir) in folders)
            options.Add(($"PowerShell — {name}   ({dir})", ShellType.Default, dir, name));
        options.Add(($"Windows PowerShell — {folders[0].Name}", ShellType.WindowsPowerShell, folders[0].Folder, "powershell"));
        options.Add(($"Símbolo del sistema (cmd) — {folders[0].Name}", ShellType.Cmd, folders[0].Folder, "cmd"));

        var i = Dialogs.Choose(_owner, "Nueva terminal en…", options.Select(x => x.Text).ToList(), 0);
        if (i < 0) return;
        var o = options[i];
        Fresh(o.Tipo, o.Folder, o.Label);
    }
}
