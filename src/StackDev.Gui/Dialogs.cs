using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StackDev.Core.Detection;

namespace StackDev.Gui;

/// <summary>Ventanas pequeñas (confirmar, preguntar, elegir, abrir proyecto) construidas en código y con el tema oscuro.</summary>
public static class Dialogs
{
    private static Style GetStyle(string key) => (Style)Application.Current.FindResource(key);

    private static Window Base(Window? owner, string title, double width)
    {
        var window = new Window
        {
            Title = title,
            Owner = owner,
            Width = width,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        TitleBar.Apply(window);
        return window;
    }

    private static Button Button(string text, bool primary, bool cancel = false)
    {
        var b = new Button { Content = text, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        if (primary) { b.Style = GetStyle("BtnPrimary"); b.IsDefault = true; }
        b.IsCancel = cancel;
        return b;
    }

    private static StackPanel ButtonRow(params Button[] buttons)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        foreach (var b in buttons) p.Children.Add(b);
        return p;
    }

    public static bool Confirm(Window? owner, string question, bool byDefault = true)
    {
        var w = Base(owner, "stackdev", 460);
        var yes = Button("Sí", byDefault);
        var no = Button("No", !byDefault, cancel: true);
        var result = false;
        yes.Click += (_, _) => { result = true; w.Close(); };
        no.Click += (_, _) => { result = false; w.Close(); };
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
        root.Children.Add(ButtonRow(no, yes));
        w.Content = root;
        w.ShowDialog();
        return result;
    }

    public static string? Ask(Window? owner, string question, string? byDefault)
    {
        var w = Base(owner, "stackdev", 480);
        var box = new TextBox { Text = byDefault ?? "", Margin = new Thickness(0, 10, 0, 0) };
        var ok = Button("Aceptar", true);
        var cancel = Button("Cancelar", false, cancel: true);
        string? result = null;
        ok.Click += (_, _) => { result = box.Text.Trim(); w.Close(); };
        cancel.Click += (_, _) => w.Close();
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
        root.Children.Add(box);
        root.Children.Add(ButtonRow(cancel, ok));
        w.Content = root;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        w.ShowDialog();
        return string.IsNullOrEmpty(result) ? null : result;
    }

    /// <summary>Devuelve el índice elegido o -1.</summary>
    public static int Choose(Window? owner, string title, IReadOnlyList<string> options, int byDefault)
    {
        var w = Base(owner, "stackdev", 480);
        var list = new ListBox { MaxHeight = 320, Margin = new Thickness(0, 12, 0, 0), BorderThickness = new Thickness(1) };
        foreach (var o in options) list.Items.Add(o);
        list.SelectedIndex = byDefault >= 0 && byDefault < options.Count ? byDefault : (options.Count > 0 ? 0 : -1);
        var ok = Button("Elegir", true);
        var cancel = Button("Cancelar", false, cancel: true);
        var result = -1;
        ok.Click += (_, _) => { result = list.SelectedIndex; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        list.MouseDoubleClick += (_, _) => { if (list.SelectedIndex >= 0) { result = list.SelectedIndex; w.Close(); } };
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, FontSize = 14, FontWeight = FontWeights.SemiBold });
        root.Children.Add(list);
        root.Children.Add(ButtonRow(cancel, ok));
        w.Content = root;
        w.ShowDialog();
        return result;
    }

    /// <summary>Elegir la carpeta de un proyecto: escribirla, examinar, o buscar proyectos dentro de una carpeta.</summary>
    public static string? Project(Window? owner, string? last)
    {
        var w = Base(owner, "Abrir proyecto", 620);
        var path = new TextBox { Text = last ?? "" };
        var browse = new Button { Content = "Examinar…", Margin = new Thickness(8, 0, 0, 0) };
        var search = new Button { Content = "Buscar proyectos aquí", Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        var list = new ListBox { Height = 190, Margin = new Thickness(0, 10, 0, 0) };
        var ok = Button("Abrir", true);
        var cancel = Button("Cancelar", false, cancel: true);
        string? result = null;

        void Fill(string? baseDir, int depth)
        {
            list.Items.Clear();
            if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir)) return;
            var previous = w.Cursor;
            w.Cursor = Cursors.Wait;
            try { foreach (var p in Explorer.SearchProjects(baseDir, depth)) list.Items.Add(p); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            finally { w.Cursor = previous; }
        }

        browse.Click += (_, _) =>
        {
            var d = new Microsoft.Win32.OpenFolderDialog { Title = "Carpeta del proyecto" };
            if (Directory.Exists(path.Text)) d.InitialDirectory = path.Text;
            if (d.ShowDialog(w) == true) { path.Text = d.FolderName; Fill(d.FolderName, 3); }
        };
        search.Click += (_, _) => Fill(path.Text.Trim().Trim('"'), 3);
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is string s) path.Text = s; };
        list.MouseDoubleClick += (_, _) => ok.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        ok.Click += (_, _) =>
        {
            var r = path.Text.Trim().Trim('"');
            if (!Directory.Exists(r)) { MessageBox.Show(w, "Esa carpeta no existe.", "stackdev", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            result = Path.GetFullPath(r);
            w.Close();
        };
        cancel.Click += (_, _) => w.Close();

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(browse, 1);
        row.Children.Add(path);
        row.Children.Add(browse);

        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = "¿Qué proyecto quieres abrir?", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        root.Children.Add(row);
        root.Children.Add(search);
        root.Children.Add(list);
        root.Children.Add(ButtonRow(cancel, ok));
        w.Content = root;

        w.Loaded += (_, _) =>
        {
            var parent = string.IsNullOrWhiteSpace(last) ? null : Path.GetDirectoryName(last);
            Fill(parent, 2);
            path.Focus();
            path.CaretIndex = path.Text.Length;
        };
        w.ShowDialog();
        return result;
    }
}
