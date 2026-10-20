using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StackDev.Core.Detection;

namespace StackDev.Gui;

/// <summary>Ventanas pequeñas (confirmar, preguntar, elegir, abrir proyecto) construidas en código y con el tema oscuro.</summary>
public static class Dialogos
{
    private static Style Estilo(string clave) => (Style)Application.Current.FindResource(clave);

    private static Window Base(Window? owner, string titulo, double ancho)
    {
        var ventana = new Window
        {
            Title = titulo,
            Owner = owner,
            Width = ancho,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        BarraTitulo.Aplicar(ventana);
        return ventana;
    }

    private static Button Boton(string texto, bool primario, bool cancelar = false)
    {
        var b = new Button { Content = texto, MinWidth = 90, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(8, 0, 0, 0) };
        if (primario) { b.Style = Estilo("BtnPrimary"); b.IsDefault = true; }
        b.IsCancel = cancelar;
        return b;
    }

    private static StackPanel FilaBotones(params Button[] botones)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        foreach (var b in botones) p.Children.Add(b);
        return p;
    }

    public static bool Confirmar(Window? owner, string pregunta, bool porDefecto = true)
    {
        var w = Base(owner, "stackdev", 460);
        var si = Boton("Sí", porDefecto);
        var no = Boton("No", !porDefecto, cancelar: true);
        var resultado = false;
        si.Click += (_, _) => { resultado = true; w.Close(); };
        no.Click += (_, _) => { resultado = false; w.Close(); };
        var raiz = new StackPanel { Margin = new Thickness(22) };
        raiz.Children.Add(new TextBlock { Text = pregunta, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
        raiz.Children.Add(FilaBotones(no, si));
        w.Content = raiz;
        w.ShowDialog();
        return resultado;
    }

    public static string? Preguntar(Window? owner, string pregunta, string? porDefecto)
    {
        var w = Base(owner, "stackdev", 480);
        var caja = new TextBox { Text = porDefecto ?? "", Margin = new Thickness(0, 10, 0, 0) };
        var ok = Boton("Aceptar", true);
        var cancel = Boton("Cancelar", false, cancelar: true);
        string? resultado = null;
        ok.Click += (_, _) => { resultado = caja.Text.Trim(); w.Close(); };
        cancel.Click += (_, _) => w.Close();
        var raiz = new StackPanel { Margin = new Thickness(22) };
        raiz.Children.Add(new TextBlock { Text = pregunta, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
        raiz.Children.Add(caja);
        raiz.Children.Add(FilaBotones(cancel, ok));
        w.Content = raiz;
        w.Loaded += (_, _) => { caja.Focus(); caja.SelectAll(); };
        w.ShowDialog();
        return string.IsNullOrEmpty(resultado) ? null : resultado;
    }

    /// <summary>Devuelve el índice elegido o -1.</summary>
    public static int Elegir(Window? owner, string titulo, IReadOnlyList<string> opciones, int porDefecto)
    {
        var w = Base(owner, "stackdev", 480);
        var lista = new ListBox { MaxHeight = 320, Margin = new Thickness(0, 12, 0, 0), BorderThickness = new Thickness(1) };
        foreach (var o in opciones) lista.Items.Add(o);
        lista.SelectedIndex = porDefecto >= 0 && porDefecto < opciones.Count ? porDefecto : (opciones.Count > 0 ? 0 : -1);
        var ok = Boton("Elegir", true);
        var cancel = Boton("Cancelar", false, cancelar: true);
        var resultado = -1;
        ok.Click += (_, _) => { resultado = lista.SelectedIndex; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        lista.MouseDoubleClick += (_, _) => { if (lista.SelectedIndex >= 0) { resultado = lista.SelectedIndex; w.Close(); } };
        var raiz = new StackPanel { Margin = new Thickness(22) };
        raiz.Children.Add(new TextBlock { Text = titulo, TextWrapping = TextWrapping.Wrap, FontSize = 14, FontWeight = FontWeights.SemiBold });
        raiz.Children.Add(lista);
        raiz.Children.Add(FilaBotones(cancel, ok));
        w.Content = raiz;
        w.ShowDialog();
        return resultado;
    }

    /// <summary>Elegir la carpeta de un proyecto: escribirla, examinar, o buscar proyectos dentro de una carpeta.</summary>
    public static string? Proyecto(Window? owner, string? ultimo)
    {
        var w = Base(owner, "Abrir proyecto", 620);
        var ruta = new TextBox { Text = ultimo ?? "" };
        var examinar = new Button { Content = "Examinar…", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 7, 14, 7) };
        var buscar = new Button { Content = "Buscar proyectos aquí", Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 6, 12, 6) };
        var lista = new ListBox { Height = 190, Margin = new Thickness(0, 10, 0, 0) };
        var ok = Boton("Abrir", true);
        var cancel = Boton("Cancelar", false, cancelar: true);
        string? resultado = null;

        void Llenar(string? baseDir, int profundidad)
        {
            lista.Items.Clear();
            if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir)) return;
            var anterior = w.Cursor;
            w.Cursor = Cursors.Wait;
            try { foreach (var p in Explorador.BuscarProyectos(baseDir, profundidad)) lista.Items.Add(p); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            finally { w.Cursor = anterior; }
        }

        examinar.Click += (_, _) =>
        {
            var d = new Microsoft.Win32.OpenFolderDialog { Title = "Carpeta del proyecto" };
            if (Directory.Exists(ruta.Text)) d.InitialDirectory = ruta.Text;
            if (d.ShowDialog(w) == true) { ruta.Text = d.FolderName; Llenar(d.FolderName, 3); }
        };
        buscar.Click += (_, _) => Llenar(ruta.Text.Trim().Trim('"'), 3);
        lista.SelectionChanged += (_, _) => { if (lista.SelectedItem is string s) ruta.Text = s; };
        lista.MouseDoubleClick += (_, _) => ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ok.Click += (_, _) =>
        {
            var r = ruta.Text.Trim().Trim('"');
            if (!Directory.Exists(r)) { MessageBox.Show(w, "Esa carpeta no existe.", "stackdev", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            resultado = Path.GetFullPath(r);
            w.Close();
        };
        cancel.Click += (_, _) => w.Close();

        var fila = new Grid();
        fila.ColumnDefinitions.Add(new ColumnDefinition());
        fila.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(examinar, 1);
        fila.Children.Add(ruta);
        fila.Children.Add(examinar);

        var raiz = new StackPanel { Margin = new Thickness(22) };
        raiz.Children.Add(new TextBlock { Text = "¿Qué proyecto quieres abrir?", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        raiz.Children.Add(fila);
        raiz.Children.Add(buscar);
        raiz.Children.Add(lista);
        raiz.Children.Add(FilaBotones(cancel, ok));
        w.Content = raiz;

        w.Loaded += (_, _) =>
        {
            var padre = string.IsNullOrWhiteSpace(ultimo) ? null : Path.GetDirectoryName(ultimo);
            Llenar(padre, 2);
            ruta.Focus();
            ruta.CaretIndex = ruta.Text.Length;
        };
        w.ShowDialog();
        return resultado;
    }
}
