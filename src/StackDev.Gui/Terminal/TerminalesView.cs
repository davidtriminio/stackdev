using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StackDev.Core;
using StackDev.Core.Config;

namespace StackDev.Gui.Terminal;

public enum TipoShell { Predeterminado, WindowsPowerShell, Cmd }

/// <summary>
/// La sección «Terminales»: pestañas con terminales reales (cada una con su propio shell), un botón para abrir más,
/// otro para elegir carpeta y shell, y otro para dividir la pestaña en paneles (hasta tres).
/// </summary>
public sealed class TerminalesView : Grid
{
    private const string FuenteIconos = "Segoe MDL2 Assets";
    private const int MaxPaneles = 3;

    private sealed class Pestana
    {
        public Border Cabecera = null!;
        public TextBlock Titulo = null!;
        public string Base = "";
        public string? NombreFijo;
        public Grid Contenido = null!;
        public readonly List<TerminalView> Paneles = new();
        public readonly List<GridSplitter> Divisores = new();
        public TerminalView? Activo;
    }

    private readonly Window _owner;
    private readonly Func<Sesion?> _sesion;
    private readonly StackPanel _tira = new() { Orientation = Orientation.Horizontal };
    private readonly Grid _cuerpo = new();
    private readonly List<Pestana> _pestanas = new();
    private readonly UIElement _vacio;
    private Pestana? _actual;
    private Button? _btnCompleto;

    /// <summary>Pide al contenedor ocultar el panel de terminales (las sesiones siguen vivas).</summary>
    public event Action? PedirOcultar;
    /// <summary>Pide alternar entre media pantalla y pantalla completa.</summary>
    public event Action? PedirAlternarCompleto;
    /// <summary>Atajos que afectan al contenedor (mostrar/ocultar) y llegaron desde dentro de una terminal.</summary>
    public event Action<string, int>? PedirAtajo;

    /// <summary>Atajos de pestañas: next, prev, tab (n = 1..9) y new.</summary>
    public void Atajo(string accion, int n)
    {
        switch (accion)
        {
            case "new": Nueva(); return;
            case "tab":
                if (n >= 1 && n <= _pestanas.Count) Seleccionar(_pestanas[n - 1]);
                return;
            case "next":
            case "prev":
                if (_pestanas.Count == 0) { Nueva(); return; }
                var i = _actual is null ? 0 : _pestanas.IndexOf(_actual);
                var paso = accion == "next" ? 1 : -1;
                Seleccionar(_pestanas[(i + paso + _pestanas.Count) % _pestanas.Count]);
                return;
        }
    }

    public TerminalesView(Window owner, Func<Sesion?> sesion)
    {
        _owner = owner;
        _sesion = sesion;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Barra de pestañas
        var barra = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        barra.ColumnDefinitions.Add(new ColumnDefinition());
        barra.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _tira,
        };
        barra.Children.Add(scroll);
        var botones = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
        botones.Children.Add(Icono("", "Nueva terminal", () => Nueva()));
        botones.Children.Add(Icono("", "Elegir carpeta y shell…", ElegirYAbrir));
        botones.Children.Add(Icono("\u25EB", "Dividir esta pestaña", Dividir, "Segoe UI Symbol"));
        _btnCompleto = Icono("\uE740", "Pantalla completa", () => PedirAlternarCompleto?.Invoke());
        botones.Children.Add(_btnCompleto);
        botones.Children.Add(Icono("\uE8BB", "Ocultar terminales", () => PedirOcultar?.Invoke()));
        Grid.SetColumn(botones, 1);
        barra.Children.Add(botones);
        Children.Add(barra);

