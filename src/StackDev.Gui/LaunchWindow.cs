using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using StackDev.Core.Deploy;
using StackDev.Core.Ui;

namespace StackDev.Gui;

public enum StepState { Pending, Running, Ok, Warning, Failed }

/// <summary>What the whole "check and start" flow ended with.</summary>
public sealed record LaunchOutcome(bool Ok, string Text, string? Url = null, bool CreateFirst = false);

/// <summary>
/// One window for the whole flow of checking, fixing and starting an API: the screen behind is blurred, the steps are listed at the top,
/// the log runs below and every question is asked inside this same window (so nothing piles up over anything else).
/// </summary>
public sealed class LaunchWindow : Window
{
    private const int MaxLines = 3000;

    private readonly TextBlock _title = new() { FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _subtitle = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    private readonly StackPanel _steps = new() { Margin = new Thickness(0, 14, 0, 12) };
    private readonly List<(TextBlock Icon, TextBlock Name, TextBlock Detail)> _rows = new();
    private readonly StackPanel _lines = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Border _question = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(16), Visibility = Visibility.Collapsed };
    private readonly StackPanel _questionBody = new();
    private readonly TextBlock _result = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly Border _banner = new() { BorderThickness = new Thickness(4, 0, 0, 0), CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _copy = new() { Content = "Copiar registro", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _open = new() { Content = "Abrir en el navegador ▾", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _cancel = new() { Content = "Cancelar", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _close = new() { Content = "Cerrar", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly StringBuilder _text = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _running = true;
    private readonly Dictionary<int, System.Diagnostics.Stopwatch> _stepStart = new();
    private string? _url;

    private LaunchWindow(Window? owner, string title, string subtitle, IReadOnlyList<string> stepNames)
    {
        Title = title;
        Owner = owner;
        Width = 800;
        Height = 640;
        MinWidth = 600;
        MinHeight = 440;
        ShowInTaskbar = false;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        TitleBar.Apply(this);
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        SetResourceReference(FontFamilyProperty, "UiFont");
        FontSize = 13;

        _title.Text = title;
        _subtitle.Text = subtitle;
        _subtitle.Foreground = ServerDialogs.P("MutedBrush");
        _banner.Background = ServerDialogs.P("Surface2Brush");
        _banner.Child = _result;
        _question.Background = ServerDialogs.P("Surface2Brush");
        _question.BorderBrush = ServerDialogs.P("AccentBrush");
        _question.BorderThickness = new Thickness(1);
        _question.Child = new ScrollViewer { Content = _questionBody, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _close.Style = ServerDialogs.GetStyle("BtnPrimary");

        foreach (var name in stepNames)
        {
            var icon = new TextBlock { Width = 24, FontSize = 14, Text = "○", Foreground = ServerDialogs.P("MutedBrush"), VerticalAlignment = VerticalAlignment.Top };
            var label = new TextBlock { FontSize = 13, Text = name, Foreground = ServerDialogs.P("MutedBrush") };
            var detail = new TextBlock { FontSize = 11.5, Foreground = ServerDialogs.P("MutedBrush"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            var text = new StackPanel();
            text.Children.Add(label);
            text.Children.Add(detail);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(icon);
            row.Children.Add(text);
            _steps.Children.Add(row);
            _rows.Add((icon, label, detail));
        }

        _scroll.Content = _lines;
        var logFrame = new Border
        {
            Background = ServerDialogs.P("Surface2Brush"),
            BorderBrush = ServerDialogs.P("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Child = _scroll,
        };
        var center = new Grid();
        center.Children.Add(logFrame);
        center.Children.Add(_question);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(_copy);
        buttons.Children.Add(_open);
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_close);

        var head = new StackPanel();
        head.Children.Add(_title);
        head.Children.Add(_subtitle);
        head.Children.Add(_steps);

        var grid = new Grid { Margin = new Thickness(22) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(center, 1);
        Grid.SetRow(_banner, 2);
        Grid.SetRow(buttons, 3);
        grid.Children.Add(head);
        grid.Children.Add(center);
        grid.Children.Add(_banner);
        grid.Children.Add(buttons);
        Content = grid;

        _close.Click += (_, _) => Close();
        _cancel.Click += (_, _) =>
        {
            _cancel.IsEnabled = false;
            _cancel.Content = "Cancelando…";
            AddLine(new ProgressEvent("", "Cancelando: detengo la conexión con el servidor…", Level.Warning));
            _cts.Cancel();
        };
        _copy.Click += (_, _) => { try { Clipboard.SetText(_text.ToString()); } catch (System.Runtime.InteropServices.COMException) { } };
        _open.Click += async (_, _) => { if (_url is not null) await BrowserMenu.ShowAsync(_open, _url); };
        Closing += (_, e) => { if (_running) e.Cancel = true; };
    }

    /// <summary>Opens the window (blurring the one behind), runs <paramref name="work"/> on another thread and returns its outcome once the window is closed.</summary>
    public static LaunchOutcome Run(Window? owner, string title, string subtitle, IReadOnlyList<string> stepNames, Func<LaunchWindow, CancellationToken, LaunchOutcome> work)
    {
        var w = new LaunchWindow(owner, title, subtitle, stepNames);
        var behind = owner?.Content as UIElement;
        if (behind is not null) behind.Effect = new BlurEffect { Radius = 12 };
        var outcome = new LaunchOutcome(false, "No se completó.");
        w.Loaded += async (_, _) =>
        {
            try
            {
                outcome = await Task.Run(() => work(w, w._cts.Token));
            }
            catch (Exception ex)
            {
                outcome = new LaunchOutcome(false, "Error inesperado: " + ex.Message);
            }
            w.Finish(outcome);
        };
        try { w.ShowDialog(); }
        finally { if (behind is not null) behind.Effect = null; }
        return outcome;
    }

    // ------------------------------------------------------------ called from the worker thread

    public void Step(int index, StepState state, string? detail = null)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (index < 0 || index >= _rows.Count) return;
            var (icon, name, text) = _rows[index];
            if (state == StepState.Running) _stepStart[index] = System.Diagnostics.Stopwatch.StartNew();
            else if (_stepStart.Remove(index, out var sw))
                AddLine(new ProgressEvent("", $"  «{name.Text}»: {(state == StepState.Ok ? "bien" : state == StepState.Warning ? "con aviso" : "falló")} en {sw.Elapsed.TotalSeconds:0.0} s{(detail is null ? "" : " — " + detail)}", Level.Muted));
            (icon.Text, var brush) = state switch
            {
                StepState.Running => ("▶", ServerDialogs.P("AccentBrush")),
                StepState.Ok => ("✔", ServerDialogs.P("OkBrush")),
                StepState.Warning => ("⚠", ServerDialogs.P("WarnBrush")),
                StepState.Failed => ("✘", ServerDialogs.P("ErrBrush")),
                _ => ("○", ServerDialogs.P("MutedBrush")),
            };
            icon.Foreground = brush;
            name.Foreground = state == StepState.Pending ? ServerDialogs.P("MutedBrush") : ServerDialogs.P("TextBrush");
            if (detail is not null)
            {
                text.Text = detail;
                text.Foreground = brush;
                text.Visibility = Visibility.Visible;
            }
        }));
    }

    /// <summary>
    /// While something slow runs, writes a line every 10 s saying what is awaited, for how long and up to when,
    /// so the window never looks frozen and the log shows where the time went. Dispose it when the wait is over.
    /// </summary>
    public IDisposable Waiting(string what, int limitSec)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new Timer(_ => Info($"  … {what}: {(int)clock.Elapsed.TotalSeconds} s (límite {limitSec} s)", Level.Muted), null, 10_000, 10_000);
        return new WaitHandle(timer, clock, what, this);
    }

    private sealed class WaitHandle : IDisposable
    {
        private readonly Timer _timer; private readonly System.Diagnostics.Stopwatch _clock; private readonly string _what; private readonly LaunchWindow _w;
        public WaitHandle(Timer t, System.Diagnostics.Stopwatch c, string what, LaunchWindow w) { _timer = t; _clock = c; _what = what; _w = w; }
        public void Dispose()
        {
            _timer.Dispose();
            _w.Info($"  {_what}: terminó en {_clock.Elapsed.TotalSeconds:0.0} s", Level.Muted);
        }
    }

    /// <summary>Marks every step still in progress as failed, so nothing is left spinning after an error or a cancel.</summary>
    public void FailRunningSteps(string detail)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var (icon, _, text) in _rows)
            {
                if (icon.Text != "▶") continue;
                icon.Text = "✘";
                icon.Foreground = ServerDialogs.P("ErrBrush");
                text.Text = detail;
                text.Foreground = ServerDialogs.P("ErrBrush");
                text.Visibility = Visibility.Visible;
            }
        }));
    }

    public void Report(ProgressEvent e)
    {
        Dispatcher.BeginInvoke(new Action(() => AddLine(e)));
    }

    public void Info(string message, Level level = Level.Normal) => Report(new ProgressEvent("", message, level));

    /// <summary>Asks a question inside the window and waits for the answer. Returns the index of the option chosen.</summary>
    public int Ask(string text, params string[] options)
    {
        var done = new TaskCompletionSource<int>();
        using var _ = _cts.Token.Register(() => done.TrySetResult(-1));   // cancelling the window also answers any open question
        Dispatcher.Invoke(() =>
        {
            _questionBody.Children.Clear();
            _questionBody.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 0, 0, 14) });
            _questionBody.Children.Add(ButtonsFor(options, i => done.TrySetResult(i)));
            _question.Visibility = Visibility.Visible;
        });
        var answer = done.Task.GetAwaiter().GetResult();
        Dispatcher.Invoke(() => _question.Visibility = Visibility.Collapsed);
        return answer;
    }

    /// <summary>Asks for values the user has to provide (one text box per name, with a hint). Null when the user cancels.</summary>
    public Dictionary<string, string>? AskValues(string text, IReadOnlyList<string> names, Func<string, string> hint, string okText, string cancelText)
    {
        var done = new TaskCompletionSource<Dictionary<string, string>?>();
        using var _ = _cts.Token.Register(() => done.TrySetResult(null));
        Dispatcher.Invoke(() =>
        {
            _questionBody.Children.Clear();
            _questionBody.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 0, 0, 12) });
            var boxes = new List<(string Name, TextBox Box)>();
            foreach (var n in names)
            {
                _questionBody.Children.Add(new TextBlock { Text = n, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 1) });
                _questionBody.Children.Add(new TextBlock { Text = hint(n), FontSize = 11, Foreground = ServerDialogs.P("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3) });
                var box = new TextBox { Margin = new Thickness(0, 0, 0, 6) };
                _questionBody.Children.Add(box);
                boxes.Add((n, box));
            }
            var error = ServerDialogs.Error();
            _questionBody.Children.Add(error);
            _questionBody.Children.Add(ButtonsFor(new[] { okText, cancelText }, i =>
            {
                if (i != 0) { done.TrySetResult(null); return; }
                if (boxes.Any(b => b.Box.Text.Trim().Length == 0 || b.Box.Text.Contains('\n')))
                {
                    error.Text = "Escribe un valor en cada casilla (sin saltos de línea).";
                    error.Visibility = Visibility.Visible;
                    return;
                }
                done.TrySetResult(boxes.ToDictionary(b => b.Name, b => b.Box.Text.Trim()));
            }));
            _question.Visibility = Visibility.Visible;
            if (boxes.Count > 0) boxes[0].Box.Focus();
        });
        var answer = done.Task.GetAwaiter().GetResult();
        Dispatcher.Invoke(() => _question.Visibility = Visibility.Collapsed);
        return answer;
    }

    private StackPanel ButtonsFor(IReadOnlyList<string> options, Action<int> chosen)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        for (var i = 0; i < options.Count; i++)
        {
            var index = i;
            var b = new Button { Content = options[i], MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
            if (i == 0) b.Style = ServerDialogs.GetStyle("BtnPrimary");
            b.Click += (_, _) => chosen(index);
            row.Children.Add(b);
        }
        return row;
    }

    private void AddLine(ProgressEvent e)
    {
        var brush = e.Level switch
        {
            Level.Title => ServerDialogs.P("AccentBrush"),
            Level.Ok => ServerDialogs.P("OkBrush"),
            Level.Warning => ServerDialogs.P("WarnBrush"),
            Level.Error => ServerDialogs.P("ErrBrush"),
            Level.Muted => ServerDialogs.P("MutedBrush"),
            _ => ServerDialogs.P("TextBrush"),
        };
        var prefix = e.Level == Level.Title ? "▶ " : "";
        _lines.Children.Add(new TextBlock
        {
            Text = prefix + e.Message,
            Foreground = brush,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = e.Level == Level.Title ? FontWeights.SemiBold : FontWeights.Normal,
            Margin = new Thickness(0, e.Level == Level.Title ? 8 : 0, 0, 0),
        });
        _text.AppendLine(prefix + e.Message);
        if (_lines.Children.Count > MaxLines) _lines.Children.RemoveAt(0);
        _scroll.ScrollToEnd();
    }

    private void Finish(LaunchOutcome outcome)
    {
        _running = false;
        FailRunningSteps(outcome.Ok ? "" : outcome.Text);
        _cancel.Visibility = Visibility.Collapsed;
        _question.Visibility = Visibility.Collapsed;
        _close.Visibility = Visibility.Visible;
        var color = outcome.Ok ? ServerDialogs.P("OkBrush") : ServerDialogs.P("ErrBrush");
        _result.Text = outcome.Text;
        _result.Foreground = ServerDialogs.P("TextBrush");
        _banner.BorderBrush = color;
        _banner.Visibility = Visibility.Visible;
        if (outcome.Url is not null) { _url = outcome.Url; _open.Visibility = Visibility.Visible; }
        _scroll.ScrollToEnd();
        _text.AppendLine((outcome.Ok ? "OK: " : "ERROR: ") + outcome.Text);
        _close.Focus();
    }
}
