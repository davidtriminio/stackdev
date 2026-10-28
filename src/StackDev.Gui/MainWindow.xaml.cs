using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StackDev.Core;
using StackDev.Core.Android;
using StackDev.Core.Config;
using StackDev.Core.Detection;
using StackDev.Core.Docker;
using StackDev.Core.Monitoring;
using StackDev.Core.Orchestration;
using StackDev.Core.Services;
using StackDev.Core.State;
using StackDev.Core.Ui;

namespace StackDev.Gui;

public partial class MainWindow : Window
{
    private enum ViewKind { Home, Panel, Conditions, ConsolePage, Config, Terminals, Diagnostics, Server }

    /// <summary>A panel card (Postgres, API, Web, Emulator, App) with its state and its buttons.</summary>
    private sealed class Card
    {
        public required string Section { get; init; }
        public required System.Windows.Shapes.Ellipse Dot { get; init; }
        public required TextBlock State { get; init; }
        public required TextBlock Detail { get; init; }
    }

    private GuiUi _ui = null!;
    private StateStore _state = null!;
    private Session? _s;
    private StatusMonitor? _mon;
    private CommandConsole? _cc;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _closing;
    private bool _quit;                                  // true = really close (do not send to the tray)
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _trayNotified;
    private Snapshot? _shown;
    private DateTime _lastConfigCheck = DateTime.UtcNow;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly List<Card> _cards = new();
    private readonly List<Button> _actionButtons = new();
    private readonly List<ToggleButton> _chips = new();

    private enum TerminalMode { Hidden, Half, Full }

    private Terminal.TerminalsView? _terminals;
    private DiagnosticsView? _diag;
    private ServerView? _srv;
    private TerminalMode _termMode = TerminalMode.Hidden;
    private TerminalMode _appliedMode = TerminalMode.Hidden;
    private bool _outputHidden;
    private bool _cleaning;
    private GridLength _outputHeight = new(190);
    private bool _rail;        // barra lateral reducida a iconos
    private bool _railBefore;   // how it was before the terminals were opened