        // Cuerpo: el fondo es el mismo de la terminal, así no parpadea al abrir
        var marco = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x08, 0x09, 0x0A)),
            BorderBrush = P("LineBrush"),
            BorderThickness = new Thickness(1),
            Child = _cuerpo,
        };
        Grid.SetRow(marco, 1);
        Children.Add(marco);

        var abrir = new Button { Content = "Nueva terminal", Style = (Style)Application.Current.FindResource("BtnPrimary"), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        abrir.Click += (_, _) => Nueva();
        var vacio = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
        vacio.Children.Add(new TextBlock { Text = "No hay terminales abiertas.", Foreground = P("MutedBrush") });
        vacio.Children.Add(abrir);
        _vacio = vacio;
        _cuerpo.Children.Add(vacio);
    }

    private static Brush P(string clave) => (Brush)Application.Current.FindResource(clave);

    private static Button Icono(string glifo, string ayuda, Action alClic, string fuente = FuenteIconos)
    {
        var b = new Button
        {
            Content = glifo,
            FontFamily = new FontFamily(fuente),
            FontSize = 14,
            Style = (Style)Application.Current.FindResource("BtnIcono"),
            ToolTip = ayuda,
        };
        b.Click += (_, _) => alClic();
        return b;
    }

    // ------------------------------------------------------------ Shells y carpetas

    private static (string Nombre, string Linea) ShellDe(TipoShell tipo)
    {
        var sys = Environment.SystemDirectory;
        switch (tipo)
        {
            case TipoShell.Cmd:
                return ("cmd", $"\"{Path.Combine(sys, "cmd.exe")}\"");
            case TipoShell.WindowsPowerShell:
                return ("powershell", $"\"{Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe")}\" -NoLogo");
            default:
                var exe = StackDev.Core.Proc.Shell.PowerShellExe;
                var nombre = Path.GetFileNameWithoutExtension(exe).Equals("pwsh", StringComparison.OrdinalIgnoreCase) ? "pwsh" : "powershell";
                return (nombre, $"\"{exe}\" -NoLogo");
        }
    }

    private List<(string Nombre, string Carpeta)> Carpetas()
    {
        var lista = new List<(string, string)>();
        var s = _sesion();
        if (s is null)
        {
            lista.Add(("inicio", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            return lista;
        }
        lista.Add(("raíz", s.Raiz));
        foreach (var (comp, nombre) in new[] { (Componente.Api, "api"), (Componente.Web, "web"), (Componente.Movil, "móvil") })
        {
            if (!s.Ctx.Tiene(comp)) continue;
            var dir = s.Ctx.Carpeta(comp);
            if (Directory.Exists(dir)) lista.Add((nombre, dir));
        }
        return lista;
    }

    // ------------------------------------------------------------ Pestañas

    /// <summary>Al mostrar la sección: si no hay ninguna terminal, abre una; si hay, devuelve el foco a la activa.</summary>
    public void AlMostrar()
    {
        if (_pestanas.Count == 0) Nueva();
        else if (_actual is not null) Seleccionar(_actual);
    }

    /// <summary>Cambia el glifo y la ayuda del botón según el modo actual.</summary>
    public void MarcarCompleto(bool completo)
    {
        if (_btnCompleto is null) return;
        _btnCompleto.Content = completo ? "\uE73F" : "\uE740";
        _btnCompleto.ToolTip = completo ? "Volver a media pantalla" : "Pantalla completa";
    }

    /// <summary>Muestra la pestaña de esa carpeta si ya existe; si no, abre una terminal nueva ahí.</summary>
    public void Abrir(string etiqueta, string carpeta)
    {
        var existente = _pestanas.FirstOrDefault(x => x.Base == etiqueta && x.Paneles.Count > 0
            && string.Equals(x.Paneles[0].Carpeta, carpeta, StringComparison.OrdinalIgnoreCase));
        if (existente is not null) Seleccionar(existente);
        else Nueva(TipoShell.Predeterminado, carpeta, etiqueta);
    }

    /// <summary>Abre una pestaña que ejecuta una línea de comando propia (por ejemplo ssh) en lugar de la shell.</summary>
    public void AbrirComando(string etiqueta, string linea) => Nueva(TipoShell.Predeterminado, null, etiqueta, linea);

    public void Nueva(TipoShell tipo = TipoShell.Predeterminado, string? carpeta = null, string? etiqueta = null, string? lineaPropia = null)
    {
        var (nombreShell, linea) = ShellDe(tipo);
        if (lineaPropia is not null) linea = lineaPropia;
        carpeta ??= Carpetas()[0].Carpeta;
        var baseTitulo = etiqueta ?? nombreShell;
        var repetidas = _pestanas.Count(x => x.Base == baseTitulo);

        var p = new Pestana { Base = baseTitulo };
        p.Contenido = new Grid();
        _cuerpo.Children.Add(p.Contenido);
        ConstruirCabecera(p, repetidas == 0 ? baseTitulo : $"{baseTitulo} {repetidas + 1}");
        _tira.Children.Add(p.Cabecera);
        _pestanas.Add(p);
        AgregarPanel(p, linea, carpeta);
        Seleccionar(p);
    }

    private void ConstruirCabecera(Pestana p, string titulo)
    {
        var icono = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily(FuenteIconos),
            FontSize = 12,
            Foreground = P("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        p.Titulo = new TextBlock { Text = titulo, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var cerrar = new Button
        {
            Content = "",
            FontFamily = new FontFamily(FuenteIconos),
            FontSize = 10,
            Style = (Style)Application.Current.FindResource("BtnIcono"),
            ToolTip = "Cerrar",
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(0),
        };
        cerrar.Click += (_, e) => { e.Handled = true; CerrarPestana(p); };

        var fila = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(icono, Dock.Left);
        DockPanel.SetDock(cerrar, Dock.Right);
        fila.Children.Add(icono);
        fila.Children.Add(cerrar);
        fila.Children.Add(p.Titulo);

        p.Cabecera = new Border
        {
            Child = fila,
            Padding = new Thickness(10, 6, 6, 6),
            Margin = new Thickness(0, 0, 4, 0),
            MinWidth = 120,
            MaxWidth = 240,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(0, 0, 0, 2),
            Cursor = Cursors.Hand,
            ToolTip = "Doble clic: cambiar el nombre · clic central: cerrar",
        };
        p.Cabecera.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) Renombrar(p); else Seleccionar(p);
        };
        p.Cabecera.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) CerrarPestana(p);
        };
    }

    private void Renombrar(Pestana p)
    {
        var nuevo = Dialogos.Preguntar(_owner, "Nombre de la pestaña", p.Titulo.Text);
        if (nuevo is null) return;
        p.NombreFijo = nuevo;
        p.Titulo.Text = nuevo;
    }

    private void Seleccionar(Pestana p)
    {
        _actual = p;
        foreach (var t in _pestanas)
        {
            var activa = t == p;
            t.Contenido.Visibility = activa ? Visibility.Visible : Visibility.Collapsed;
            t.Cabecera.Background = activa ? P("Surface2Brush") : Brushes.Transparent;
            t.Cabecera.BorderBrush = activa ? P("AccentBrush") : Brushes.Transparent;
            t.Titulo.Foreground = activa ? P("TextBrush") : P("MutedBrush");
        }
        _vacio.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => p.Activo?.Enfocar()));
    }

    private void CerrarPestana(Pestana p)
    {
        var indice = _pestanas.IndexOf(p);
        if (indice < 0) return;
        _pestanas.RemoveAt(indice);
        _tira.Children.Remove(p.Cabecera);
        _cuerpo.Children.Remove(p.Contenido);
        foreach (var v in p.Paneles.ToList())
        {
            p.Contenido.Children.Remove(v);
            v.Dispose();
        }
        p.Paneles.Clear();

        if (_pestanas.Count == 0)
        {
            _actual = null;
            _vacio.Visibility = Visibility.Visible;
        }
        else if (_actual == p) Seleccionar(_pestanas[Math.Min(indice, _pestanas.Count - 1)]);
    }

    /// <summary>Cierra todas las terminales (y los programas que corren en ellas). Al cerrar la aplicación.</summary>
    public void CerrarTodo()
    {
        foreach (var p in _pestanas.ToList()) CerrarPestana(p);
    }

    // ------------------------------------------------------------ Paneles

    private TerminalView AgregarPanel(Pestana p, string linea, string carpeta)
    {
        var env = _sesion()?.EnvPara(carpeta);
        var v = new TerminalView(linea, carpeta, env);
        v.Terminado += (vista, _) => AlTerminar(p, vista);
        v.Atajo += (accion, n) => { if (accion is "next" or "prev" or "new" or "tab") Atajo(accion, n); else PedirAtajo?.Invoke(accion, n); };
        v.TituloCambiado += (_, titulo) => AlCambiarTitulo(p, titulo);

        if (p.Paneles.Count > 0)
        {
            var divisor = new GridSplitter
            {
                Width = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = P("LineBrush"),
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ResizeDirection = GridResizeDirection.Columns,
            };
            p.Divisores.Add(divisor);
            p.Contenido.Children.Add(divisor);
        }
        p.Paneles.Add(v);
        p.Contenido.Children.Add(v);
        p.Activo = v;
        Colocar(p);
        return v;
    }

    /// <summary>Reparte las columnas: panel, divisor, panel... Cambiar las columnas no toca a los paneles, así sus terminales siguen vivas.</summary>
    private static void Colocar(Pestana p)
    {
        p.Contenido.ColumnDefinitions.Clear();
        for (var i = 0; i < p.Paneles.Count; i++)
        {
            if (i > 0) p.Contenido.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            p.Contenido.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        }
        for (var i = 0; i < p.Paneles.Count; i++) Grid.SetColumn(p.Paneles[i], i * 2);
        for (var i = 0; i < p.Divisores.Count; i++) Grid.SetColumn(p.Divisores[i], i * 2 + 1);
    }

    private void AlTerminar(Pestana p, TerminalView v)
    {
        if (p.Paneles.Count > 1) QuitarPanel(p, v);
        else CerrarPestana(p);
    }

    private void QuitarPanel(Pestana p, TerminalView v)
    {
        var i = p.Paneles.IndexOf(v);
        if (i < 0) return;
        p.Contenido.Children.Remove(v);
        p.Paneles.RemoveAt(i);
        if (p.Divisores.Count > 0)
        {
            var d = p.Divisores[Math.Max(0, i - 1)];
            p.Contenido.Children.Remove(d);
            p.Divisores.Remove(d);
        }
        v.Dispose();
        Colocar(p);
        p.Activo = p.Paneles.FirstOrDefault();
        if (_actual == p) Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => p.Activo?.Enfocar()));
    }

    private static void AlCambiarTitulo(Pestana p, string titulo)
    {
        // Los shells ponen la ruta de su .exe como título: eso no sirve. Un programa que pone un título propio (vim, claude...) sí.
        if (p.NombreFijo is not null || string.IsNullOrWhiteSpace(titulo)) return;
        if (titulo.Contains('\\') || titulo.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
        p.Titulo.Text = titulo.Length > 40 ? titulo[..40] : titulo;
    }

    private void Dividir()
    {
        if (_actual is null) { Nueva(); return; }
        if (_actual.Paneles.Count >= MaxPaneles) return;
        var origen = _actual.Activo ?? _actual.Paneles[0];
        var v = AgregarPanel(_actual, origen.Linea, origen.Carpeta);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(v.Enfocar));
    }

    // ------------------------------------------------------------ Elegir carpeta y shell

    private void ElegirYAbrir()
    {
        var opciones = new List<(string Texto, TipoShell Tipo, string Carpeta, string Etiqueta)>();
        var carpetas = Carpetas();
        foreach (var (nombre, dir) in carpetas)
            opciones.Add(($"PowerShell — {nombre}   ({dir})", TipoShell.Predeterminado, dir, nombre));
        opciones.Add(($"Windows PowerShell — {carpetas[0].Nombre}", TipoShell.WindowsPowerShell, carpetas[0].Carpeta, "powershell"));
        opciones.Add(($"Símbolo del sistema (cmd) — {carpetas[0].Nombre}", TipoShell.Cmd, carpetas[0].Carpeta, "cmd"));

        var i = Dialogos.Elegir(_owner, "Nueva terminal en…", opciones.Select(x => x.Texto).ToList(), 0);
        if (i < 0) return;
        var o = opciones[i];
        Nueva(o.Tipo, o.Carpeta, o.Etiqueta);
    }
}
