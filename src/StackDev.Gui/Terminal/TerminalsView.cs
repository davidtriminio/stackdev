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
/// The "Terminales" section: tabs with real terminals (each with its own shell), a button to open more,
/// another to choose folder and shell, and another to split the tab into panes (up to three).
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

    /// <summary>Asks the container to hide the terminal panel (the sessions stay alive).</summary>
    public event Action? AskHide;
    /// <summary>Asks to toggle between half screen and full screen.</summary>
    public event Action? AskToggleFull;
    /// <summary>Shortcuts that affect the container (show/hide) and arrived from inside a terminal.</summary>
    public event Action<string, int>? AskShortcut;

    /// <summary>Tab shortcuts: next, prev, tab (n = 1..9) and new.</summary>
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

        // Tab bar
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

        // Body: the background is the same as the terminal's, so it does not flicker when opening
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

    // ------------------------------------------------------------ Shells and folders

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

    // ------------------------------------------------------------ Tabs

    /// <summary>When the section is shown: if there is no terminal, opens one; if there is, gives focus back to the active one.</summary>
    public void OnShow()
    {
        if (_tabs.Count == 0) Fresh();
        else if (_current is not null) Select(_current);
    }

    /// <summary>Changes the button's glyph and tooltip according to the current mode.</summary>
    public void MarkComplete(bool full)
    {
        if (_btnFull is null) return;
        _btnFull.Content = full ? "\uE73F" : "\uE740";
        _btnFull.ToolTip = full ? "Volver a media pantalla" : "Pantalla completa";
    }

    /// <summary>Shows the tab for that folder if it already exists; otherwise opens a new terminal there.</summary>
    public void Open(string label, string folder)
    {
        var existing = _tabs.FirstOrDefault(x => x.Base == label && x.Panels.Count > 0
            && string.Equals(x.Panels[0].Folder, folder, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) Select(existing);
        else Fresh(ShellType.Default, folder, label);
    }

    /// <summary>Opens a tab that runs its own command line (for example ssh) instead of the shell.</summary>
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

    /// <summary>Closes all the terminals (and the programs running in them). When the application closes.</summary>
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

    /// <summary>Lays out the columns: pane, divider, pane... Changing the columns does not touch the panes, so their terminals stay alive.</summary>
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
        // Shells put the path of their .exe as the title: that is useless. A program that sets a title of its own (vim, claude...) is fine.
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

    // ------------------------------------------------------------ Choose folder and shell

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