    public MainWindow()
    {
        InitializeComponent();
        _terminals = new Terminal.TerminalsView(this, () => _s);
        TerminalDock.Child = _terminals;
        _terminals.AskHide += () => ChangeTerminalMode(TerminalMode.Hidden);
        _terminals.AskToggleFull += () =>
            ChangeTerminalMode(_termMode == TerminalMode.Full ? TerminalMode.Half : TerminalMode.Full);
        _timer.Tick += (_, _) => Tick();
        MainArea.SizeChanged += (_, _) => AdjustHeader();
        Loaded += async (_, _) => await StartAsync();
        Closing += OnClose;
        _terminals.AskShortcut += (action, n) => TerminalShortcut(action, n);
        BtnBrowser.Content = Icons.WithGlyph("\uE774", "Navegador");
        _diag = new DiagnosticsView(this, () => _s, RunAsync);
        DiagnosticsHost.Children.Add(_diag);
        _srv = new ServerView(this, () => _s, () => new StackDev.Core.Servers.ServerStore(_state.Dir),
            OpenTerminalCommand, SaveDeployment, (t, n) => _ui.Write(t, n));
        ServerHost.Children.Add(_srv);
        CreateTray();
        Application.Current.SessionEnding += (_, _) => _quit = true;   // Windows shutdown/sign-out: it does exit
        PreviewKeyDown += (_, e) =>
        {
            var mod = Keyboard.Modifiers;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Q && mod == ModifierKeys.Control) { e.Handled = true; Exit(); return; }
            if (key == Key.P && mod == ModifierKeys.Control && _s is not null)
            {
                e.Handled = true;
                ChangeBar_Click(BtnChangeBar, new RoutedEventArgs());
                return;
            }
            if (_s is null) return;
            string? action = null;
            var n = 0;
            if ((key == Key.T || key == Key.Tab) && (mod == ModifierKeys.Control || mod == (ModifierKeys.Control | ModifierKeys.Shift)))
                action = (mod & ModifierKeys.Shift) != 0 ? "prev" : "next";
            else if (key == Key.Oem3 && mod == ModifierKeys.Control) action = "toggle";
            else if (key == Key.T && mod == (ModifierKeys.Control | ModifierKeys.Alt)) action = "new";
            else if (key >= Key.D1 && key <= Key.D9 && mod == (ModifierKeys.Control | ModifierKeys.Alt)) { action = "tab"; n = key - Key.D0; }
            if (action is null) return;
            e.Handled = true;
            TerminalShortcut(action, n);
        };
    }

    private static Brush P(string key) => (Brush)Application.Current.FindResource(key);

    // ------------------------------------------------------------ Start-up and project

    private async Task StartAsync()
    {
        _ui = new GuiUi(this, Output);
        _state = StateStore.Open();
        _compact = _state.Compact == "1";
        SetAppearanceText();
        Window_StateChanged(this, EventArgs.Empty);

        var arg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
        if (arg is not null && Directory.Exists(arg))
        {
            await OpenProjectAsync(Path.GetFullPath(arg));
            return;
        }

        var favorite = _state.Favorite;
        if (!string.IsNullOrWhiteSpace(favorite))
        {
            if (Directory.Exists(favorite))
            {
                await OpenProjectAsync(favorite);
                return;
            }
            _ui.Write($"El proyecto favorito ya no existe: {favorite}", Level.Warning);
        }

        // With no favourite: the last project you used is reopened
        var last = _state.LastProject;
        if (!string.IsNullOrWhiteSpace(last) && Directory.Exists(last))
        {
            await OpenProjectAsync(last);
            return;
        }
        UpdateNav();
        ShowView(ViewKind.Home);
    }

    private System.Windows.Controls.Primitives.Popup? _selector;

    /// <summary>Drop-down list of recent projects to jump from one to another without going through Home.</summary>
    private void ChangeBar_Click(object sender, RoutedEventArgs e)
    {
        if (_state is null) return;
        if (_selector is { IsOpen: true }) { _selector.IsOpen = false; return; }

        var list = new StackPanel { MinWidth = 280 };
        foreach (var path in _state.ReadProjects().Where(Directory.Exists).Take(12))
        {
            var current = _s is not null && string.Equals(Path.GetFullPath(path), _s.Root, StringComparison.OrdinalIgnoreCase);
            var row = new StackPanel();
            row.Children.Add(new TextBlock
            {
                Text = (current ? "✓ " : "") + System.IO.Path.GetFileName(path.TrimEnd('\\', '/')),
                FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
                FontFamily = (FontFamily)FindResource("UiFont"),
                FontSize = 13,
                Foreground = P("TextBrush"),
            });
            row.Children.Add(new TextBlock { Text = path, FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 11, Foreground = P("MutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            var b = new Button
            {
                Content = row,
                Style = (Style)FindResource("BtnIcon"),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 6, 10, 6),
                Width = 320,
            };
            var destination = path;
            b.Click += async (_, _) =>
            {
                _selector!.IsOpen = false;
                if (!current) await OpenFromHome(destination);
            };
            list.Children.Add(b);
        }
        var all = new Button
        {
            Content = new TextBlock { Text = "Ver todos los proyectos…", FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 13, Foreground = P("AccentBrush") },
            Style = (Style)FindResource("BtnIcon"),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 6, 10, 6),
            Width = 320,
        };
        all.Click += (_, _) => { _selector!.IsOpen = false; NavHome.IsChecked = true; };
        list.Children.Add(all);

        _selector = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = ProjectChip.IsVisible ? ProjectChip : BtnChangeBar,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = P("SurfaceBrush"),
                BorderBrush = P("LineBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4),
                Margin = new Thickness(0, 4, 0, 8),
                Child = list,
            },
            IsOpen = true,
        };
    }

    private async Task OpenProjectAsync(string root)
    {
        root = Path.GetFullPath(root);
        var parent = ProjectRoot.SuggestParent(root);
        if (parent is not null && Dialogs.Confirm(this, $"«{Path.GetFileName(root)}» parece ser parte del proyecto en:\n{parent}\n\n¿Usar la carpeta raíz del proyecto?"))
            root = parent;
        _state.LastProject = root;
        _state.AddProject(root);

        _timer.Stop();
        _mon?.Dispose();
        _mon = null;
        _shown = null;

        var s = new Session(root, _state, _ui, new SessionOptions());
        s.Capture = line => _ui.Write(line);
        _s = s;

        Occupy($"Abriendo {Path.GetFileName(root)}…");
        try { await Task.Run(s.Start); }
        catch (Exception ex) { _ui.Write($"No pude abrir el proyecto: {ex.Message}", Level.Error); }
        Release();

        _cc = new CommandConsole(s);
        _mon = new StatusMonitor(s);
        UpdateNav();
        NavPanel.IsChecked = true;
        BuildAll();
        _timer.Start();
        _ui.Write($"Proyecto abierto: {s.Root}", Level.Ok);
    }

    private void BuildAll()
    {
        if (_s is null) return;
        TxtProject.Text = _s.Ctx.Name;
        TxtPath.Text = _s.Root;
        TxtPath.ToolTip = _s.Root;
        Title = $"stackdev — {_s.Ctx.Name}";
        UpdateFavorite();
        BuildCards();
        BuildConditions();
        RefreshConsole();
        RefreshConfig();
        RefreshSidebar();
        ShowView(CurrentView());
        if (_shown is not null) UpdateCards(_shown);
    }

    private void RefreshSidebar()
    {
        if (_s is null) return;
        TxtMode.Text = _s.BuildMode;
        TxtAvd.Text = _s.Ctx.Has(ProjectComponent.Mobile) ? (_s.Avd ?? "(ninguno)") : "—";
    }

    // ------------------------------------------------------------ Ejecutar acciones

    private bool _compact;

    private void SetAppearanceText()
    {
        BtnAccent.Content = Appearance.IsAmber(_state.Accent) ? "Ámbar (terminal vieja)" : "Verde (terminal)";
        BtnDensity.Content = _compact ? "Compacta" : "Normal";
    }

    private void Accent_Click(object sender, RoutedEventArgs e)
    {
        var amber = !Appearance.IsAmber(_state.Accent);
        _state.Accent = amber ? "ambar" : "verde";
        var full = Appearance.Apply(_state.Accent);
        SetAppearanceText();
        if (!full) _ui.Write("El acento se aplica por completo al reiniciar stackdev.", Level.Info);
    }

    private void Density_Click(object sender, RoutedEventArgs e)
    {
        _compact = !_compact;
        _state.Compact = _compact ? "1" : "";
        SetAppearanceText();
        BuildCards();
        _shown = null;
        _mon?.Refresh();
    }

    /// <summary>The logo blinks while an action is in progress.</summary>
    private void BlinkLogo(bool active)
    {
        if (active)
            Logo.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(600))
            { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        else
            Logo.BeginAnimation(OpacityProperty, null);
    }

    private void Occupy(string text)
    {
        BlinkLogo(true);
        _busy = true;
        SetOutputHidden(false);
        TxtBusy.Text = text;
        BusyProgress.Visibility = Visibility.Visible;
        BtnCancel.IsEnabled = true;
        PinButtons(false);
    }

    private void Release()
    {
        BlinkLogo(false);
        _busy = false;
        TxtBusy.Text = "Listo";
        BusyProgress.Visibility = Visibility.Collapsed;
        BtnCancel.IsEnabled = false;
        PinButtons(true);
    }

    private void PinButtons(bool active)
    {
        foreach (var b in _actionButtons) b.IsEnabled = active;
        BtnUp.IsEnabled = BtnDown.IsEnabled = BtnCheck.IsEnabled = active;
        BtnChange.IsEnabled = active;
        TxtBar.IsEnabled = BtnBar.IsEnabled = active && _s is not null;
    }

    /// <summary>Runs something from the logic in the background showing its output. One action at a time; "Cancelar acción" stops it.</summary>
    private async Task<bool> RunAsync(string title, Func<Session, bool> f)
    {
        var s = _s;
        if (s is null || _busy) return false;

        _cts = new CancellationTokenSource();
        s.Ct = _cts.Token;
        _mon?.Pause();
        Occupy(title + "…");
        _ui.Write($"\n▶ {title}", Level.Title);

        var ok = false;
        try { ok = await Task.Run(() => f(s)); }
        catch (Exception ex) { _ui.Write($"Error inesperado: {ex.Message}", Level.Error); }

        var cancelled = _cts.IsCancellationRequested;
        _ui.Write(cancelled ? "Detenido." : ok ? "✔ Listo." : "✘ No terminó bien.", cancelled ? Level.Warning : ok ? Level.Ok : Level.Error);
        s.Ct = CancellationToken.None;
        _cts.Dispose();
        _cts = null;

        Release();
        if (_fromBar) { _fromBar = false; TxtBar.Focus(); }
        _mon?.Resume();
        RefreshSidebar();
        RefreshConditions();
        RefreshConsole();
        return ok;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _cleaning = true;
        _ui.Clean();
        Dispatcher.InvokeAsync(() => _cleaning = false, DispatcherPriority.Background);
    }

    // ------------------------------------------------------------ Salida plegable

    private void HideOutput_Click(object sender, RoutedEventArgs e) => SetOutputHidden(!_outputHidden);

    /// <summary>With the output hidden only its header and the command line remain; any new text shows it again.</summary>
    private void SetOutputHidden(bool hide)
    {
        if (_outputHidden == hide) return;
        _outputHidden = hide;
        if (hide && OutputRow.Height.IsAbsolute && OutputRow.Height.Value > 0) _outputHeight = OutputRow.Height;
        Output.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        Separator.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        OutputRow.MinHeight = hide ? 0 : 90;
        OutputRow.Height = hide ? GridLength.Auto : _outputHeight;
        BtnHideOutput.Content = hide ? "\uE70E" : "\uE70D";   // chevron arriba (mostrar) / abajo (ocultar)
        BtnHideOutput.ToolTip = hide ? "Mostrar la salida" : "Ocultar la salida (se muestra sola al ejecutar algo)";
    }

    private void Output_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_outputHidden && !_cleaning) SetOutputHidden(false);
    }

    private void Up_Click(object sender, RoutedEventArgs e) => _ = RunAsync("Levantar todo", s => s.All.Start());

    private void Down_Click(object sender, RoutedEventArgs e) => _ = RunAsync("Detener todo", s => s.All.Stop());

    private void Check_Click(object sender, RoutedEventArgs e) =>
        _ = RunAsync("Verificar todo", s =>
        {
            var res = s.All.Verify();
            foreach (var r in res)
                s.Ui.Write($"  {r.Step,-28} {(r.Code == 0 ? "OK" : "FALLÓ (" + r.Code + ")")}  {r.Seconds} s", r.Code == 0 ? Level.Ok : Level.Error);
            return res.All(r => r.Code == 0);
        });

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (_s is null || !_s.Ctx.Has(ProjectComponent.Mobile)) return;
        var a = Operations.ByCode("b");
        if (a is not null) _ = RunAsync(a.Title, a.Run);
    }

    // ------------------------------------------------------------ Card panel

    private static readonly (string Section, string Title, ProjectComponent Comp)[] Sections =
    {
        ("POSTGRES", "Postgres", ProjectComponent.Postgres),
        ("API", "API", ProjectComponent.Api),
        ("WEB", "Web", ProjectComponent.Web),
        ("EMULADOR", "Emulador Android", ProjectComponent.Mobile),
        ("FLUTTER", "App Flutter", ProjectComponent.Mobile),
    };

    private void PanelHost_SizeChanged(object sender, SizeChangedEventArgs e) => AdjustCards();

    /// <summary>With the terminals at half width the cards shrink to fit two across; otherwise normal width.</summary>
    /// <summary>With little width (terminal at half, narrow window) the buttons drop to a second row so they do not overlap the title.</summary>
    private void AdjustHeader()
    {
        var narrow = MainArea.ActualWidth < 1300;
        Grid.SetRow(ActionsPanel, narrow ? 1 : 0);
        ActionsPanel.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        ActionsPanel.Margin = narrow ? new Thickness(0, 14, 0, 0) : new Thickness(0);
    }

    /// <summary>
    /// Spreads the cards into equal columns that fill the width (leaving no gaps at the side).
    /// Leaves a few pixels of safety: if the columns measure exactly the width, the last one jumps to the next row.
    /// </summary>
    private static void Distribute(Panel panel, double available, double minimum, double gap)
    {
        if (available <= 0) return;
        var util = available - 10;
        var columns = Math.Max(1, (int)Math.Floor(util / (minimum + gap)));
        var width = Math.Max(240, Math.Floor(util / columns) - gap);
        foreach (var child in panel.Children)
        {
            if (child is FrameworkElement fe && fe is Border or CredentialsCard)
            {
                fe.Width = width;
                fe.Margin = new Thickness(0, 0, gap, 16);
            }
        }
    }

    private void AdjustCards()
    {
        var available = PanelHost.ViewportWidth > 0 ? PanelHost.ViewportWidth : PanelHost.ActualWidth;
        Distribute(CardsPanel, available, 360, _termMode == TerminalMode.Half ? 12 : 16);
    }

    private void AdjustHome()
    {
        var available = HomeScroll.ViewportWidth > 0 ? HomeScroll.ViewportWidth : HomeScroll.ActualWidth;
        Distribute(HomePanel, available, 380, 16);
    }

    /// <summary>Settings in two columns when there is width; in a single one otherwise.</summary>
    private void ConfigHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = ConfigHost.ActualWidth >= 1250;
        ColCfgGap.Width = new GridLength(wide ? 16 : 0);
        ColCfgB.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(ColConfigB, wide ? 0 : 1);
        Grid.SetColumn(ColConfigB, wide ? 2 : 0);
        Grid.SetColumnSpan(ColConfigB, wide ? 1 : 3);
    }

    private void ScrollHome_SizeChanged(object sender, SizeChangedEventArgs e) => AdjustHome();

    private void BuildCards()
    {
        CardsPanel.Children.Clear();
        _cards.Clear();
        _actionButtons.Clear();
        if (_s is null) return;

        foreach (var (section, title, comp) in Sections)
        {
            if (!_s.Ctx.Has(comp)) continue;

            var point = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = P("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            var state = new TextBlock { Text = "Consultando…", Foreground = P("MutedBrush"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            var detail = new TextBlock
            {
                Text = " ",
                Foreground = P("MutedBrush"),
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, _compact ? 10 : 16),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var blockName = new StackPanel { Orientation = Orientation.Horizontal };
            blockName.Children.Add(Icons.Badge(section));
            blockName.Children.Add(name);
            Grid.SetColumn(blockName, 1);
            Grid.SetColumn(state, 2);
            header.Children.Add(point);
            header.Children.Add(blockName);
            header.Children.Add(state);

            // Open a terminal in this component's folder (at half, next to the panel)
            if (CardTerminal(section) is { } destination)
            {
                var btnTerm = new Button
                {
                    Content = "\uE756",
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    Style = (Style)FindResource("BtnIcon"),
                    ToolTip = $"Abrir una terminal en {destination.Label}",
                    Margin = new Thickness(8, 0, 0, 0),
                };
                btnTerm.Click += (_, _) => OpenTerminal(destination.Label, destination.Folder);
                Grid.SetColumn(btnTerm, 4);
                header.Children.Add(btnTerm);
            }

            // Open the web app or the API in the chosen browser
            if (section is "API" or "WEB")
            {
                var component = comp;
                var btnWeb = new Button
                {
                    Content = "\uE774",
                    FontFamily = new FontFamily(Icons.Font),
                    Style = (Style)FindResource("BtnIcon"),
                    ToolTip = "Abrir en el navegador",
                    Margin = new Thickness(8, 0, 0, 0),
                };
                btnWeb.Click += (_, _) => _s?.Services.OpenInBrowser(component);
                Grid.SetColumn(btnWeb, 3);
                header.Children.Add(btnWeb);
            }

            var buttons = new WrapPanel();
            var first = true;
            foreach (var a in Operations.Available(_s).Where(x => x.Section == section))
            {
                var action = a;
                var b = new Button { Content = Icons.WithGlyph(Icons.ActionGlyph(a.Name), a.Title), Style = (Style)FindResource("BtnChip"), ToolTip = $"Código: {a.Code}" };
                if (first)
                {
                    b.Background = P("AccentGradient");
                    b.BorderBrush = P("AccentBrush");
                    b.Foreground = P("AccentOnBrush");
                    b.FontWeight = FontWeights.SemiBold;
                    first = false;
                }
                b.Click += (_, _) => _ = RunAsync(action.Title, action.Run);
                buttons.Children.Add(b);
                _actionButtons.Add(b);
            }

            var content = new StackPanel();
            content.Children.Add(header);
            content.Children.Add(detail);
            content.Children.Add(buttons);

            CardsPanel.Children.Add(new Border
            {
                Style = (Style)FindResource("Card"),
                Width = 380,
                Margin = new Thickness(0, 0, 16, 16),
                Padding = new Thickness(_compact ? 14 : 20, _compact ? 12 : 18, _compact ? 14 : 20, _compact ? 6 : 10),
                Child = content,
            });
            _cards.Add(new Card { Section = section, Dot = point, State = state, Detail = detail });
        }

        Dispatcher.InvokeAsync(AdjustCards, DispatcherPriority.Loaded);

        // The project's credentials, right after the app card
        CardsPanel.Children.Add(new CredentialsCard(new StackDev.Core.Credentials.CredentialsStore(_s.State.ConfigFolder(_s.Key)), this));

        if (_cards.Count == 0)
            CardsPanel.Children.Add(new TextBlock
            {
                Text = "Este proyecto no tiene nada configurado. Abre «Configuración» para editar dev-menu.json o detectarlo de nuevo.",
                Foreground = P("MutedBrush"),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520,
            });
        PinButtons(!_busy);
    }

    private static string ServiceColor(ServiceCode c) => c switch
    {
        ServiceCode.Running => "OkBrush",
        ServiceCode.Starting => "WarnBrush",
        ServiceCode.Error => "ErrBrush",
        _ => "MutedBrush",
    };

    private void Put(string section, string colorKey, string state, string detail)
    {
        var t = _cards.FirstOrDefault(x => x.Section == section);
        if (t is null) return;
        t.Dot.Fill = P(colorKey);
        t.State.Text = state;
        t.State.Foreground = P(colorKey);
        t.Detail.Text = string.IsNullOrEmpty(detail) ? " " : detail;
    }

    private void UpdateCards(Snapshot i)
    {
        var s = _s;
        if (s is null) return;

        if (i.Postgres is { } pg)
            Put("POSTGRES",
                pg.Code == PostgresCode.Running ? "OkBrush" : pg.Code == PostgresCode.Stopped ? "MutedBrush" : "WarnBrush",
                pg.Running ? "CORRIENDO" : pg.Code == PostgresCode.Stopped ? "DETENIDO" : "ATENCIÓN",
                pg.Text);

        foreach (var (section, e, log) in new[] { ("API", i.Api, i.LogApi), ("WEB", i.Web, i.LogWeb) })
        {
            if (e is null) continue;
            var brief = e.Code switch
            {
                ServiceCode.Running => "CORRIENDO",
                ServiceCode.Starting => "ARRANCANDO",
                ServiceCode.Error => "TERMINÓ CON ERROR",
                _ => "DETENIDA",
            };
            Put(section, ServiceColor(e.Code), brief, e.Running ? (e.Url ?? "") : (log ?? e.Url ?? ""));
        }

        if (s.Ctx.Has(ProjectComponent.Mobile))
        {
            if (i.Emulator is { } emu)
                Put("EMULADOR",
                    emu.Code == EmuCode.Ok ? "OkBrush" : emu.Code == EmuCode.NotResponding ? "ErrBrush" : "MutedBrush",
                    emu.Code == EmuCode.Ok ? "CORRIENDO" : emu.Code == EmuCode.NotResponding ? "NO RESPONDE" : "DETENIDO",
                    $"{emu.Text}   ·   AVD: {s.Avd ?? "(ninguno)"}" + (string.IsNullOrEmpty(i.Conditions) || i.Conditions == "Normal" ? "" : $"   ·   {i.Conditions}")
                    + (i.Phones.Length > 0 ? $"   ·   Teléfonos: {i.Phones}" : ""));
            else
                Put("EMULADOR", "MutedBrush", "SIN SDK", "No encontré el Android SDK (mobile.androidSdk en dev-menu.json)");

            Put("FLUTTER", i.App ? "OkBrush" : "MutedBrush", i.App ? "CORRIENDO" : "DETENIDA",
                i.App ? $"flutter run ({s.BuildMode}) en {s.Services.LiveApps().Count} sesión(es), cada una en su ventana" : $"Modo: {s.BuildMode}");
        }
    }

    private void Tick()
    {
        if (_s is null || _mon is null) return;

        if ((DateTime.UtcNow - _lastGit).TotalSeconds >= 20)
        {
            _lastGit = DateTime.UtcNow;
            _ = UpdateGitAsync();
        }

        var u = _mon.Last;
        if (u is not null && !ReferenceEquals(u, _shown))
        {
            _shown = u;
            UpdateCards(u);
            if (CurrentView() == ViewKind.Conditions) TxtActiveConds.Text = ConditionsText();
        }

        if (!_busy && (DateTime.UtcNow - _lastConfigCheck).TotalSeconds >= 2)
        {
            _lastConfigCheck = DateTime.UtcNow;
            if (_s.ReloadIfChanged())
            {
                _ui.Write("dev-menu.json cambió: lo recargué.", Level.Info);
                _mon.Refresh();
                BuildAll();
            }
        }
    }

    // ------------------------------------------------------------ Navigation

    private ViewKind CurrentView() =>
        NavHome.IsChecked == true ? ViewKind.Home :
        NavTerminals.IsChecked == true ? ViewKind.Terminals :
        NavDiagnostics.IsChecked == true ? ViewKind.Diagnostics :
        NavServer.IsChecked == true ? ViewKind.Server :
        NavCond.IsChecked == true ? ViewKind.Conditions :
        NavConsole.IsChecked == true ? ViewKind.ConsolePage :
        NavConfig.IsChecked == true ? ViewKind.Config : ViewKind.Panel;

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ShowView(CurrentView());
    }

    private void ShowView(ViewKind v)
    {
        HomeHost.Visibility = v == ViewKind.Home ? Visibility.Visible : Visibility.Collapsed;
        ActionsPanel.Visibility = v == ViewKind.Home || _s is null ? Visibility.Collapsed : Visibility.Visible;
        PanelHost.Visibility = v == ViewKind.Panel ? Visibility.Visible : Visibility.Collapsed;
        CondHost.Visibility = v == ViewKind.Conditions ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsHost.Visibility = v == ViewKind.Diagnostics ? Visibility.Visible : Visibility.Collapsed;
        ServerHost.Visibility = v == ViewKind.Server ? Visibility.Visible : Visibility.Collapsed;
        ConsoleHost.Visibility = v == ViewKind.ConsolePage ? Visibility.Visible : Visibility.Collapsed;

        // "Terminales" in the menu = the terminals at full screen; when going to another view they drop to half
        if (v == ViewKind.Terminals) _termMode = TerminalMode.Full;
        else if (_termMode == TerminalMode.Full) _termMode = TerminalMode.Half;
        ApplyTerminalMode();
        ConfigHost.Visibility = v == ViewKind.Config ? Visibility.Visible : Visibility.Collapsed;

        (TxtTitle.Text, TxtSubtitle.Text) = v switch
        {
            ViewKind.Home => ("Proyectos", "Los proyectos que has abierto. Elige uno para continuar."),
            ViewKind.Diagnostics => ("Diagnóstico", "Compara tu configuración con Docker y el proyecto, y administra contenedores y volúmenes."),
            ViewKind.Server => ("Servidor", "Tus servidores, el estado de la API de este proyecto y sus logs en vivo."),
            ViewKind.Conditions => ("Condiciones del dispositivo", "Red, batería, tema, texto, pantalla y ubicación del emulador."),
            ViewKind.Terminals => ("Terminales", "Terminales reales con pestañas: abre varias, divídelas o elige la carpeta del proyecto."),
            ViewKind.ConsolePage => ("Consola de comandos", "Cualquier comando en una carpeta del proyecto; tareas y últimos comandos."),
            ViewKind.Config => ("Configuración", "Cómo se lee dev-menu.json y dónde se guarda todo."),
            _ => ("Panel", "Estado en vivo de lo que corre en este proyecto."),
        };
        TxtWindowTitle.Text = _s is null || v == ViewKind.Home ? "stackdev" : $"{_s.Ctx.Name}  ·  {TxtTitle.Text}";
        if (v == ViewKind.Home) RefreshHome();
        if (v == ViewKind.Conditions) RefreshConditions();
        if (v == ViewKind.Diagnostics && _diag is not null) _ = _diag.UpdateAsync();
        if (v == ViewKind.Server) _srv?.Update();
        if (v == ViewKind.ConsolePage) RefreshConsole();
    }

    // ------------------------------------------------------------ Device conditions

    private string ConditionsText() => _s is null ? "" : $"Activas ahora: {_s.Conditions.Text()}";

    private void BuildConditions()
    {
        CondPanel.Children.Clear();
        _chips.Clear();
        if (_s is null) return;
        if (!_s.Ctx.Has(ProjectComponent.Mobile))
        {
            CondPanel.Children.Add(new TextBlock { Text = "Este proyecto no tiene móvil configurado.", Foreground = P("MutedBrush") });
            return;
        }

        foreach (var group in ConditionsService.Presets.GroupBy(p => p.Cat))
        {
            var row = new WrapPanel();
            var ofGroup = new List<ToggleButton>();
            foreach (var p in group)
            {
                var preset = p;
                var chip = new ToggleButton { Content = p.Txt, Tag = p, Style = (Style)FindResource("ChipToggle") };
                chip.Checked += (_, _) =>
                {
                    // Within a category only one option at a time.
                    foreach (var other in ofGroup.Where(o => !ReferenceEquals(o, chip) && o.IsChecked == true)) other.IsChecked = false;
                };
                ofGroup.Add(chip);
                _chips.Add(chip);
                row.Children.Add(chip);
            }
            var card = new StackPanel();
            card.Children.Add(new TextBlock { Text = group.Key.ToUpperInvariant(), FontSize = 11, Foreground = P("MutedBrush"), Margin = new Thickness(0, 0, 0, 8) });
            card.Children.Add(row);
            CondPanel.Children.Add(new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 12), Child = card });
        }
        RefreshConditions();
    }

    /// <summary>Marks the conditions that are active according to what is saved for this emulator.</summary>
    private void RefreshConditions()
    {
        if (_s is null || !_s.Ctx.Has(ProjectComponent.Mobile)) return;
        TxtActiveConds.Text = ConditionsText();
        var active = _s.Conditions.Active();
        foreach (var chip in _chips)
        {
            var p = (Preset)chip.Tag;
            chip.IsChecked = active.TryGetValue(p.Cat, out var v) && v == p.Txt;
        }
    }

    private void CondApply_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _chips.Where(c => c.IsChecked == true).Select(c => ((Preset)c.Tag).N.ToString()).ToList();
        if (chosen.Count == 0) { _ui.Write("Marca al menos una condición.", Level.Warning); return; }
        _ = RunAsync("Aplicar condiciones", s => s.Conditions.ApplyNumbers(chosen));
    }

    private void CondReset_Click(object sender, RoutedEventArgs e) =>
        _ = RunAsync("Restablecer condiciones", s => s.Conditions.Reset());

    // ------------------------------------------------------------ Command console

    private void RefreshConsole()
    {
        if (_s is null || _cc is null) return;
        TxtConsoleDir.Text = _cc.Prompt;
        TxtBarDir.Text = _cc.Prompt;
        BtnAliasApi.Visibility = _s.Ctx.Has(ProjectComponent.Api) ? Visibility.Visible : Visibility.Collapsed;
        BtnAliasWeb.Visibility = _s.Ctx.Has(ProjectComponent.Web) ? Visibility.Visible : Visibility.Collapsed;
        BtnAliasMobile.Visibility = _s.Ctx.Has(ProjectComponent.Mobile) ? Visibility.Visible : Visibility.Collapsed;

        CommandList.Items.Clear();
        var n = 1;
        foreach (var it in _cc.Items())
            CommandList.Items.Add($"{n++,2}   {(it.Task ? "★ " : "")}{it.Title}");
    }

    private int _hist = -1;
    private bool _fromBar;
    private readonly List<string> _barCommands = new();

    private void BarRun_Click(object sender, RoutedEventArgs e)
    {
        var text = TxtBar.Text;
        if (_s is null || string.IsNullOrWhiteSpace(text)) return;
        SetOutputHidden(false);
        if (_busy) { _ui.Write("Hay una acción en curso: espera o usa «Cancelar acción».", Level.Warning); return; }
        _hist = -1;
        _fromBar = true;
        _barCommands.Add(text.Trim());
        _state.AddBarHistory(_s.Key, text);
        RunText(text);
        TxtBar.Clear();
    }

    private void TxtBar_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            BarRun_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down)
        {
            e.Handled = true;
            NavigateHistory(e.Key == Key.Up ? 1 : -1);
        }
        else if (e.Key == Key.Escape && TxtBar.Text.Length > 0)
        {
            _hist = -1;
            TxtBar.Clear();
            e.Handled = true;
        }
    }

    private void HistUp_Click(object sender, RoutedEventArgs e) { NavigateHistory(1); TxtBar.Focus(); }

    private void HistDown_Click(object sender, RoutedEventArgs e) { NavigateHistory(-1); TxtBar.Focus(); }

    /// <summary>↑ (delta = 1) goes to an older command, ↓ (-1) to a newer one; past the newest it leaves the line empty.</summary>
    private void NavigateHistory(int delta)
    {
        if (_s is null) return;
        var history = _barCommands.AsEnumerable().Reverse()
            .Concat(_state.ReadBarHistory(_s.Key))
            .Concat(_state.ReadRecent(_s.Key).Select(r => r.Command))
            .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
        if (history.Count == 0)
        {
            _ui.Write("Aún no hay comandos en el historial: escribe uno y pulsa Enter.", Level.Muted);
            return;
        }
        _hist = Math.Clamp(_hist + delta, -1, history.Count - 1);
        TxtBar.Text = _hist < 0 ? "" : history[_hist];
        TxtBar.CaretIndex = TxtBar.Text.Length;
    }

    private void Alias_Click(object sender, RoutedEventArgs e)
    {
        if (_cc is null || sender is not Button { Tag: string alias }) return;
        _cc.HandleInput(alias);
        RefreshConsole();
    }

    private void RunText(string text)
    {
        if (_cc is null || string.IsNullOrWhiteSpace(text)) return;
        var cc = _cc;
        _ = RunAsync(text.Trim(), _ => { cc.HandleInput(text); return true; });
    }

    private void RunCommand_Click(object sender, RoutedEventArgs e)
    {
        RunText(TxtCommand.Text);
        TxtCommand.Clear();
    }

    private void RunWindow_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtCommand.Text)) return;
        RunText("v " + TxtCommand.Text.Trim());
        TxtCommand.Clear();
    }

    private void TxtCommand_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        RunCommand_Click(sender, e);
        e.Handled = true;
    }

    private void CommandList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CommandList.SelectedIndex < 0) return;
        RunText((CommandList.SelectedIndex + 1).ToString());
    }

    // ------------------------------------------------------------ Settings

    private TextBlock SummaryTitle(string text) => new()
    {
        Text = text, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = P("MutedBrush"), Margin = new Thickness(0, 10, 0, 8),
    };

    /// <summary>A small label (port, image, URL…) with its own background.</summary>
    private Border Chip(string text, string colorKey = "TextBrush", bool mono = true, bool icons = false)
    {
        var t = new TextBlock { FontSize = 11.5, Foreground = P(colorKey), VerticalAlignment = VerticalAlignment.Center };
        if (icons && text.Length > 1)
        {
            t.Inlines.Add(new System.Windows.Documents.Run(text[..1]) { FontFamily = new FontFamily(Icons.Font) });
            t.Inlines.Add(new System.Windows.Documents.Run(text[1..]));
        }
        else
        {
            t.Text = text;
            if (mono) t.FontFamily = (FontFamily)FindResource("MonoFont");
        }
        return new Border
        {
            Background = P("Surface2Brush"), BorderBrush = P("LineBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 4), Child = t,
        };
    }

    private UIElement ComponentRow(string key, string name, string main, params string[] labels)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tile = Icons.Badge(key, 32);
        tile.VerticalAlignment = VerticalAlignment.Top;
        g.Children.Add(tile);
        var n = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(n, 1);
        g.Children.Add(n);
        var body = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        if (main.Length > 0)
            body.Children.Add(new TextBlock
            {
                Text = main, FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 4),
            });
        foreach (var e in labels.Where(x => !string.IsNullOrWhiteSpace(x)))
            body.Children.Add(Chip(e));
        Grid.SetColumn(body, 2);
        g.Children.Add(body);
        return g;
    }

    private static Border SmallGlyphTile(string glyph, string hex)
    {
        var col = (Color)ColorConverter.ConvertFromString(hex);
        return new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(9), VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(0x26, col.R, col.G, col.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, col.R, col.G, col.B)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily(Icons.Font), FontSize = 15, Foreground = new SolidColorBrush(col),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    /// <summary>A path: tile with icon, name, the path in a box (folder dimmed, final name highlighted) and copy / open buttons.</summary>
    private UIElement PathRow(string label, string glyph, string color, string path, bool isFile, string? warning, string? ok = null)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tile = SmallGlyphTile(glyph, color);
        tile.Margin = new Thickness(0, 0, 10, 0);
        g.Children.Add(tile);

        var name = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(name, 1);
        g.Children.Add(name);

        // Dimmed folder + highlighted last segment
        var text = new TextBlock { FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var cut = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        if (cut > 0)
        {
            text.Inlines.Add(new System.Windows.Documents.Run(path[..(cut + 1)]) { Foreground = P("MutedBrush") });
            text.Inlines.Add(new System.Windows.Documents.Run(path[(cut + 1)..]) { Foreground = P("TextBrush"), FontWeight = FontWeights.SemiBold });
        }
        else text.Inlines.Add(new System.Windows.Documents.Run(path) { Foreground = P("TextBrush") });

        var body = new StackPanel();
        body.Children.Add(new Border
        {
            Background = P("Surface2Brush"), BorderBrush = P("LineBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 7), Child = text,
        });
        if (warning is not null || ok is not null)
        {
            var marks = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            if (ok is not null) marks.Children.Add(Chip("\uE73E  " + ok, "OkBrush", mono: false, icons: true));
            if (warning is not null) marks.Children.Add(Chip("\uE7BA  " + warning, "WarnBrush", mono: false, icons: true));
            body.Children.Add(marks);
        }
        Grid.SetColumn(body, 2);
        g.Children.Add(body);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 1, 0, 0) };
        var copy = new Button
        {
            Content = "\uE8C8", FontFamily = new FontFamily(Icons.Font), Style = (Style)FindResource("BtnIcon"), ToolTip = "Copiar la ruta",
        };
        copy.Click += async (_, _) =>
        {
            try { Clipboard.SetText(path); } catch { return; }
            copy.Content = "\uE73E";
            await Task.Delay(1200);
            copy.Content = "\uE8C8";
        };
        var open = new Button
        {
            Content = "\uE838", FontFamily = new FontFamily(Icons.Font), Style = (Style)FindResource("BtnIcon"),
            ToolTip = isFile ? "Abrir la carpeta que lo contiene" : "Abrir la carpeta",
        };
        open.Click += (_, _) => OpenFolder(isFile ? (Path.GetDirectoryName(path) ?? path) : path);
        buttons.Children.Add(copy);
        buttons.Children.Add(open);
        Grid.SetColumn(buttons, 3);
        g.Children.Add(buttons);
        return g;
    }

    private void RefreshConfig()
    {
        if (_s is null) return;
        var c = _s.Ctx;
        SummaryPanel.Children.Clear();

        // ---- Components: icon, name, where it lives and its data as labels
        SummaryPanel.Children.Add(SummaryTitle("COMPONENTES"));
        if (c.Has(ProjectComponent.Postgres))
        {
            var pg = c.Config.Postgres!;
            SummaryPanel.Children.Add(ComponentRow("POSTGRES", "Postgres", pg.EfContainer, $"localhost:{pg.EfPort}", pg.EfImage));
        }
        foreach (var (comp, key) in new[] { (ProjectComponent.Api, "API"), (ProjectComponent.Web, "WEB"), (ProjectComponent.Mobile, "FLUTTER") })
            if (c.Has(comp))
                SummaryPanel.Children.Add(ComponentRow(key, Session.Names[comp], _s.Rel(c.Folder(comp)), c.Url(comp) ?? ""));
        if (c.Tasks.Count > 0)
            SummaryPanel.Children.Add(ComponentRow("FLUTTER", "Tareas", "", c.Tasks.Select(t => t.Name).ToArray()));

        // ---- Rutas
        SummaryPanel.Children.Add(SummaryTitle("RUTAS"));
        SummaryPanel.Children.Add(PathRow("Archivo", "\uE8A5", "#6FA8FF", _s.Store.FilePath, true,
            _s.Store.InRepo ? "Está dentro del proyecto: usa «Mover fuera del proyecto»" : null,
            _s.Store.InRepo ? null : "Fuera del repositorio"));
        SummaryPanel.Children.Add(PathRow("Estado", "\uE838", "#22D3EE", _s.State.Dir, false, null));
        if (_s.Sdk is not null) SummaryPanel.Children.Add(PathRow("Android SDK", "\uE8EA", "#F5B94A", _s.Sdk, false, null));

        // ---- Secretos
        SummaryPanel.Children.Add(SummaryTitle("SECRETOS"));
        var encrypted = StackDev.Core.Security.Protection.Available;
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(Chip(encrypted ? "\uE72E  Cifrados con DPAPI" : "\uE785  Sin cifrar", encrypted ? "OkBrush" : "WarnBrush", mono: false, icons: true));
        row.Children.Add(new TextBlock
        {
            Text = encrypted
                ? "Contraseña de Postgres, variables secretas de la API y credenciales. Solo tu usuario en este equipo puede leerlos."
                : "El cifrado DPAPI solo existe en Windows.",
            Foreground = P("MutedBrush"), FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 4), MaxWidth = 560,
        });
        SummaryPanel.Children.Add(row);
        RefreshBrowsers();
    }

    // ------------------------------------------------------------ Navegador

    private System.Windows.Controls.Primitives.Popup? _menu;

    /// <summary>Dark drop-down list under a button (the default WPF menus come out light).</summary>
    private void ShowMenu(FrameworkElement anchor, IEnumerable<(string Text, Action Operation)> items)
    {
        if (_menu is { IsOpen: true }) _menu.IsOpen = false;
        var list = new StackPanel { MinWidth = Math.Max(220, anchor.ActualWidth) };
        foreach (var (text, action) in items)
        {
            var chosen = action;
            var b = new Button
            {
                Content = new TextBlock { Text = text, FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 13, Foreground = P("TextBrush") },
                Style = (Style)FindResource("BtnIcon"),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 6, 10, 6),
            };
            b.Click += (_, _) => { _menu!.IsOpen = false; chosen(); };
            list.Children.Add(b);
        }
        _menu = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = P("SurfaceBrush"), BorderBrush = P("LineBrush"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(4), Margin = new Thickness(0, 4, 0, 8), Child = list,
            },
            IsOpen = true,
        };
    }

    private void RefreshBrowsers()
    {
        BtnNavGlobal.Content = StackDev.Core.Proc.Browsers.Name(_state?.Browser) + "   ▾";
        var own = _s?.Ctx.Config.Browser;
        BtnNavProject.Content = (string.IsNullOrWhiteSpace(own) ? "Igual que el de todos los proyectos" : StackDev.Core.Proc.Browsers.Name(own)) + "   ▾";
        BtnNavProject.IsEnabled = _s is not null;
        var effective = string.IsNullOrWhiteSpace(own) ? _state?.Browser : own;
        var nameNav = string.IsNullOrWhiteSpace(effective) ? "Predeterminado" : StackDev.Core.Proc.Browsers.Name(effective);
        BtnNavQuick.Content = Icons.WithGlyph("\uE774", nameNav + "  ▾");
    }

    /// <summary>Quick browser change from the panel: for this project, or (last option) for all.</summary>
    private void NavQuick_Click(object sender, RoutedEventArgs e)
    {
        if (_s is null || _state is null) return;
        var ops = BrowserOptions(v =>
        {
            var copy = ConfigJson.Read(ConfigJson.Write(_s.Ctx.Config));
            copy.Browser = v;
            _s.ApplyConfig(copy);
            RefreshBrowsers();
        }, "Igual que el de todos los proyectos");
        ops.Add(("Cambiar el de todos los proyectos…", () => Dispatcher.InvokeAsync(() =>
            ShowMenu(BtnNavQuick, BrowserOptions(v => { _state.Browser = v; RefreshBrowsers(); }, null)))));
        ShowMenu(BtnNavQuick, ops);
    }

    private List<(string Text, Action Operation)> BrowserOptions(Action<string?> put, string? textWithoutValue)
    {
        var ops = new List<(string, Action)>();
        if (textWithoutValue is not null) ops.Add((textWithoutValue, () => put(null)));
        ops.Add(("Predeterminado del sistema", () => put("")));
        foreach (var n in StackDev.Core.Proc.Browsers.Installed())
        {
            var id = n.Id;
            ops.Add((n.Name, () => put(id)));
        }
        ops.Add(("Otro programa…", () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Elige el navegador", Filter = "Programas (*.exe)|*.exe" };
            if (dlg.ShowDialog(this) == true) put(dlg.FileName);
        }));
        return ops;
    }

    private void NavGlobal_Click(object sender, RoutedEventArgs e)
    {
        if (_state is null) return;
        ShowMenu(BtnNavGlobal, BrowserOptions(v => { _state.Browser = v; RefreshBrowsers(); }, null));
    }

    private void NavProject_Click(object sender, RoutedEventArgs e)
    {
        if (_s is null) return;
        ShowMenu(BtnNavProject, BrowserOptions(v =>
        {
            var copy = ConfigJson.Read(ConfigJson.Write(_s.Ctx.Config));
            copy.Browser = v;
            _s.ApplyConfig(copy);
            RefreshBrowsers();
        }, "Igual que el de todos los proyectos"));
    }

    /// <summary>Header button: opens the project's web app and/or API in the chosen browser.</summary>
    private void Browser_Click(object sender, RoutedEventArgs e)
    {
        if (_s is null) return;
        var s = _s;
        var items = new List<(string Text, Action Operation)>();
        var openable = new List<ProjectComponent>();
        foreach (var (comp, name) in new[] { (ProjectComponent.Web, "Web"), (ProjectComponent.Api, "API") })
        {
            if (!s.Ctx.Has(comp) || s.Ctx.UrlToOpen(comp) is not { } url) continue;
            var c = comp;
            openable.Add(c);
            items.Add(($"{name}    {url}", () => s.Services.OpenInBrowser(c)));
        }
        if (items.Count == 0) { _ui.Write("Este proyecto no tiene una URL de web o API para abrir.", Level.Warning); return; }
        if (items.Count == 1) { items[0].Operation(); return; }
        items.Add(("Abrir las dos", () => { foreach (var c2 in openable) s.Services.OpenInBrowser(c2); }));
        ShowMenu(BtnBrowser, items);
    }

    private void NavTest_Click(object sender, RoutedEventArgs e)
    {
        if (_s is null) return;
        var url = _s.Ctx.UrlToOpen(ProjectComponent.Web) ?? _s.Ctx.UrlToOpen(ProjectComponent.Api) ?? "https://example.com";
        _s.Executor.OpenBrowser(url);
    }

    private async void CfgComponents_Click(object sender, RoutedEventArgs e)
    {
        if (_s is null || _busy) return;
        var fresh = ComponentsDialog.Show(this, _s);
        if (fresh is null) return;
        var s = _s;
        await RunAsync("Guardar componentes", x => { x.ApplyConfig(fresh); return true; });
        _mon?.Refresh();
        BuildAll();
        _ui.Write("Componentes actualizados.", Level.Ok);
    }

    private void CfgMove_Click(object sender, RoutedEventArgs e)
    {
        if (_s is null) return;
        if (!_s.Store.InRepo) { _ui.Write("La configuración ya está fuera del proyecto.", Level.Info); return; }
        var destination = _s.Store.CopyOut();
        _s.Store.Load();
        RefreshConfig();
        _ui.Write($"Copiada a {destination}. Desde ahora se usa esa.", Level.Ok);
        _ui.Write("El dev-menu.json de la carpeta del proyecto no se borró: elimínalo tú (o agrégalo al .gitignore).", Level.Warning);
    }

    private void CfgEdit_Click(object sender, RoutedEventArgs e) => _s?.OpenConfigEditor();

    private void CfgRedetect_Click(object sender, RoutedEventArgs e)
    {
        if (_s is null) return;
        if (!Dialogs.Confirm(this, "Esto reemplaza dev-menu.json (queda una copia .bak). ¿Seguir?", false)) return;
        _ = Redetect();
    }

    private async Task Redetect()
    {
        await RunAsync("Detectar de nuevo", s => { s.Redetect(); return true; });
        BuildAll();
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            _ui.Write($"No pude abrir {path}: {ex.Message}", Level.Error);
        }
    }

    private void CfgLogs_Click(object sender, RoutedEventArgs e)
    {
        if (_s is not null) OpenFolder(Path.GetDirectoryName(_s.Services.LogPath("api"))!);
    }

    private void CfgStatus_Click(object sender, RoutedEventArgs e) => OpenFolder(_state.Dir);

    // ------------------------------------------------------------ Project and closing

    // ------------------------------------------------------------ Barra superior propia

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ------------------------------------------------------------ Background (system tray)

    /// <summary>The X does not close: it sends the application to the tray and everything (terminals, API, emulator) stays alive. Shift+click exits completely.</summary>
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) Exit(); else HideToTray();
    }

    private void CreateTray()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/stackdev.ico"));
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Text = "stackdev",
                Icon = info is null ? null : new System.Drawing.Icon(info.Stream),
                Visible = true,
            };
            _tray.MouseUp += (_, ev) =>
            {
                if (ev.Button != System.Windows.Forms.MouseButtons.Right) return;
                var at = System.Windows.Forms.Cursor.Position;
                Dispatcher.Invoke(() => TrayMenu.Open(at.X, at.Y, new (string, Action?)[]
                {
                    ("Abrir stackdev", ShowFromTray), ("", null), ("Salir", Exit),
                }));
            };
            _tray.MouseClick += (_, ev) =>
            {
                if (ev.Button == System.Windows.Forms.MouseButtons.Left) Dispatcher.Invoke(ShowFromTray);
            };
        }
        catch (Exception) { _tray = null; }   // without a tray icon the X goes back to closing normally
    }

    private void HideToTray()
    {
        if (_tray is null) { Exit(); return; }
        Hide();
        if (!_trayNotified)
        {
            _trayNotified = true;
            _tray.ShowBalloonTip(2500, "stackdev sigue en segundo plano",
                "Haz clic en el icono para volver a abrirlo. Clic derecho → Salir para cerrarlo del todo.",
                System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;   // brings it to the front even if another window has focus
    }

    private void Exit()
    {
        _quit = true;
        if (!IsVisible) Show();   // the "what do I do with what is running?" dialog needs the window to be visible
        Close();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        // Maximised, a window with its own frame overshoots the screen edge by a few pixels.
        var rb = SystemParameters.WindowResizeBorderThickness;
        Root.Margin = maximized ? new Thickness(rb.Left, rb.Top, rb.Right, rb.Bottom + 10) : new Thickness(0);
        BtnMaximize.Content = maximized ? "\uE923" : "\uE922";
    }

    private void Sidebar_Click(object sender, RoutedEventArgs e)
    {
        _rail = !_rail;
        ApplyRail();
    }

    // ------------------------------------------------------------ Terminales: mitad, completo u ocultas

    private void ChangeTerminalMode(TerminalMode m)
    {
        _termMode = m;
        if (m == TerminalMode.Full)
        {
            if (NavTerminals.IsChecked != true) { NavTerminals.IsChecked = true; return; }   // Nav_Checked applies the mode
        }
        else if (CurrentView() == ViewKind.Terminals)
        {
            (_s is null ? NavHome : NavPanel).IsChecked = true;   // leave the Terminals view; Nav_Checked applies the mode
            return;
        }
        ApplyTerminalMode();
    }

    private void ApplyTerminalMode(bool createFirst = true)
    {
        var m = _termMode;
        MainArea.Visibility = m == TerminalMode.Full ? Visibility.Collapsed : Visibility.Visible;
        ColMain.MinWidth = m == TerminalMode.Half ? 460 : 0;
        // At half width the panel gives up space: 2/5 for the cards, 3/5 for the terminal
        ColMain.Width = m == TerminalMode.Full ? new GridLength(0)
            : new GridLength(m == TerminalMode.Half && _appliedMode == TerminalMode.Hidden ? 2 : 1, GridUnitType.Star);
        TerminalDock.Visibility = m == TerminalMode.Hidden ? Visibility.Collapsed : Visibility.Visible;
        ColTerminal.MinWidth = m == TerminalMode.Hidden ? 0 : 360;
        ColTerminal.Width = m == TerminalMode.Hidden ? new GridLength(0)
            : new GridLength(m == TerminalMode.Half && _appliedMode == TerminalMode.Hidden ? 3 : 1, GridUnitType.Star);
        TerminalDivider.Visibility = m == TerminalMode.Half ? Visibility.Visible : Visibility.Collapsed;
        _terminals?.MarkComplete(m == TerminalMode.Full);
        Dispatcher.InvokeAsync(() => { AdjustCards(); AdjustHome(); }, DispatcherPriority.Loaded);   // the hidden views keep widths measured for the old split: redo them

        var opens = _appliedMode == TerminalMode.Hidden && m != TerminalMode.Hidden;
        var closes = _appliedMode != TerminalMode.Hidden && m == TerminalMode.Hidden;
        _appliedMode = m;
        if (opens) { _railBefore = _rail; _rail = true; }       // with the terminals open the menu switches to icons
        else if (closes) _rail = _railBefore;
        ApplyRail();
        if (opens && createFirst) _terminals?.OnShow();
    }

    /// <summary>Opens (at half width if needed) a folder's terminal; if there is already a tab for it, shows it.</summary>
    private void OpenTerminal(string label, string folder)
    {
        if (_terminals is null) return;
        if (_termMode == TerminalMode.Hidden)
        {
            _termMode = TerminalMode.Half;
            ApplyTerminalMode(createFirst: false);
        }
        _terminals.Open(label, folder);
    }

    /// <summary>Opens a terminal tab that runs its own command line (ssh, server logs…).</summary>
    private void OpenTerminalCommand(string label, string line)
    {
        if (_terminals is null) return;
        if (_termMode == TerminalMode.Hidden)
        {
            _termMode = TerminalMode.Half;
            ApplyTerminalMode(createFirst: false);
        }
        _terminals.OpenCommand(label, line);
    }

    /// <summary>Saves in dev-menu.json which server this project is uploaded to.</summary>
    private void SaveDeployment(StackDev.Core.Config.DeploymentConfig? d)
    {
        if (_s is null) return;
        var copy = StackDev.Core.Config.ConfigJson.Read(StackDev.Core.Config.ConfigJson.Write(_s.Ctx.Config));
        copy.Deployment = d;
        _s.ApplyConfig(copy);
        _ui.Write("Despliegue del proyecto guardado.", Level.Ok);
    }

    /// <summary>Terminal shortcuts: they work the same with focus on the window or inside a terminal.</summary>
    private void TerminalShortcut(string action, int n)
    {
        if (_terminals is null || _s is null) return;
        if (action == "toggle")
        {
            ChangeTerminalMode(_termMode == TerminalMode.Hidden ? TerminalMode.Half : TerminalMode.Hidden);
            return;
        }
        var wasHidden = _termMode == TerminalMode.Hidden;
        if (wasHidden)
        {
            _termMode = TerminalMode.Half;
            ApplyTerminalMode();          // creates the first terminal if there is none
            if (action is "next" or "prev") return;   // the first one is already shown; do not jump tabs
        }
        _terminals.Shortcut(action, n);
    }

    private void RootTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (_s is not null) OpenTerminal("raíz", _s.Root);
    }

    private (string Label, string Folder)? CardTerminal(string section)
    {
        if (_s is null) return null;
        return section switch
        {
            "POSTGRES" => ("raíz", _s.Root),
            "API" => ("api", TerminalFolder(ProjectComponent.Api, "api", "backend", "server", "src")),
            "WEB" => ("web", TerminalFolder(ProjectComponent.Web, "web", "frontend", "client", "app")),
            "FLUTTER" => ("móvil", TerminalFolder(ProjectComponent.Mobile, "mobile", "movil", "app", "flutter")),
            _ => null,
        };
    }

    /// <summary>
    /// The component's configured folder; if it is the root itself (the commands use relative paths),
    /// the subfolder with the typical name is preferred, so the terminal opens where the work happens.
    /// </summary>
    private string TerminalFolder(ProjectComponent c, params string[] candidates)
    {
        var root = _s!.Root;
        var conf = _s.Ctx.Folder(c);
        if (!string.Equals(Path.GetFullPath(conf).TrimEnd('\\', '/'), Path.GetFullPath(root).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return conf;
        foreach (var n in candidates)
        {
            var dir = Path.Combine(root, n);
            if (Directory.Exists(dir)) return dir;
        }
        return conf;
    }

    private static readonly (string Text, string Glyph)[] NavIcons =
    {
        ("Proyectos", "\uE8B7"),
        ("Panel", "\uE80A"),
        ("Terminales", "\uE756"),
        ("Diagnóstico", "\uE9D9"),
        ("Servidor", "\uE753"),
        ("Condiciones del dispositivo", "\uE717"),
        ("Consola de comandos", "\uE943"),
        ("Configuración", "\uE713"),
    };

    /// <summary>Full side menu (text) or reduced to small icons.</summary>
    private void ApplyRail()
    {
        ColSidebar.Width = new GridLength(_rail ? 60 : 240);
        ProjectCard.Visibility = SidebarFooter.Visibility = _rail ? Visibility.Collapsed : Visibility.Visible;
        SidebarInner.Margin = _rail ? new Thickness(8, 16, 8, 16) : new Thickness(16);
        var buttons = new[] { NavHome, NavPanel, NavTerminals, NavDiagnostics, NavServer, NavCond, NavConsole, NavConfig };
        for (var i = 0; i < buttons.Length; i++)
        {
            var (text, glyph) = NavIcons[i];
            buttons[i].ToolTip = _rail ? text : null;
            buttons[i].Content = _rail
                ? new TextBlock
                {
                    Text = glyph,
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                }
                : WithNavIcon(glyph, text);
        }
    }

    private static object WithNavIcon(string glyph, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily(Icons.Font),
            FontSize = 15,
            Width = 22,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => NavHome.IsChecked = true;

    private bool IsFavorite(string? path)
    {
        var fav = _state.Favorite;
        if (string.IsNullOrWhiteSpace(fav) || string.IsNullOrWhiteSpace(path)) return false;
        return string.Equals(Path.GetFullPath(fav), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Only one can be the favourite: marking another removes the previous one; pressing again removes it.</summary>
    private void ToggleFavorite(string path)
    {
        if (IsFavorite(path))
        {
            _state.Favorite = null;
            _ui.Write("Ya no hay proyecto favorito.", Level.Muted);
        }
        else
        {
            _state.Favorite = Path.GetFullPath(path);
            _ui.Write($"Favorito: {path}. Se abrirá al iniciar la aplicación.", Level.Ok);
        }
        UpdateFavorite();
        if (CurrentView() == ViewKind.Home) RefreshHome();
    }

    // ------------------------------------------------------------ Project chip (~/project on branch*)

    private string? _gitRoot;
    private string? _branch;
    private bool _dirty;
    private DateTime _lastGit = DateTime.MinValue;

    private void PaintChip()
    {
        if (_s is null)
        {
            ProjectChip.Visibility = Visibility.Collapsed;
            TxtWindowTitle.Visibility = Visibility.Visible;
            return;
        }
        ProjectChip.Visibility = Visibility.Visible;
        TxtWindowTitle.Visibility = Visibility.Collapsed;
        var t = new TextBlock { FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        t.Inlines.Add(new Run("~/") { Foreground = P("MutedBrush") });
        t.Inlines.Add(new Run(_s.Ctx.Name) { Foreground = P("TextBrush") });
        if (_branch is not null)
        {
            t.Inlines.Add(new Run("  on  ") { Foreground = P("MutedBrush") });
            t.Inlines.Add(new Run(_branch) { Foreground = P("AccentBrush") });
            if (_dirty) t.Inlines.Add(new Run("*") { Foreground = P("WarnBrush") });
        }
        t.Inlines.Add(new Run("   ▾") { Foreground = P("MutedBrush") });
        ProjectChip.Content = t;
        ProjectChip.ToolTip = $"{_s.Root}\nCambiar de proyecto (Ctrl+P)";
    }

    private async Task UpdateGitAsync()
    {
        var root = _s?.Root;
        if (root is null) return;
        var (branch, dirty) = await Task.Run(() => ReadGit(root));
        if (!string.Equals(root, _s?.Root, StringComparison.OrdinalIgnoreCase)) return;
        _branch = branch;
        _dirty = dirty;
        PaintChip();
    }

    private static (string? Rama, bool Sucio) ReadGit(string root)
    {
        static string? Git(string root, string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("git", $"-C \"{root}\" {args}")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (p is null) return null;
                var output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return null; }
                return p.ExitCode == 0 ? output.Trim() : null;
            }
            catch { return null; }
        }

        var branch = Git(root, "rev-parse --abbrev-ref HEAD");
        if (string.IsNullOrEmpty(branch)) return (null, false);
        if (branch == "HEAD") branch = Git(root, "rev-parse --short HEAD") ?? "HEAD";
        var dirty = !string.IsNullOrEmpty(Git(root, "status --porcelain"));
        return (branch, dirty);
    }

    private void UpdateFavorite()
    {
        BtnBarFavorite.Visibility = _s is null ? Visibility.Collapsed : Visibility.Visible;
        BtnChangeBar.Visibility = Visibility.Collapsed;   // replaced by the project chip
        if (!string.Equals(_gitRoot, _s?.Root, StringComparison.OrdinalIgnoreCase))
        {
            _gitRoot = _s?.Root;
            _branch = null;
            _dirty = false;
            _lastGit = DateTime.UtcNow;
            _ = UpdateGitAsync();
        }
        PaintChip();
        var fav = _s is not null && IsFavorite(_s.Root);
        BtnBarFavorite.Content = fav ? "★" : "☆";
        BtnBarFavorite.Foreground = P(fav ? "WarnBrush" : "MutedBrush");
    }

    private void BarFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (_s is not null) ToggleFavorite(_s.Root);
    }

    private void ChangeProjectClick(object sender, RoutedEventArgs e) => NavHome.IsChecked = true;

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var path = Dialogs.Project(this, _state.LastProject);
        if (path is null) return;
        await OpenFromHome(path);
    }

    private async Task OpenFromHome(string path)
    {
        if (_busy) { _ui.Write("Espera a que termine la acción en curso.", Level.Warning); return; }
        if (!Directory.Exists(path)) { _ui.Write($"Esa carpeta ya no existe: {path}", Level.Warning); RefreshHome(); return; }
        if (_s is not null && string.Equals(Path.GetFullPath(path), _s.Root, StringComparison.OrdinalIgnoreCase))
        {
            NavPanel.IsChecked = true;
            return;
        }
        await OpenProjectAsync(path);
    }

    private void UpdateNav()
    {
        var has = _s is not null;
        TxtBar.IsEnabled = BtnBar.IsEnabled = has && !_busy;
        UpdateFavorite();
        NavPanel.IsEnabled = NavTerminals.IsEnabled = NavDiagnostics.IsEnabled = NavCond.IsEnabled = NavConsole.IsEnabled = NavConfig.IsEnabled = has;
        if (!has)
        {
            TxtProject.Text = "Sin proyecto";
            TxtPath.Text = "";
            TxtMode.Text = "—";
            TxtAvd.Text = "—";
            TxtBarDir.Text = ">";
        }
    }

    private void RefreshHome()
    {
        HomePanel.Children.Clear();
        var list = _state.ReadProjects();
        if (list.Count == 0)
        {
            HomePanel.Children.Add(new TextBlock
            {
                Text = "Aún no has abierto ningún proyecto. Usa «Abrir carpeta…» para empezar.",
                Foreground = P("MutedBrush"),
            });
            return;
        }

        foreach (var path in list)
        {
            var folder = path;
            var exists = Directory.Exists(path);
            var name = Path.GetFileName(path.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name)) name = path;
            var parts = new List<string>();
            if (exists)
            {
                try
                {
                    var st = new ConfigStore(path, _state.ConfigFolder(StateStore.Key(path)));
                    if (st.Exists)
                    {
                        st.Load();
                        if (st.Error is null)
                        {
                            var c = st.Context;
                            name = c.Name;
                            if (c.Has(ProjectComponent.Postgres)) parts.Add("Postgres");
                            if (c.Has(ProjectComponent.Api)) parts.Add("API");
                            if (c.Has(ProjectComponent.Web)) parts.Add("Web");
                            if (c.Has(ProjectComponent.Mobile)) parts.Add("Móvil");
                        }
                    }
                }
                catch (Exception) { /* una configuración dañada no debe romper la lista */ }
            }

            var isOpen = _s is not null && string.Equals(_s.Root, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

            var isFav = IsFavorite(path);
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock { Text = name, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            if (isOpen || !exists)
            {
                var label = new TextBlock
                {
                    Text = isOpen ? "ABIERTO" : "NO EXISTE",
                    FontSize = 10,
                    Foreground = P(isOpen ? "OkBrush" : "WarnBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                };
                Grid.SetColumn(label, 1);
                header.Children.Add(label);
            }
            var star = new Button
            {
                Content = isFav ? "★" : "☆",
                Style = (Style)FindResource("BtnIcon"),
                Foreground = P(isFav ? "WarnBrush" : "MutedBrush"),
                ToolTip = isFav ? "Quitar de favorito" : "Hacer favorito: se abrirá al iniciar la aplicación",
            };
            star.Click += (_, _) => ToggleFavorite(folder);
            Grid.SetColumn(star, 2);
            header.Children.Add(star);

            var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            var open = new Button { Content = isOpen ? "Ir al panel" : "Abrir", Style = (Style)FindResource("BtnChip"), Background = P("AccentBrush"), BorderBrush = P("AccentBrush"), Foreground = P("AccentOnBrush") };
            open.Click += async (_, _) => await OpenFromHome(folder);
            var remove = new Button { Content = "Quitar de la lista", Style = (Style)FindResource("BtnChip") };
            remove.Click += (_, _) => { _state.RemoveProject(folder); RefreshHome(); };
            buttons.Children.Add(open);
            buttons.Children.Add(remove);

            var content = new StackPanel();
            content.Children.Add(header);
            content.Children.Add(new TextBlock { Text = path, FontSize = 11, Foreground = P("MutedBrush"), Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = path });
            if (parts.Count > 0)
                content.Children.Add(new TextBlock { Text = string.Join("  ·  ", parts), FontSize = 12, Foreground = P("MutedBrush"), Margin = new Thickness(0, 8, 0, 0) });
            content.Children.Add(buttons);

            HomePanel.Children.Add(new Border
            {
                Style = (Style)FindResource("Card"),
                Width = 380,
                Margin = new Thickness(0, 0, 16, 16),
                Child = content,
            });
        }
        Dispatcher.InvokeAsync(AdjustHome, DispatcherPriority.Loaded);
    }

    private async void OnClose(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_quit && !_closing && _tray is not null)
        {
            e.Cancel = true;      // Alt+F4 or closing from the taskbar: also to the background
            HideToTray();
            return;
        }
        if (_closing) { _tray?.Dispose(); _terminals?.CloseAll(); _mon?.Dispose(); return; }
        if (_s is null) { _tray?.Dispose(); _terminals?.CloseAll(); return; }

        e.Cancel = true;   // it is decided first what to do with what keeps running
        _quit = false;    // if it ends up cancelled, the next X goes to the background again
        if (_busy)
        {
            if (!Dialogs.Confirm(this, "Hay una acción en curso. ¿Cancelarla y salir?", false)) return;
            _cts?.Cancel();
            while (_busy) await Task.Delay(100);
        }

        var s = _s;
        Snapshot? photo = null;
        _timer.Stop();
        try { photo = await Task.Run(() => Snapshot.Take(s)); } catch (Exception) { }

        if (photo is { AnythingRunning: true })
        {
            var i = Dialogs.Choose(this, "Hay cosas corriendo. ¿Qué hago?",
                new[] { "Detener todo y salir", "Salir y dejar todo corriendo" }, 0);
            if (i < 0) { _timer.Start(); return; }
            if (i == 0)
            {
                await RunAsync("Detener todo", x => x.All.Stop());
            }
        }
        _closing = true;
        Close();
    }
}
