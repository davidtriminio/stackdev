using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using StackDev.Core.Ui;

namespace StackDev.App;

/// <summary>
/// The window's "console": the logic's messages go to the output panel and its questions are dialogs.
/// Safe to call from any thread; questions block the calling (background) thread until they are answered.
/// </summary>
public sealed class AvaloniaUi : IConsoleUi
{
    private const int MaxLines = 3000;
    private readonly Window _owner;
    private readonly Panel _lines;
    private readonly ScrollViewer _scroll;

    public AvaloniaUi(Window owner, Panel lines, ScrollViewer scroll)
    {
        _owner = owner;
        _lines = lines;
        _scroll = scroll;
    }

    public bool Interactive => true;

    public static IBrush ColorOf(Level level) => level switch
    {
        Level.Title => Brush.Parse("#A5B4FC"),
        Level.Info => Brush.Parse("#7DD3FC"),
        Level.Ok => Brush.Parse("#4ADE80"),
        Level.Warning => Brush.Parse("#FBBF24"),
        Level.Error => Brush.Parse("#F87171"),
        Level.Muted => Brush.Parse("#8B93A5"),
        _ => Brush.Parse("#E6E8EE"),
    };

    public void Write(string text, Level level = Level.Normal) => Dispatcher.UIThread.Post(() =>
    {
        foreach (var line in text.Split('\n'))
        {
            _lines.Children.Add(new SelectableTextBlock
            {
                Text = line.TrimEnd('\r'),
                Foreground = ColorOf(level),
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = level == Level.Title ? FontWeight.SemiBold : FontWeight.Normal,
            });
        }
        while (_lines.Children.Count > MaxLines) _lines.Children.RemoveAt(0);
        _scroll.ScrollToEnd();
    });

    public void WriteInline(string text) => Dispatcher.UIThread.Post(() =>
    {
        if (_lines.Children.LastOrDefault() is SelectableTextBlock last) last.Text += text;
        else Write(text, Level.Muted);
    });

    public bool Confirm(string question, bool byDefault = true) =>
        Dispatcher.UIThread.InvokeAsync(() => Dialogs.Confirm(_owner, question, byDefault)).GetAwaiter().GetResult();

    public string? Ask(string question, string? byDefault = null) =>
        Dispatcher.UIThread.InvokeAsync(() => Dialogs.Ask(_owner, question, byDefault)).GetAwaiter().GetResult();

    public int Choose(string title, IReadOnlyList<string> options, int byDefault = -1) =>
        Dispatcher.UIThread.InvokeAsync(() => Dialogs.Choose(_owner, title, options, byDefault)).GetAwaiter().GetResult();

    public void Clear() => Dispatcher.UIThread.Post(() => _lines.Children.Clear());
}
