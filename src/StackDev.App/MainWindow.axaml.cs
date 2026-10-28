using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using StackDev.Core;
using StackDev.Core.Config;
using StackDev.Core.Services;
using StackDev.Core.State;
using StackDev.Core.Ui;

namespace StackDev.App;

public sealed partial class MainWindow : Window
{
    private readonly StateStore _state;
    private readonly AvaloniaUi _ui;
    private readonly List<ServiceCard> _cards = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Session? _s;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _refreshing;

    public MainWindow() : this(null, null) { }

    /// <param name="state">Where the personal state lives; tests pass a temporary one.</param>
    public MainWindow(string? projectPath, StateStore? state = null)
    {
        _state = state ?? StateStore.Open();
        InitializeComponent();
        _ui = new AvaloniaUi(this, LogLines, LogScroll);

        OpenFolder.Click += async (_, _) => await PickFolderAsync();
        ClearLog.Click += (_, _) => _ui.Clear();
        CancelAction.Click += (_, _) => _cts?.Cancel();
        Recent.SelectionChanged += (_, _) =>
        {
            if (Recent.SelectedItem is string p && !string.Equals(p, _s?.Root, StringComparison.OrdinalIgnoreCase)) OpenProject(p);
        };
        _timer.Tick += (_, _) => RefreshStates();
        Closing += (_, _) => { _timer.Stop(); _cts?.Cancel(); };

        LoadRecent();
        Opened += (_, _) =>
        {
            var start = projectPath ?? _state.LastProject;
            if (!string.IsNullOrWhiteSpace(start) && Directory.Exists(start)) OpenProject(start);
        };
    }

    private void LoadRecent()
    {
        Recent.ItemsSource = _state.ReadProjects().Where(Directory.Exists).ToList();
    }

    private async Task PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Carpeta del proyecto", AllowMultiple = false });
        if (folders.Count == 1 && folders[0].TryGetLocalPath() is { } path) OpenProject(path);
    }

    // ------------------------------------------------------------ Opening a project

    private void OpenProject(string root)
    {
        if (_busy) { _ui.Write("Espera a que termine la acción en curso.", Level.Warning); return; }
        _timer.Stop();
        _ui.Write($"Abriendo {root}…", Level.Title);
        _busy = true;
        CancelAction.IsEnabled = false;
        Task.Run(() =>
        {
            try
            {
                var s = new Session(root, _state, _ui);
                s.Start();
                _state.AddProject(s.Root);
                _state.LastProject = s.Root;
                Dispatcher.UIThread.Post(() => ShowProject(s));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _ui.Write($"No pude abrir el proyecto: {ex.Message}", Level.Error);
            }
            finally { Dispatcher.UIThread.Post(() => _busy = false); }
        });
    }

    private void ShowProject(Session s)
    {
        _s = s;
        Heading.Text = string.IsNullOrWhiteSpace(s.Ctx.Config.Name) ? Path.GetFileName(s.Root) : s.Ctx.Config.Name;
        Subtitle.Text = s.Root;
        LoadRecent();

        Cards.Children.Clear();
        _cards.Clear();
        foreach (var (component, title) in new[] { (ProjectComponent.Api, "API"), (ProjectComponent.Web, "Web") })
        {
            if (!s.Ctx.Has(component)) continue;
            var card = new ServiceCard(component, title, OnAction);
            _cards.Add(card);
            Cards.Children.Add(card);
        }
        if (_cards.Count == 0)
            _ui.Write("No reconocí ninguna API ni web en esta carpeta. Revisa dev-menu.json o agrega un perfil (ver CONTRIBUTING.md).", Level.Warning);
        else
            _ui.Write($"Listo: {_cards.Count} servicio(s) en {s.Root}.", Level.Ok);
        RefreshStates();
        _timer.Start();
    }

    // ------------------------------------------------------------ States and actions

    private void RefreshStates()
    {
        if (_s is not { } s || _refreshing) return;
        _refreshing = true;
        Task.Run(() => _cards.Select(c => (Card: c, State: s.Services.State(c.Component))).ToList())
            .ContinueWith(t =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _refreshing = false;
                    if (t.IsCompletedSuccessfully && ReferenceEquals(s, _s)) foreach (var (card, st) in t.Result) card.Update(st, s.Ctx.Url(card.Component));
                });
            });
    }

    private void OnAction(ProjectComponent component, string action)
    {
        if (_s is not { } s) return;
        var key = ServiceManager.KeyOf(component);
        switch (action)
        {
            case "start": Run(s, $"Iniciar {key}", () => { if (component == ProjectComponent.Api) s.Services.StartApi(); else s.Services.StartWeb(); }); break;
            case "stop": Run(s, $"Detener {key}", () => s.Services.Stop(component)); break;
            case "restart": Run(s, $"Reiniciar {key}", () => s.Services.Restart(component)); break;
            case "open": Run(s, $"Abrir {key}", () => s.Services.OpenInBrowser(component)); break;
            case "log": Run(s, $"Log de {key} (Cancelar acción para dejar de seguirlo)", () => s.Services.Follow(key, 200, s.Ct, l => _ui.Write(l, Level.Muted))); break;
        }
    }

    /// <summary>Runs one action at a time in the background; "Cancelar acción" stops it.</summary>
    private void Run(Session s, string title, Action work)
    {
        if (_busy) { _ui.Write("Ya hay una acción en curso: espera o cancélala.", Level.Warning); return; }
        _busy = true;
        _cts = new CancellationTokenSource();
        s.Ct = _cts.Token;
        CancelAction.IsEnabled = true;
        _ui.Write(title + "…", Level.Title);
        Task.Run(() =>
        {
            try { work(); }
            catch (OperationCanceledException) { _ui.Write("Cancelado.", Level.Warning); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            { _ui.Write($"Falló: {ex.Message}", Level.Error); }
            finally
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _busy = false;
                    CancelAction.IsEnabled = false;
                    _cts?.Dispose();
                    _cts = null;
                    RefreshStates();
                });
            }
        });
    }
}
