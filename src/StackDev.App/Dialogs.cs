using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace StackDev.App;

/// <summary>Small question windows (confirm, ask, choose) built in code, so the logic can ask things through <see cref="AvaloniaUi"/>.</summary>
internal static class Dialogs
{
    private static Window Base(string title) => new()
    {
        Title = title,
        Width = 460,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
    };

    private static Button Option(string text, bool primary)
    {
        var b = new Button { Content = text, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (primary) b.Classes.Add("primary");
        return b;
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };
        row.Children.AddRange(buttons);
        return row;
    }

    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    /// <summary>A question with one button per option. Returns the index pressed, or -1 if the window is closed.</summary>
    public static async Task<int> Pick(Window owner, string text, IReadOnlyList<string> options, int primary)
    {
        var w = Base("stackdev");
        var answer = -1;
        var buttons = options.Select((o, i) =>
        {
            var b = Option(o, i == primary);
            b.Click += (_, _) => { answer = i; w.Close(); };
            return b;
        }).ToArray();
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(Text(text));
        root.Children.Add(Buttons(buttons));
        w.Content = root;
        await w.ShowDialog(owner);
        return answer;
    }

    public static async Task<bool> Confirm(Window owner, string question, bool byDefault) =>
        await Pick(owner, question, new[] { "Sí", "No" }, byDefault ? 0 : 1) == 0;

    /// <summary>A text box with a question. Returns null when cancelled or left empty.</summary>
    public static async Task<string?> Ask(Window owner, string question, string? byDefault)
    {
        var w = Base("stackdev");
        string? result = null;
        var box = new TextBox { Text = byDefault ?? "", Margin = new Thickness(0, 10, 0, 0) };
        var ok = Option("Aceptar", true);
        var cancel = Option("Cancelar", false);
        ok.Click += (_, _) => { result = box.Text; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(Text(question));
        root.Children.Add(box);
        root.Children.Add(Buttons(cancel, ok));
        w.Content = root;
        w.Opened += (_, _) => box.Focus();
        await w.ShowDialog(owner);
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    /// <summary>A list to choose from. Returns the index, or -1 when cancelled.</summary>
    public static async Task<int> Choose(Window owner, string title, IReadOnlyList<string> options, int byDefault)
    {
        var w = Base("stackdev");
        var chosen = -1;
        var list = new ListBox { ItemsSource = options.ToList(), SelectedIndex = byDefault >= 0 ? byDefault : 0, MaxHeight = 300, Margin = new Thickness(0, 10, 0, 0) };
        var ok = Option("Elegir", true);
        var cancel = Option("Cancelar", false);
        ok.Click += (_, _) => { chosen = list.SelectedIndex; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        list.DoubleTapped += (_, _) => { chosen = list.SelectedIndex; w.Close(); };
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(Text(title));
        root.Children.Add(list);
        root.Children.Add(Buttons(cancel, ok));
        w.Content = root;
        await w.ShowDialog(owner);
        return chosen;
    }
}
