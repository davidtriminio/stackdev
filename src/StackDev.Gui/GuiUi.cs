using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using StackDev.Core.Ui;

namespace StackDev.Gui;

/// <summary>
/// The window's "console": the logic's messages go to the output panel and the questions are dialogs.
/// It can be called from any thread (actions run in the background).
/// </summary>
public sealed class GuiUi : IConsoleUi
{
    private const int MaxBlocks = 4000;
    private readonly Window _window;
    private readonly RichTextBox _output;

    public GuiUi(Window window, RichTextBox output)
    {
        _window = window;
        _output = output;
    }

    private Dispatcher D => _window.Dispatcher;

    public bool Interactive => true;

    private static Brush ColorOf(Level n) => n switch
    {
        Level.Title => MakeBrush("#A5B4FC"),
        Level.Info => MakeBrush("#7DD3FC"),
        Level.Ok => MakeBrush("#4ADE80"),
        Level.Warning => MakeBrush("#FBBF24"),
        Level.Error => MakeBrush("#F87171"),
        Level.Muted => MakeBrush("#8B93A5"),
        _ => MakeBrush("#E6E8EE"),
    };

    private static Brush MakeBrush(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    public void Write(string text, Level level = Level.Normal)
    {
        D.InvokeAsync(() =>
        {
            foreach (var line in text.Split('\n'))
            {
                var p = new Paragraph(new Run(line.TrimEnd('\r')) { Foreground = ColorOf(level) });
                _output.Document.Blocks.Add(p);
            }
            while (_output.Document.Blocks.Count > MaxBlocks && _output.Document.Blocks.FirstBlock is { } first)
                _output.Document.Blocks.Remove(first);
            _output.ScrollToEnd();
        });
    }

    public void WriteInline(string text)
    {
        D.InvokeAsync(() =>
        {
            if (_output.Document.Blocks.LastBlock is Paragraph last) last.Inlines.Add(new Run(text) { Foreground = ColorOf(Level.Muted) });
            else _output.Document.Blocks.Add(new Paragraph(new Run(text)));
            _output.ScrollToEnd();
        });
    }

    public bool Confirm(string question, bool byDefault = true) =>
        D.Invoke(() => Dialogs.Confirm(_window, question, byDefault));

    public string? Ask(string question, string? byDefault = null) =>
        D.Invoke(() => Dialogs.Ask(_window, question, byDefault));

    public int Choose(string title, IReadOnlyList<string> options, int byDefault = -1) =>
        D.Invoke(() => Dialogs.Choose(_window, title, options, byDefault));

    public void Clean() => D.InvokeAsync(() => _output.Document.Blocks.Clear());
}
