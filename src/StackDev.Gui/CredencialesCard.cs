using System.Collections.Generic;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StackDev.Core.Credenciales;

namespace StackDev.Gui;

/// <summary>
/// La tarjeta «Credenciales» del panel: usuarios y contraseñas de prueba del proyecto, con ojo para mostrar u ocultar
/// (todas a la vez o cada una), botones para copiar y para agregar, editar y quitar.
/// </summary>
public sealed class CredencialesCard : Border
{
    private const string Fuente = "Segoe MDL2 Assets";
    private const string IcOjo = "", IcOjoTachado = "", IcCopiar = "", IcListo = "",
                         IcEditar = "", IcQuitar = "", IcMas = "";

    private readonly CredencialesStore _store;
    private readonly Window? _owner;
    private readonly StackPanel _lista = new();
    private readonly Button _ojoGlobal;
    private readonly HashSet<int> _visibles = new();
    private bool _verTodas;

    public CredencialesCard(CredencialesStore store, Window? owner)
    {
        _store = store;
        _owner = owner;
        Style = (Style)Application.Current.FindResource("Card");
        Width = 380;
        Margin = new Thickness(0, 0, 16, 16);

        var cabecera = new Grid();
        cabecera.ColumnDefinitions.Add(new ColumnDefinition());
        cabecera.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cabecera.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titulo = new StackPanel { Orientation = Orientation.Horizontal };
        titulo.Children.Add(Iconos.Insignia("CREDENCIALES"));
        titulo.Children.Add(new TextBlock { Text = "Credenciales", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        cabecera.Children.Add(titulo);
        _ojoGlobal = Icono(IcOjo, "Mostrar u ocultar todas las contraseñas", AlternarTodas);
        var mas = Icono(IcMas, "Agregar credencial", () => Editar(-1));
        Grid.SetColumn(_ojoGlobal, 1);
        Grid.SetColumn(mas, 2);
        cabecera.Children.Add(_ojoGlobal);
        cabecera.Children.Add(mas);

        var raiz = new StackPanel();
        raiz.Children.Add(cabecera);
        _lista.Margin = new Thickness(0, 10, 0, 0);
        raiz.Children.Add(_lista);
        Child = raiz;
        Refrescar();
    }

    private static Brush P(string clave) => (Brush)Application.Current.FindResource(clave);

    private Button Icono(string glifo, string ayuda, Action alHacerClick)
    {
        var b = new Button
        {
            Content = glifo,
            FontFamily = new FontFamily(Fuente),
            FontSize = 13,
            Style = (Style)Application.Current.FindResource("BtnIcono"),
            ToolTip = ayuda,
            Padding = new Thickness(2),
        };
        b.Click += (_, _) => alHacerClick();
        return b;
    }

    private void AlternarTodas()
    {
        _verTodas = !_verTodas;
        _visibles.Clear();
        Refrescar();
    }

    private async void Copiar(Button boton, string texto)
    {
        try { Clipboard.SetText(texto); }
        catch (ExternalException) { return; }   // el portapapeles lo tiene otro programa en este instante
        boton.Content = IcListo;
        await Task.Delay(1000);
        boton.Content = IcCopiar;
    }

    private void Refrescar()
    {
        _ojoGlobal.Content = _verTodas ? IcOjoTachado : IcOjo;
        _lista.Children.Clear();
        var datos = _store.Leer();
        if (datos.Count == 0)
        {
            _lista.Children.Add(new TextBlock
            {
                Text = "Aún no hay ninguna. Con + guardas usuarios y contraseñas de prueba para no olvidarlos.",
                Foreground = P("MutedBrush"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        for (var i = 0; i < datos.Count; i++) _lista.Children.Add(Fila(i, datos[i]));
    }

    private UIElement Fila(int indice, Credencial c)
    {
        var contenido = new StackPanel();

        var titulo = new Grid();
        titulo.ColumnDefinitions.Add(new ColumnDefinition());
        titulo.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titulo.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titulo.Children.Add(new TextBlock { Text = c.Nombre, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var editar = Icono(IcEditar, "Editar", () => Editar(indice));
        var quitar = Icono(IcQuitar, "Eliminar", () => Quitar(indice));
        Grid.SetColumn(editar, 1);
        Grid.SetColumn(quitar, 2);
        titulo.Children.Add(editar);
        titulo.Children.Add(quitar);
        contenido.Children.Add(titulo);

        if (c.Usuario.Length > 0) contenido.Children.Add(Linea("Usuario", c.Usuario, c.Usuario, null));

        if (c.Clave.Length > 0)
        {
            var visible = _verTodas || _visibles.Contains(indice);
            var ojo = Icono(visible ? IcOjoTachado : IcOjo, visible ? "Ocultar" : "Mostrar", () =>
            {
                if (!_visibles.Remove(indice)) _visibles.Add(indice);
                Refrescar();
            });
            contenido.Children.Add(Linea("Contraseña", visible ? c.Clave : "••••••••", c.Clave, ojo));
        }

        if (c.Nota.Length > 0)
            contenido.Children.Add(new TextBlock { Text = c.Nota, Foreground = P("MutedBrush"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });

        return new Border
        {
            Background = P("Surface2Brush"),
            BorderBrush = P("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 6, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = contenido,
        };
    }

    /// <summary>«Etiqueta  valor  [ojo] [copiar]».</summary>
    private Grid Linea(string etiqueta, string mostrado, string aCopiar, Button? ojo)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock { Text = etiqueta, Foreground = P("MutedBrush"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var valor = new TextBlock
        {
            Text = mostrado,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = ojo is null ? aCopiar : null,
        };
        Grid.SetColumn(valor, 1);
        g.Children.Add(valor);
        if (ojo is not null) { Grid.SetColumn(ojo, 2); g.Children.Add(ojo); }
        Button? copiar = null;
        copiar = Icono(IcCopiar, "Copiar", () => Copiar(copiar!, aCopiar));
        Grid.SetColumn(copiar, 3);
        g.Children.Add(copiar);
        return g;
    }

    private void Quitar(int indice)
    {
        var datos = _store.Leer();
        if (indice < 0 || indice >= datos.Count) return;
        if (!Dialogos.Confirmar(_owner, $"¿Eliminar «{datos[indice].Nombre}»?", false)) return;
        datos.RemoveAt(indice);
        _store.Guardar(datos);
        _visibles.Clear();
        Refrescar();
    }

    private void Editar(int indice)
    {
        var datos = _store.Leer();
        var actual = indice >= 0 && indice < datos.Count ? datos[indice] : new Credencial();

        var w = new Window
        {
            Title = indice < 0 ? "Nueva credencial" : "Editar credencial",
            Owner = _owner,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = _owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        BarraTitulo.Aplicar(w);

        var raiz = new StackPanel { Margin = new Thickness(22) };
        TextBox Campo(string etiqueta, string valor, bool varias = false)
        {
            raiz.Children.Add(new TextBlock { Text = etiqueta, Foreground = P("MutedBrush"), FontSize = 12, Margin = new Thickness(0, 10, 0, 4) });
            var t = new TextBox { Text = valor };
            if (varias) { t.AcceptsReturn = true; t.TextWrapping = TextWrapping.Wrap; t.MinHeight = 56; t.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; }
            raiz.Children.Add(t);
            return t;
        }
        var nombre = Campo("Nombre (p. ej. «Admin web», «Usuario de prueba»)", actual.Nombre);
        var usuario = Campo("Usuario o correo", actual.Usuario);
        var clave = Campo("Contraseña", actual.Clave);
        var nota = Campo("Nota (opcional)", actual.Nota, varias: true);

        var ok = new Button { Content = "Guardar", MinWidth = 90, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(8, 0, 0, 0), Style = (Style)Application.Current.FindResource("BtnPrimary"), IsDefault = true };
        var cancelar = new Button { Content = "Cancelar", MinWidth = 90, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var botones = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        botones.Children.Add(cancelar);
        botones.Children.Add(ok);
        raiz.Children.Add(botones);
        w.Content = raiz;

        var guardado = false;
        ok.Click += (_, _) => { guardado = true; w.Close(); };
        cancelar.Click += (_, _) => w.Close();
        w.Loaded += (_, _) => { nombre.Focus(); nombre.SelectAll(); };
        w.ShowDialog();
        if (!guardado) return;

        var nueva = new Credencial
        {
            Nombre = nombre.Text.Trim(),
            Usuario = usuario.Text.Trim(),
            Clave = clave.Text,
            Nota = nota.Text.Trim(),
        };
        if (nueva.Nombre.Length == 0) nueva.Nombre = nueva.Usuario.Length > 0 ? nueva.Usuario : "Credencial";
        if (indice >= 0 && indice < datos.Count) datos[indice] = nueva; else datos.Add(nueva);
        _store.Guardar(datos);
        Refrescar();
    }
}
