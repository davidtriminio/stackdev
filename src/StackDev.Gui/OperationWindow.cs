using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using StackDev.Core.Deploy;
using StackDev.Core.Ui;

namespace StackDev.Gui;

/// <summary>
/// A window for an operation that takes a while (deploy, provisioning, saving variables): the current step, a progress bar,
/// the log as it happens and, at the end, the outcome. It runs the work on another thread and can ask the user questions through it.
/// </summary>
public sealed class OperationWindow : Window
{
    private const int MaxLines = 3000;

    private readonly TextBlock _step = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Text = "Empezando…" };
    private readonly Ellipse _dot = new() { Width = 10, Height = 10, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, Height = 6, Margin = new Thickness(0, 12, 0, 14) };
    private readonly StackPanel _lines = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBlock _result = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly Border _banner = new() { BorderThickness = new Thickness(4, 0, 0, 0), CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _cancel = new() { Content = "Cancelar", MinWidth = 90, Padding = new Thickness(16, 8, 16, 8) };
    private readonly Button _copy = new() { Content = "Copiar registro", Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _close = new() { Content = "Cerrar", MinWidth = 90, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly StringBuilder _text = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _running = true;

    private OperationWindow(Window? owner, string title, bool canCancel)
    {
        Title = title;
        Owner = owner;
        Width = 780;
        Height = 560;
        MinWidth = 560;
        MinHeight = 380;
        ShowInTaskbar = false;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        TitleBar.Apply(this);
        // A Window subclass does not get the implicit Window style of the theme, so the colors are set here.
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        SetResourceReference(FontFamilyProperty, "UiFont");
        FontSize = 13;
        _dot.Fill = ServerDialogs.P("AccentBrush");
        _banner.Background = ServerDialogs.P("Surface2Brush");
        _banner.Child = _result;
        _bar.Foreground = ServerDialogs.P("AccentBrush");

        _close.Style = ServerDialogs.GetStyle("BtnPrimary");
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

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(_copy);
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_close);
        _cancel.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(_dot);
        header.Children.Add(_step);

        var grid = new Grid { Margin = new Thickness(22) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_bar, 1);
        Grid.SetRow(logFrame, 2);
        Grid.SetRow(_banner, 3);
        Grid.SetRow(buttons, 4);
        grid.Children.Add(header);
        grid.Children.Add(_bar);
        grid.Children.Add(logFrame);
        grid.Children.Add(_banner);
        grid.Children.Add(buttons);
        Content = grid;

        _cancel.Click += (_, _) => { _cancel.IsEnabled = false; _cancel.Content = "Cancelando…"; _cts.Cancel(); };
        _close.Click += (_, _) => Close();
        _copy.Click += (_, _) => { try { Clipboard.SetText(_text.ToString()); } catch (System.Runtime.InteropServices.COMException) { } };
        Closing += (_, e) => { if (_running) e.Cancel = true; };   // not while the work is still running
    }

    /// <summary>Runs <paramref name="work"/> in another thread, showing its progress. Returns its result when the window is closed.</summary>
    public static T Run<T>(Window? owner, string title, bool canCancel, Func<Window, Action<ProgressEvent>, CancellationToken, T> work,
        Func<T, (bool Ok, string Text)> summarize)
    {
        var w = new OperationWindow(owner, title, canCancel);
        T result = default!;
        w.Loaded += async (_, _) =>
        {
            try
            {
                result = await Task.Run(() => work(w, w.Report, w._cts.Token));
                var (ok, text) = summarize(result);
                w.Finish(ok, text);
            }
            catch (Exception ex)
            {
                w.Finish(false, "Error inesperado: " + ex.Message);
            }
        };
        w.ShowDialog();
        return result;
    }

    private void Report(ProgressEvent e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (e.Percent >= 0) _bar.Value = Math.Max(_bar.Value, e.Percent);
            if (e.Level == Level.Title) _step.Text = e.Message;
            AddLine(e);
        }));
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
        var line = new TextBlock
        {
            Text = prefix + e.Message,
            Foreground = brush,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = e.Level == Level.Title ? FontWeights.SemiBold : FontWeights.Normal,
            Margin = new Thickness(0, e.Level == Level.Title ? 8 : 0, 0, 0),
        };
        _lines.Children.Add(line);
        _text.AppendLine(prefix + e.Message);
        if (_lines.Children.Count > MaxLines) _lines.Children.RemoveAt(0);
        _scroll.ScrollToEnd();
    }

    private void Finish(bool ok, string text)
    {
        _running = false;
        _cancel.Visibility = Visibility.Collapsed;
        _close.Visibility = Visibility.Visible;
        _bar.Value = ok ? 100 : _bar.Value;
        var color = ok ? ServerDialogs.P("OkBrush") : ServerDialogs.P("ErrBrush");
        _bar.Foreground = color;
        _dot.Fill = color;
        _step.Text = ok ? "Listo" : "No se pudo completar";
        _result.Text = text;
        _result.Foreground = ServerDialogs.P("TextBrush");
        _banner.BorderBrush = color;
        _banner.Visibility = Visibility.Visible;
        _scroll.ScrollToEnd();
        _text.AppendLine((ok ? "OK: " : "ERROR: ") + text);
        _close.Focus();
    }
}
