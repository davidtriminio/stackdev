using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StackDev.Core;
using StackDev.Core.Android;
using StackDev.Core.Config;
using StackDev.Core.Detection;
using StackDev.Core.Docker;
using StackDev.Core.Monitoreo;
using StackDev.Core.Orquestacion;
using StackDev.Core.Servicios;
using StackDev.Core.State;
using StackDev.Core.Ui;

namespace StackDev.Gui;

public partial class MainWindow : Window
{
    private enum Vista { Inicio, Panel, Condiciones, Consola, Config, Terminales, Diagnostico, Servidor }

    /// <summary>Una tarjeta del panel (Postgres, API, Web, Emulador, App) con su estado y sus botones.</summary>
    private sealed class Tarjeta
    {
        public required string Seccion { get; init; }
        public required System.Windows.Shapes.Ellipse Punto { get; init; }
        public required TextBlock Estado { get; init; }
        public required TextBlock Detalle { get; init; }
    }

    private GuiUi _ui = null!;
    private StateStore _estado = null!;
    private Sesion? _s;
    private MonitorEstado? _mon;
    private ConsolaComandos? _cc;
    private CancellationTokenSource? _cts;
    private bool _ocupado;
    private bool _cerrando;
    private bool _salir;                                  // true = cerrar de verdad (no mandar a la bandeja)
    private System.Windows.Forms.NotifyIcon? _bandeja;
    private bool _avisoBandeja;
    private Instantanea? _mostrada;
    private DateTime _ultimoChequeoConfig = DateTime.UtcNow;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly List<Tarjeta> _tarjetas = new();
    private readonly List<Button> _botonesAccion = new();
    private readonly List<ToggleButton> _chips = new();

    private enum ModoTerminal { Oculto, Mitad, Completo }

    private Terminal.TerminalesView? _terminales;
    private DiagnosticoView? _diag;
    private ServidorView? _srv;
    private ModoTerminal _modoTerm = ModoTerminal.Oculto;
    private ModoTerminal _modoAplicado = ModoTerminal.Oculto;
    private bool _salidaOculta;
    private bool _limpiando;
    private GridLength _alturaSalida = new(190);
    private bool _rail;        // barra lateral reducida a iconos
    private bool _railAntes;   // cómo estaba antes de abrir las terminales

    public MainWindow()
    {
        InitializeComponent();
        _terminales = new Terminal.TerminalesView(this, () => _s);
        DockTerminal.Child = _terminales;
        _terminales.PedirOcultar += () => CambiarModoTerminal(ModoTerminal.Oculto);
        _terminales.PedirAlternarCompleto += () =>
            CambiarModoTerminal(_modoTerm == ModoTerminal.Completo ? ModoTerminal.Mitad : ModoTerminal.Completo);
        _timer.Tick += (_, _) => Tic();
        Principal.SizeChanged += (_, _) => AjustarEncabezado();
        Loaded += async (_, _) => await ArrancarAsync();
        Closing += AlCerrar;
        _terminales.PedirAtajo += (accion, n) => AtajoTerminal(accion, n);
        BtnNavegador.Content = Iconos.ConGlifo("\uE774", "Navegador");
        _diag = new DiagnosticoView(this, () => _s, CorrerAsync);
        VistaDiag.Children.Add(_diag);
        _srv = new ServidorView(this, () => _s, () => new StackDev.Core.Servidores.ServidoresStore(_estado.Dir),
            AbrirTerminalComando, GuardarDespliegue, (t, n) => _ui.Escribir(t, n));
        VistaServidor.Children.Add(_srv);
        CrearBandeja();
        Application.Current.SessionEnding += (_, _) => _salir = true;   // apagar/cerrar sesión de Windows: sí se sale
        PreviewKeyDown += (_, e) =>
        {
            var mod = Keyboard.Modifiers;
            var tecla = e.Key == Key.System ? e.SystemKey : e.Key;
            if (tecla == Key.Q && mod == ModifierKeys.Control) { e.Handled = true; Salir(); return; }
            if (tecla == Key.P && mod == ModifierKeys.Control && _s is not null)
            {
                e.Handled = true;
                CambiarBarra_Click(BtnCambiarBarra, new RoutedEventArgs());
                return;
            }
            if (_s is null) return;
            string? accion = null;
            var n = 0;
            if ((tecla == Key.T || tecla == Key.Tab) && (mod == ModifierKeys.Control || mod == (ModifierKeys.Control | ModifierKeys.Shift)))
                accion = (mod & ModifierKeys.Shift) != 0 ? "prev" : "next";
            else if (tecla == Key.Oem3 && mod == ModifierKeys.Control) accion = "toggle";
            else if (tecla == Key.T && mod == (ModifierKeys.Control | ModifierKeys.Alt)) accion = "new";
            else if (tecla >= Key.D1 && tecla <= Key.D9 && mod == (ModifierKeys.Control | ModifierKeys.Alt)) { accion = "tab"; n = tecla - Key.D0; }
            if (accion is null) return;
            e.Handled = true;
            AtajoTerminal(accion, n);
        };
    }

    private static Brush P(string clave) => (Brush)Application.Current.FindResource(clave);

    // ------------------------------------------------------------ Arranque y proyecto

    private async Task ArrancarAsync()
    {
        _ui = new GuiUi(this, Salida);
        _estado = StateStore.Abrir();
        _compacto = _estado.Compacto == "1";
        PonerTextoApariencia();
        Ventana_StateChanged(this, EventArgs.Empty);

        var arg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
        if (arg is not null && Directory.Exists(arg))
        {
            await AbrirProyectoAsync(Path.GetFullPath(arg));
            return;
        }

        var favorito = _estado.Favorito;
        if (!string.IsNullOrWhiteSpace(favorito))
        {
            if (Directory.Exists(favorito))
            {
                await AbrirProyectoAsync(favorito);
                return;
            }
            _ui.Escribir($"El proyecto favorito ya no existe: {favorito}", Nivel.Aviso);
        }

        // Sin favorito: se reabre el último proyecto que usaste
        var ultimo = _estado.UltimoProyecto;
        if (!string.IsNullOrWhiteSpace(ultimo) && Directory.Exists(ultimo))
        {
            await AbrirProyectoAsync(ultimo);
            return;
        }
        ActualizarNav();
        MostrarVista(Vista.Inicio);
    }

    private System.Windows.Controls.Primitives.Popup? _selector;

    /// <summary>Lista desplegable de proyectos recientes para saltar de uno a otro sin pasar por Inicio.</summary>
    private void CambiarBarra_Click(object sender, RoutedEventArgs e)
    {
        if (_estado is null) return;
        if (_selector is { IsOpen: true }) { _selector.IsOpen = false; return; }

        var lista = new StackPanel { MinWidth = 280 };
        foreach (var ruta in _estado.LeerProyectos().Where(Directory.Exists).Take(12))
        {
            var actual = _s is not null && string.Equals(Path.GetFullPath(ruta), _s.Raiz, StringComparison.OrdinalIgnoreCase);
            var fila = new StackPanel();
            fila.Children.Add(new TextBlock
            {
                Text = (actual ? "✓ " : "") + System.IO.Path.GetFileName(ruta.TrimEnd('\\', '/')),
                FontWeight = actual ? FontWeights.SemiBold : FontWeights.Normal,
                FontFamily = (FontFamily)FindResource("UiFont"),
                FontSize = 13,
                Foreground = P("TextBrush"),
            });
            fila.Children.Add(new TextBlock { Text = ruta, FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 11, Foreground = P("MutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            var b = new Button
            {
                Content = fila,
                Style = (Style)FindResource("BtnIcono"),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 6, 10, 6),
                Width = 320,
            };
            var destino = ruta;
            b.Click += async (_, _) =>
            {
                _selector!.IsOpen = false;
                if (!actual) await AbrirDesdeInicio(destino);
            };
            lista.Children.Add(b);
        }
        var todos = new Button
        {
            Content = new TextBlock { Text = "Ver todos los proyectos…", FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 13, Foreground = P("AccentBrush") },
            Style = (Style)FindResource("BtnIcono"),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 6, 10, 6),
            Width = 320,
        };
        todos.Click += (_, _) => { _selector!.IsOpen = false; NavInicio.IsChecked = true; };
        lista.Children.Add(todos);

        _selector = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = ChipProyecto.IsVisible ? ChipProyecto : BtnCambiarBarra,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = P("SurfaceBrush"),
                BorderBrush = P("LineBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4),
                Margin = new Thickness(0, 4, 0, 8),
                Child = lista,
            },
            IsOpen = true,
        };
    }

    private async Task AbrirProyectoAsync(string raiz)
    {
        raiz = Path.GetFullPath(raiz);
        var padre = RaizProyecto.SugerirPadre(raiz);
        if (padre is not null && Dialogos.Confirmar(this, $"«{Path.GetFileName(raiz)}» parece ser parte del proyecto en:\n{padre}\n\n¿Usar la carpeta raíz del proyecto?"))
            raiz = padre;
        _estado.UltimoProyecto = raiz;
        _estado.AgregarProyecto(raiz);

        _timer.Stop();
        _mon?.Dispose();
        _mon = null;
        _mostrada = null;

        var s = new Sesion(raiz, _estado, _ui, new OpcionesSesion());
        s.Captura = linea => _ui.Escribir(linea);
        _s = s;

        Ocupar($"Abriendo {Path.GetFileName(raiz)}…");
        try { await Task.Run(s.Iniciar); }
        catch (Exception ex) { _ui.Escribir($"No pude abrir el proyecto: {ex.Message}", Nivel.Error); }
        Liberar();

        _cc = new ConsolaComandos(s);
        _mon = new MonitorEstado(s);
        ActualizarNav();
        NavPanel.IsChecked = true;
        ConstruirTodo();
        _timer.Start();
        _ui.Escribir($"Proyecto abierto: {s.Raiz}", Nivel.Ok);
    }

    private void ConstruirTodo()
    {
        if (_s is null) return;
        TxtProyecto.Text = _s.Ctx.Nombre;
        TxtRuta.Text = _s.Raiz;
        TxtRuta.ToolTip = _s.Raiz;
        Title = $"stackdev — {_s.Ctx.Nombre}";
        ActualizarFavorito();
        ConstruirTarjetas();
        ConstruirCondiciones();
        RefrescarConsola();
        RefrescarConfig();
        RefrescarLateral();
        MostrarVista(VistaActual());
        if (_mostrada is not null) ActualizarTarjetas(_mostrada);
    }

    private void RefrescarLateral()
    {
        if (_s is null) return;
        TxtModo.Text = _s.ModoBuild;
        TxtAvd.Text = _s.Ctx.Tiene(Componente.Movil) ? (_s.Avd ?? "(ninguno)") : "—";
    }

    // ------------------------------------------------------------ Ejecutar acciones

    private bool _compacto;

    private void PonerTextoApariencia()
    {
        BtnAcento.Content = Apariencia.EsAmbar(_estado.Acento) ? "Ámbar (terminal vieja)" : "Verde (terminal)";
        BtnDensidad.Content = _compacto ? "Compacta" : "Normal";
    }

    private void Acento_Click(object sender, RoutedEventArgs e)
    {
        var ambar = !Apariencia.EsAmbar(_estado.Acento);
        _estado.Acento = ambar ? "ambar" : "verde";
        var completo = Apariencia.Aplicar(_estado.Acento);
        PonerTextoApariencia();
        if (!completo) _ui.Escribir("El acento se aplica por completo al reiniciar stackdev.", Nivel.Info);
    }

    private void Densidad_Click(object sender, RoutedEventArgs e)
    {
        _compacto = !_compacto;
        _estado.Compacto = _compacto ? "1" : "";
        PonerTextoApariencia();
        ConstruirTarjetas();
        _mostrada = null;
        _mon?.Refrescar();
    }

    /// <summary>El logo parpadea mientras hay una acción en curso.</summary>
    private void ParpadearLogo(bool activo)
    {
        if (activo)
            Logo.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(600))
            { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        else
            Logo.BeginAnimation(OpacityProperty, null);
    }

    private void Ocupar(string texto)
    {
        ParpadearLogo(true);
        _ocupado = true;
        PonerSalidaOculta(false);
        TxtOcupado.Text = texto;
        Progreso.Visibility = Visibility.Visible;
        BtnCancelar.IsEnabled = true;
        FijarBotones(false);
    }

    private void Liberar()
    {
        ParpadearLogo(false);
        _ocupado = false;
        TxtOcupado.Text = "Listo";
        Progreso.Visibility = Visibility.Collapsed;
        BtnCancelar.IsEnabled = false;
        FijarBotones(true);
    }

    private void FijarBotones(bool activos)
    {
        foreach (var b in _botonesAccion) b.IsEnabled = activos;
        BtnUp.IsEnabled = BtnDown.IsEnabled = BtnCheck.IsEnabled = activos;
        BtnCambiar.IsEnabled = activos;
        TxtBarra.IsEnabled = BtnBarra.IsEnabled = activos && _s is not null;
    }

    /// <summary>Corre algo de la lógica en segundo plano mostrando su salida. Una acción a la vez; «Cancelar acción» la detiene.</summary>
    private async Task<bool> CorrerAsync(string titulo, Func<Sesion, bool> f)
    {
        var s = _s;
        if (s is null || _ocupado) return false;

        _cts = new CancellationTokenSource();
        s.Ct = _cts.Token;
        _mon?.Pausar();
        Ocupar(titulo + "…");
        _ui.Escribir($"\n▶ {titulo}", Nivel.Titulo);

        var ok = false;
        try { ok = await Task.Run(() => f(s)); }
        catch (Exception ex) { _ui.Escribir($"Error inesperado: {ex.Message}", Nivel.Error); }

        var cancelado = _cts.IsCancellationRequested;
        _ui.Escribir(cancelado ? "Detenido." : ok ? "✔ Listo." : "✘ No terminó bien.", cancelado ? Nivel.Aviso : ok ? Nivel.Ok : Nivel.Error);
        s.Ct = CancellationToken.None;
        _cts.Dispose();
        _cts = null;

        Liberar();
        if (_desdeBarra) { _desdeBarra = false; TxtBarra.Focus(); }
        _mon?.Reanudar();
        RefrescarLateral();
        RefrescarCondiciones();
        RefrescarConsola();
        return ok;
    }

    private void Cancelar_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Limpiar_Click(object sender, RoutedEventArgs e)
    {
        _limpiando = true;
        _ui.Limpiar();
        Dispatcher.InvokeAsync(() => _limpiando = false, DispatcherPriority.Background);
    }

    // ------------------------------------------------------------ Salida plegable

    private void OcultarSalida_Click(object sender, RoutedEventArgs e) => PonerSalidaOculta(!_salidaOculta);

    /// <summary>Con la salida oculta solo queda su cabecera y la línea de comandos; cualquier texto nuevo la vuelve a mostrar.</summary>
    private void PonerSalidaOculta(bool ocultar)
    {
        if (_salidaOculta == ocultar) return;
        _salidaOculta = ocultar;
        if (ocultar && FilaSalida.Height.IsAbsolute && FilaSalida.Height.Value > 0) _alturaSalida = FilaSalida.Height;
        Salida.Visibility = ocultar ? Visibility.Collapsed : Visibility.Visible;
        Separador.Visibility = ocultar ? Visibility.Collapsed : Visibility.Visible;
        FilaSalida.MinHeight = ocultar ? 0 : 90;
        FilaSalida.Height = ocultar ? GridLength.Auto : _alturaSalida;
        BtnOcultarSalida.Content = ocultar ? "\uE70E" : "\uE70D";   // chevron arriba (mostrar) / abajo (ocultar)
        BtnOcultarSalida.ToolTip = ocultar ? "Mostrar la salida" : "Ocultar la salida (se muestra sola al ejecutar algo)";
    }

    private void Salida_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_salidaOculta && !_limpiando) PonerSalidaOculta(false);
    }

    private void Up_Click(object sender, RoutedEventArgs e) => _ = CorrerAsync("Levantar todo", s => s.Todo.Iniciar());

    private void Down_Click(object sender, RoutedEventArgs e) => _ = CorrerAsync("Detener todo", s => s.Todo.Detener());

    private void Check_Click(object sender, RoutedEventArgs e) =>
        _ = CorrerAsync("Verificar todo", s =>
        {
            var res = s.Todo.Verificar();
            foreach (var r in res)
                s.Ui.Escribir($"  {r.Paso,-28} {(r.Codigo == 0 ? "OK" : "FALLÓ (" + r.Codigo + ")")}  {r.Segundos} s", r.Codigo == 0 ? Nivel.Ok : Nivel.Error);
            return res.All(r => r.Codigo == 0);
        });

    private void Modo_Click(object sender, RoutedEventArgs e)
    {
        if (_s is null || !_s.Ctx.Tiene(Componente.Movil)) return;
        var a = Acciones.PorCodigo("b");
        if (a is not null) _ = CorrerAsync(a.Titulo, a.Ejecutar);
    }

    // ------------------------------------------------------------ Panel de tarjetas

    private static readonly (string Seccion, string Titulo, Componente Comp)[] Secciones =
    {
        ("POSTGRES", "Postgres", Componente.Postgres),
        ("API", "API", Componente.Api),
        ("WEB", "Web", Componente.Web),
        ("EMULADOR", "Emulador Android", Componente.Movil),
        ("FLUTTER", "App Flutter", Componente.Movil),
    };

    private void VistaPanel_SizeChanged(object sender, SizeChangedEventArgs e) => AjustarTarjetas();

    /// <summary>Con las terminales a la mitad las tarjetas se encogen para caber de dos en dos; si no, ancho normal.</summary>
    /// <summary>Con poco ancho (terminal a la mitad, ventana angosta) los botones bajan a una segunda fila para no pisar el título.</summary>
    private void AjustarEncabezado()
    {
        var angosto = Principal.ActualWidth < 1300;
        Grid.SetRow(PanelAcciones, angosto ? 1 : 0);
        PanelAcciones.HorizontalAlignment = angosto ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        PanelAcciones.Margin = angosto ? new Thickness(0, 14, 0, 0) : new Thickness(0);
    }

    /// <summary>
    /// Reparte las tarjetas en columnas iguales que llenan el ancho (sin dejar huecos a un lado).
    /// Deja unos píxeles de seguridad: si las columnas miden justo el ancho, la última salta de fila.
    /// </summary>
    private static void Repartir(Panel panel, double disponible, double minimo, double hueco)
    {
        if (disponible <= 0) return;
        var util = disponible - 10;
        var columnas = Math.Max(1, (int)Math.Floor(util / (minimo + hueco)));
        var ancho = Math.Max(240, Math.Floor(util / columnas) - hueco);
        foreach (var hijo in panel.Children)
        {
            if (hijo is FrameworkElement fe && fe is Border or CredencialesCard)
            {
                fe.Width = ancho;
                fe.Margin = new Thickness(0, 0, hueco, 16);
            }
        }
    }

    private void AjustarTarjetas()
    {
        var disponible = VistaPanel.ViewportWidth > 0 ? VistaPanel.ViewportWidth : VistaPanel.ActualWidth;
        Repartir(PanelTarjetas, disponible, 360, _modoTerm == ModoTerminal.Mitad ? 12 : 16);
    }

    private void AjustarInicio()
    {
        var disponible = ScrollInicio.ViewportWidth > 0 ? ScrollInicio.ViewportWidth : ScrollInicio.ActualWidth;
        Repartir(PanelInicio, disponible, 380, 16);
    }

    /// <summary>Configuración en dos columnas cuando hay ancho; en una sola si no.</summary>
    private void VistaConfig_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var ancha = VistaConfig.ActualWidth >= 1250;
        ColCfgHueco.Width = new GridLength(ancha ? 16 : 0);
        ColCfgB.Width = ancha ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(ColConfigB, ancha ? 0 : 1);
        Grid.SetColumn(ColConfigB, ancha ? 2 : 0);
        Grid.SetColumnSpan(ColConfigB, ancha ? 1 : 3);
    }

    private void ScrollInicio_SizeChanged(object sender, SizeChangedEventArgs e) => AjustarInicio();

    private void ConstruirTarjetas()
    {
        PanelTarjetas.Children.Clear();
        _tarjetas.Clear();
        _botonesAccion.Clear();
        if (_s is null) return;

        foreach (var (seccion, titulo, comp) in Secciones)
        {
            if (!_s.Ctx.Tiene(comp)) continue;

            var punto = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = P("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            var estado = new TextBlock { Text = "Consultando…", Foreground = P("MutedBrush"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            var detalle = new TextBlock
            {
                Text = " ",
                Foreground = P("MutedBrush"),
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, _compacto ? 10 : 16),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var cabecera = new Grid();
            cabecera.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cabecera.ColumnDefinitions.Add(new ColumnDefinition());
            cabecera.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cabecera.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cabecera.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var nombre = new TextBlock { Text = titulo, FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var bloqueNombre = new StackPanel { Orientation = Orientation.Horizontal };
            bloqueNombre.Children.Add(Iconos.Insignia(seccion));
            bloqueNombre.Children.Add(nombre);
            Grid.SetColumn(bloqueNombre, 1);
            Grid.SetColumn(estado, 2);
            cabecera.Children.Add(punto);
            cabecera.Children.Add(bloqueNombre);
            cabecera.Children.Add(estado);

            // Abrir una terminal en la carpeta de este componente (a la mitad, junto al panel)
            if (TerminalDeTarjeta(seccion) is { } destino)
            {
                var btnTerm = new Button
                {
                    Content = "\uE756",
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    Style = (Style)FindResource("BtnIcono"),
                    ToolTip = $"Abrir una terminal en {destino.Etiqueta}",
                    Margin = new Thickness(8, 0, 0, 0),
                };
                btnTerm.Click += (_, _) => AbrirTerminal(destino.Etiqueta, destino.Carpeta);
                Grid.SetColumn(btnTerm, 4);
                cabecera.Children.Add(btnTerm);
            }

            // Abrir la web o la API en el navegador elegido
            if (seccion is "API" or "WEB")
            {
                var componente = comp;
                var btnWeb = new Button
                {
                    Content = "\uE774",
                    FontFamily = new FontFamily(Iconos.Fuente),
                    Style = (Style)FindResource("BtnIcono"),
                    ToolTip = "Abrir en el navegador",
                    Margin = new Thickness(8, 0, 0, 0),
                };
                btnWeb.Click += (_, _) => _s?.Servicios.AbrirEnNavegador(componente);
                Grid.SetColumn(btnWeb, 3);
                cabecera.Children.Add(btnWeb);
            }

            var botones = new WrapPanel();
            var primero = true;
            foreach (var a in Acciones.Disponibles(_s).Where(x => x.Seccion == seccion))
            {
                var accion = a;
                var b = new Button { Content = Iconos.ConGlifo(Iconos.GlifoAccion(a.Nombre), a.Titulo), Style = (Style)FindResource("BtnChip"), ToolTip = $"Código: {a.Codigo}" };
                if (primero)
                {
                    b.Background = P("AccentGradient");
                    b.BorderBrush = P("AccentBrush");
                    b.Foreground = P("AccentOnBrush");
                    b.FontWeight = FontWeights.SemiBold;
                    primero = false;
                }
                b.Click += (_, _) => _ = CorrerAsync(accion.Titulo, accion.Ejecutar);
                botones.Children.Add(b);
                _botonesAccion.Add(b);
            }

            var contenido = new StackPanel();
            contenido.Children.Add(cabecera);
            contenido.Children.Add(detalle);
            contenido.Children.Add(botones);

            PanelTarjetas.Children.Add(new Border
            {
                Style = (Style)FindResource("Card"),
                Width = 380,
                Margin = new Thickness(0, 0, 16, 16),
                Padding = new Thickness(_compacto ? 14 : 20, _compacto ? 12 : 18, _compacto ? 14 : 20, _compacto ? 6 : 10),
                Child = contenido,
            });
            _tarjetas.Add(new Tarjeta { Seccion = seccion, Punto = punto, Estado = estado, Detalle = detalle });
        }

        Dispatcher.InvokeAsync(AjustarTarjetas, DispatcherPriority.Loaded);

        // Las credenciales del proyecto, a continuación de la tarjeta de la app
        PanelTarjetas.Children.Add(new CredencialesCard(new StackDev.Core.Credenciales.CredencialesStore(_s.Estado.CarpetaConfig(_s.Clave)), this));

        if (_tarjetas.Count == 0)
            PanelTarjetas.Children.Add(new TextBlock
            {
                Text = "Este proyecto no tiene nada configurado. Abre «Configuración» para editar dev-menu.json o detectarlo de nuevo.",
                Foreground = P("MutedBrush"),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520,
            });
        FijarBotones(!_ocupado);
    }

    private static string ColorServicio(CodigoServicio c) => c switch
    {
        CodigoServicio.Corriendo => "OkBrush",
        CodigoServicio.Arrancando => "WarnBrush",
        CodigoServicio.Error => "ErrBrush",
        _ => "MutedBrush",
    };

    private void Poner(string seccion, string colorClave, string estado, string detalle)
    {
        var t = _tarjetas.FirstOrDefault(x => x.Seccion == seccion);
        if (t is null) return;
        t.Punto.Fill = P(colorClave);
        t.Estado.Text = estado;
        t.Estado.Foreground = P(colorClave);
        t.Detalle.Text = string.IsNullOrEmpty(detalle) ? " " : detalle;
    }

    private void ActualizarTarjetas(Instantanea i)
    {
        var s = _s;
        if (s is null) return;

        if (i.Postgres is { } pg)
            Poner("POSTGRES",
                pg.Codigo == CodigoPostgres.Corriendo ? "OkBrush" : pg.Codigo == CodigoPostgres.Detenido ? "MutedBrush" : "WarnBrush",
                pg.Corriendo ? "CORRIENDO" : pg.Codigo == CodigoPostgres.Detenido ? "DETENIDO" : "ATENCIÓN",
                pg.Texto);

        foreach (var (seccion, e, log) in new[] { ("API", i.Api, i.LogApi), ("WEB", i.Web, i.LogWeb) })
        {
            if (e is null) continue;
            var corto = e.Codigo switch
            {
                CodigoServicio.Corriendo => "CORRIENDO",
                CodigoServicio.Arrancando => "ARRANCANDO",
                CodigoServicio.Error => "TERMINÓ CON ERROR",
                _ => "DETENIDA",
            };
            Poner(seccion, ColorServicio(e.Codigo), corto, e.Corriendo ? (e.Url ?? "") : (log ?? e.Url ?? ""));
        }

        if (s.Ctx.Tiene(Componente.Movil))
        {
            if (i.Emulador is { } emu)
                Poner("EMULADOR",
                    emu.Codigo == CodigoEmu.Ok ? "OkBrush" : emu.Codigo == CodigoEmu.NoResponde ? "ErrBrush" : "MutedBrush",
                    emu.Codigo == CodigoEmu.Ok ? "CORRIENDO" : emu.Codigo == CodigoEmu.NoResponde ? "NO RESPONDE" : "DETENIDO",
                    $"{emu.Texto}   ·   AVD: {s.Avd ?? "(ninguno)"}" + (string.IsNullOrEmpty(i.Condiciones) || i.Condiciones == "Normal" ? "" : $"   ·   {i.Condiciones}")
                    + (i.Telefonos.Length > 0 ? $"   ·   Teléfonos: {i.Telefonos}" : ""));
            else
                Poner("EMULADOR", "MutedBrush", "SIN SDK", "No encontré el Android SDK (movil.androidSdk en dev-menu.json)");

            Poner("FLUTTER", i.App ? "OkBrush" : "MutedBrush", i.App ? "CORRIENDO" : "DETENIDA",
                i.App ? $"flutter run ({s.ModoBuild}) en {s.Servicios.AppsVivas().Count} sesión(es), cada una en su ventana" : $"Modo: {s.ModoBuild}");
        }
    }

    private void Tic()
    {
        if (_s is null || _mon is null) return;

        if ((DateTime.UtcNow - _ultimoGit).TotalSeconds >= 20)
        {
            _ultimoGit = DateTime.UtcNow;
            _ = ActualizarGitAsync();
        }

        var u = _mon.Ultima;
        if (u is not null && !ReferenceEquals(u, _mostrada))
        {
            _mostrada = u;
            ActualizarTarjetas(u);
            if (VistaActual() == Vista.Condiciones) TxtCondActivas.Text = TextoCondiciones();
        }

        if (!_ocupado && (DateTime.UtcNow - _ultimoChequeoConfig).TotalSeconds >= 2)
        {
            _ultimoChequeoConfig = DateTime.UtcNow;
            if (_s.RecargarSiCambio())
            {
                _ui.Escribir("dev-menu.json cambió: lo recargué.", Nivel.Info);
                _mon.Refrescar();
                ConstruirTodo();
            }
        }
    }

    // ------------------------------------------------------------ Navegación

    private Vista VistaActual() =>
        NavInicio.IsChecked == true ? Vista.Inicio :
        NavTerminales.IsChecked == true ? Vista.Terminales :
        NavDiag.IsChecked == true ? Vista.Diagnostico :
        NavServidor.IsChecked == true ? Vista.Servidor :
        NavCond.IsChecked == true ? Vista.Condiciones :
        NavConsola.IsChecked == true ? Vista.Consola :
        NavConfig.IsChecked == true ? Vista.Config : Vista.Panel;

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        MostrarVista(VistaActual());
    }

    private void MostrarVista(Vista v)
    {
        VistaInicio.Visibility = v == Vista.Inicio ? Visibility.Visible : Visibility.Collapsed;
        PanelAcciones.Visibility = v == Vista.Inicio || _s is null ? Visibility.Collapsed : Visibility.Visible;
        VistaPanel.Visibility = v == Vista.Panel ? Visibility.Visible : Visibility.Collapsed;
        VistaCond.Visibility = v == Vista.Condiciones ? Visibility.Visible : Visibility.Collapsed;
        VistaDiag.Visibility = v == Vista.Diagnostico ? Visibility.Visible : Visibility.Collapsed;
        VistaServidor.Visibility = v == Vista.Servidor ? Visibility.Visible : Visibility.Collapsed;
        VistaConsola.Visibility = v == Vista.Consola ? Visibility.Visible : Visibility.Collapsed;

        // «Terminales» del menú = las terminales a pantalla completa; al irse a otra vista pasan a la mitad
        if (v == Vista.Terminales) _modoTerm = ModoTerminal.Completo;
        else if (_modoTerm == ModoTerminal.Completo) _modoTerm = ModoTerminal.Mitad;
        AplicarModoTerminal();
        VistaConfig.Visibility = v == Vista.Config ? Visibility.Visible : Visibility.Collapsed;

        (TxtTitulo.Text, TxtSubtitulo.Text) = v switch
        {
            Vista.Inicio => ("Proyectos", "Los proyectos que has abierto. Elige uno para continuar."),
            Vista.Diagnostico => ("Diagnóstico", "Compara tu configuración con Docker y el proyecto, y administra contenedores y volúmenes."),
            Vista.Servidor => ("Servidor", "Tus servidores, el estado de la API de este proyecto y sus logs en vivo."),
            Vista.Condiciones => ("Condiciones del dispositivo", "Red, batería, tema, texto, pantalla y ubicación del emulador."),
            Vista.Terminales => ("Terminales", "Terminales reales con pestañas: abre varias, divídelas o elige la carpeta del proyecto."),
            Vista.Consola => ("Consola de comandos", "Cualquier comando en una carpeta del proyecto; tareas y últimos comandos."),
            Vista.Config => ("Configuración", "Cómo se lee dev-menu.json y dónde se guarda todo."),
            _ => ("Panel", "Estado en vivo de lo que corre en este proyecto."),
        };
        TxtTituloVentana.Text = _s is null || v == Vista.Inicio ? "stackdev" : $"{_s.Ctx.Nombre}  ·  {TxtTitulo.Text}";
        if (v == Vista.Inicio) RefrescarInicio();
        if (v == Vista.Condiciones) RefrescarCondiciones();
        if (v == Vista.Diagnostico && _diag is not null) _ = _diag.ActualizarAsync();
        if (v == Vista.Servidor) _srv?.Actualizar();
        if (v == Vista.Consola) RefrescarConsola();
    }

    // ------------------------------------------------------------ Condiciones del dispositivo

    private string TextoCondiciones() => _s is null ? "" : $"Activas ahora: {_s.Condiciones.Texto()}";

    private void ConstruirCondiciones()
    {
        PanelCond.Children.Clear();
        _chips.Clear();
        if (_s is null) return;
        if (!_s.Ctx.Tiene(Componente.Movil))
        {
            PanelCond.Children.Add(new TextBlock { Text = "Este proyecto no tiene móvil configurado.", Foreground = P("MutedBrush") });
            return;
        }

        foreach (var grupo in CondicionesService.Presets.GroupBy(p => p.Cat))
        {
            var fila = new WrapPanel();
            var delGrupo = new List<ToggleButton>();
            foreach (var p in grupo)
            {
                var preset = p;
                var chip = new ToggleButton { Content = p.Txt, Tag = p, Style = (Style)FindResource("ChipToggle") };
                chip.Checked += (_, _) =>
                {
                    // Dentro de una categoría solo una opción a la vez.
                    foreach (var otro in delGrupo.Where(o => !ReferenceEquals(o, chip) && o.IsChecked == true)) otro.IsChecked = false;
                };
                delGrupo.Add(chip);
                _chips.Add(chip);
                fila.Children.Add(chip);
            }
            var tarjeta = new StackPanel();
            tarjeta.Children.Add(new TextBlock { Text = grupo.Key.ToUpperInvariant(), FontSize = 11, Foreground = P("MutedBrush"), Margin = new Thickness(0, 0, 0, 8) });
            tarjeta.Children.Add(fila);
            PanelCond.Children.Add(new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 12), Child = tarjeta });
        }
        RefrescarCondiciones();
    }

    /// <summary>Marca las condiciones que están activas según lo guardado para este emulador.</summary>
    private void RefrescarCondiciones()
    {
        if (_s is null || !_s.Ctx.Tiene(Componente.Movil)) return;
        TxtCondActivas.Text = TextoCondiciones();
        var activas = _s.Condiciones.Activas();
        foreach (var chip in _chips)
        {
            var p = (Preset)chip.Tag;
            chip.IsChecked = activas.TryGetValue(p.Cat, out var v) && v == p.Txt;
        }
    }

    private void CondAplicar_Click(object sender, RoutedEventArgs e)
    {
        var elegidas = _chips.Where(c => c.IsChecked == true).Select(c => ((Preset)c.Tag).N.ToString()).ToList();
        if (elegidas.Count == 0) { _ui.Escribir("Marca al menos una condición.", Nivel.Aviso); return; }
        _ = CorrerAsync("Aplicar condiciones", s => s.Condiciones.AplicarNumeros(elegidas));
    }

    private void CondRestablecer_Click(object sender, RoutedEventArgs e) =>
        _ = CorrerAsync("Restablecer condiciones", s => s.Condiciones.Restablecer());

    // ------------------------------------------------------------ Consola de comandos

    private void RefrescarConsola()
    {
        if (_s is null || _cc is null) return;
        TxtDirConsola.Text = _cc.Prompt;
        TxtDirBarra.Text = _cc.Prompt;
        BtnAliasApi.Visibility = _s.Ctx.Tiene(Componente.Api) ? Visibility.Visible : Visibility.Collapsed;
        BtnAliasWeb.Visibility = _s.Ctx.Tiene(Componente.Web) ? Visibility.Visible : Visibility.Collapsed;
        BtnAliasMovil.Visibility = _s.Ctx.Tiene(Componente.Movil) ? Visibility.Visible : Visibility.Collapsed;

        ListaComandos.Items.Clear();
        var n = 1;
        foreach (var it in _cc.Items())
            ListaComandos.Items.Add($"{n++,2}   {(it.Tarea ? "★ " : "")}{it.Titulo}");
    }

    private int _hist = -1;
    private bool _desdeBarra;
    private readonly List<string> _comandosBarra = new();

    private void BarraEjecutar_Click(object sender, RoutedEventArgs e)
    {
        var texto = TxtBarra.Text;
        if (_s is null || string.IsNullOrWhiteSpace(texto)) return;
        PonerSalidaOculta(false);
        if (_ocupado) { _ui.Escribir("Hay una acción en curso: espera o usa «Cancelar acción».", Nivel.Aviso); return; }
        _hist = -1;
        _desdeBarra = true;
        _comandosBarra.Add(texto.Trim());
        _estado.AgregarHistorialBarra(_s.Clave, texto);
        EjecutarTexto(texto);
        TxtBarra.Clear();
    }

    private void TxtBarra_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            BarraEjecutar_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down)
        {
            e.Handled = true;
            NavegarHistorial(e.Key == Key.Up ? 1 : -1);
        }
        else if (e.Key == Key.Escape && TxtBarra.Text.Length > 0)
        {
            _hist = -1;
            TxtBarra.Clear();
            e.Handled = true;
        }
    }

    private void HistArriba_Click(object sender, RoutedEventArgs e) { NavegarHistorial(1); TxtBarra.Focus(); }

    private void HistAbajo_Click(object sender, RoutedEventArgs e) { NavegarHistorial(-1); TxtBarra.Focus(); }

    /// <summary>↑ (delta = 1) va a un comando más viejo, ↓ (-1) a uno más nuevo; pasado el más nuevo deja la línea vacía.</summary>
    private void NavegarHistorial(int delta)
    {
        if (_s is null) return;
        var historial = _comandosBarra.AsEnumerable().Reverse()
            .Concat(_estado.LeerHistorialBarra(_s.Clave))
            .Concat(_estado.LeerRecientes(_s.Clave).Select(r => r.Comando))
            .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
        if (historial.Count == 0)
        {
            _ui.Escribir("Aún no hay comandos en el historial: escribe uno y pulsa Enter.", Nivel.Tenue);
            return;
        }
        _hist = Math.Clamp(_hist + delta, -1, historial.Count - 1);
        TxtBarra.Text = _hist < 0 ? "" : historial[_hist];
        TxtBarra.CaretIndex = TxtBarra.Text.Length;
    }

    private void Alias_Click(object sender, RoutedEventArgs e)
    {
        if (_cc is null || sender is not Button { Tag: string alias }) return;
        _cc.Procesar(alias);
        RefrescarConsola();
    }

    private void EjecutarTexto(string texto)
    {
        if (_cc is null || string.IsNullOrWhiteSpace(texto)) return;
        var cc = _cc;
        _ = CorrerAsync(texto.Trim(), _ => { cc.Procesar(texto); return true; });
    }

    private void EjecutarComando_Click(object sender, RoutedEventArgs e)
    {
        EjecutarTexto(TxtComando.Text);
        TxtComando.Clear();
    }

    private void EjecutarVentana_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtComando.Text)) return;
        EjecutarTexto("v " + TxtComando.Text.Trim());
        TxtComando.Clear();
    }

    private void TxtComando_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        EjecutarComando_Click(sender, e);
        e.Handled = true;
    }

    private void ListaComandos_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ListaComandos.SelectedIndex < 0) return;
        EjecutarTexto((ListaComandos.SelectedIndex + 1).ToString());
    }

    // ------------------------------------------------------------ Configuración

    private TextBlock TituloResumen(string texto) => new()
    {
        Text = texto, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = P("MutedBrush"), Margin = new Thickness(0, 10, 0, 8),
    };

    /// <summary>Una etiqueta pequeña (puerto, imagen, URL…) con fondo propio.</summary>
    private Border Chip(string texto, string colorClave = "TextBrush", bool mono = true, bool iconos = false)
    {
        var t = new TextBlock { FontSize = 11.5, Foreground = P(colorClave), VerticalAlignment = VerticalAlignment.Center };
        if (iconos && texto.Length > 1)
        {
            t.Inlines.Add(new System.Windows.Documents.Run(texto[..1]) { FontFamily = new FontFamily(Iconos.Fuente) });
            t.Inlines.Add(new System.Windows.Documents.Run(texto[1..]));
        }
        else
        {
            t.Text = texto;
            if (mono) t.FontFamily = (FontFamily)FindResource("MonoFont");
        }
        return new Border
        {
            Background = P("Surface2Brush"), BorderBrush = P("LineBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 4), Child = t,
        };
    }

    private UIElement FilaComponente(string clave, string nombre, string principal, params string[] etiquetas)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var ficha = Iconos.Insignia(clave, 32);
        ficha.VerticalAlignment = VerticalAlignment.Top;
        g.Children.Add(ficha);
        var n = new TextBlock { Text = nombre, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(n, 1);
        g.Children.Add(n);
        var cuerpo = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        if (principal.Length > 0)
            cuerpo.Children.Add(new TextBlock
            {
                Text = principal, FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 4),
            });
        foreach (var e in etiquetas.Where(x => !string.IsNullOrWhiteSpace(x)))
            cuerpo.Children.Add(Chip(e));
        Grid.SetColumn(cuerpo, 2);
        g.Children.Add(cuerpo);
        return g;
    }

    private static Border FichaGlifoPequena(string glifo, string hex)
    {
        var col = (Color)ColorConverter.ConvertFromString(hex);
        return new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(9), VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(0x26, col.R, col.G, col.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, col.R, col.G, col.B)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = glifo, FontFamily = new FontFamily(Iconos.Fuente), FontSize = 15, Foreground = new SolidColorBrush(col),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    /// <summary>Una ruta: ficha con icono, nombre, la ruta en una caja (carpeta atenuada, nombre final resaltado) y botones de copiar / abrir.</summary>
    private UIElement FilaRuta(string etiqueta, string glifo, string color, string ruta, bool esArchivo, string? aviso, string? ok = null)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var ficha = FichaGlifoPequena(glifo, color);
        ficha.Margin = new Thickness(0, 0, 10, 0);
        g.Children.Add(ficha);

        var nombre = new TextBlock { Text = etiqueta, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(nombre, 1);
        g.Children.Add(nombre);

        // Carpeta atenuada + último tramo resaltado
        var texto = new TextBlock { FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var corte = Math.Max(ruta.LastIndexOf('\\'), ruta.LastIndexOf('/'));
        if (corte > 0)
        {
            texto.Inlines.Add(new System.Windows.Documents.Run(ruta[..(corte + 1)]) { Foreground = P("MutedBrush") });
            texto.Inlines.Add(new System.Windows.Documents.Run(ruta[(corte + 1)..]) { Foreground = P("TextBrush"), FontWeight = FontWeights.SemiBold });
        }
        else texto.Inlines.Add(new System.Windows.Documents.Run(ruta) { Foreground = P("TextBrush") });

        var cuerpo = new StackPanel();
        cuerpo.Children.Add(new Border
        {
            Background = P("Surface2Brush"), BorderBrush = P("LineBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 7), Child = texto,
        });
        if (aviso is not null || ok is not null)
        {
            var marcas = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            if (ok is not null) marcas.Children.Add(Chip("\uE73E  " + ok, "OkBrush", mono: false, iconos: true));
            if (aviso is not null) marcas.Children.Add(Chip("\uE7BA  " + aviso, "WarnBrush", mono: false, iconos: true));
            cuerpo.Children.Add(marcas);
        }
        Grid.SetColumn(cuerpo, 2);
        g.Children.Add(cuerpo);

        var botones = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 1, 0, 0) };
        var copiar = new Button
        {
            Content = "\uE8C8", FontFamily = new FontFamily(Iconos.Fuente), Style = (Style)FindResource("BtnIcono"), ToolTip = "Copiar la ruta",
        };
        copiar.Click += async (_, _) =>
        {
            try { Clipboard.SetText(ruta); } catch { return; }
            copiar.Content = "\uE73E";
            await Task.Delay(1200);
            copiar.Content = "\uE8C8";
        };
        var abrir = new Button
        {
            Content = "\uE838", FontFamily = new FontFamily(Iconos.Fuente), Style = (Style)FindResource("BtnIcono"),
            ToolTip = esArchivo ? "Abrir la carpeta que lo contiene" : "Abrir la carpeta",
        };
        abrir.Click += (_, _) => AbrirCarpeta(esArchivo ? (Path.GetDirectoryName(ruta) ?? ruta) : ruta);
        botones.Children.Add(copiar);
        botones.Children.Add(abrir);
        Grid.SetColumn(botones, 3);
        g.Children.Add(botones);
        return g;
    }

    private void RefrescarConfig()
    {
        if (_s is null) return;
        var c = _s.Ctx;
        PanelResumen.Children.Clear();

        // ---- Componentes: icono, nombre, dónde vive y sus datos como etiquetas
        PanelResumen.Children.Add(TituloResumen("COMPONENTES"));
        if (c.Tiene(Componente.Postgres))
        {
            var pg = c.Config.Postgres!;
            PanelResumen.Children.Add(FilaComponente("POSTGRES", "Postgres", pg.ContenedorEf, $"localhost:{pg.PuertoEf}", pg.ImagenEf));
        }
        foreach (var (comp, clave) in new[] { (Componente.Api, "API"), (Componente.Web, "WEB"), (Componente.Movil, "FLUTTER") })
            if (c.Tiene(comp))
                PanelResumen.Children.Add(FilaComponente(clave, Sesion.Nombres[comp], _s.Rel(c.Carpeta(comp)), c.Url(comp) ?? ""));
        if (c.Tareas.Count > 0)
            PanelResumen.Children.Add(FilaComponente("FLUTTER", "Tareas", "", c.Tareas.Select(t => t.Nombre).ToArray()));

        // ---- Rutas
        PanelResumen.Children.Add(TituloResumen("RUTAS"));
        PanelResumen.Children.Add(FilaRuta("Archivo", "\uE8A5", "#6FA8FF", _s.Store.Ruta, true,
            _s.Store.EnElRepo ? "Está dentro del proyecto: usa «Mover fuera del proyecto»" : null,
            _s.Store.EnElRepo ? null : "Fuera del repositorio"));
        PanelResumen.Children.Add(FilaRuta("Estado", "\uE838", "#22D3EE", _s.Estado.Dir, false, null));
        if (_s.Sdk is not null) PanelResumen.Children.Add(FilaRuta("Android SDK", "\uE8EA", "#F5B94A", _s.Sdk, false, null));

        // ---- Secretos
        PanelResumen.Children.Add(TituloResumen("SECRETOS"));
        var cifrado = StackDev.Core.Seguridad.Proteccion.Disponible;
        var fila = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        fila.Children.Add(Chip(cifrado ? "\uE72E  Cifrados con DPAPI" : "\uE785  Sin cifrar", cifrado ? "OkBrush" : "WarnBrush", mono: false, iconos: true));
        fila.Children.Add(new TextBlock
        {
            Text = cifrado
                ? "Contraseña de Postgres, variables secretas de la API y credenciales. Solo tu usuario en este equipo puede leerlos."
                : "El cifrado DPAPI solo existe en Windows.",
            Foreground = P("MutedBrush"), FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 4), MaxWidth = 560,
        });
        PanelResumen.Children.Add(fila);
        RefrescarNavegadores();
    }

    // ------------------------------------------------------------ Navegador

    private System.Windows.Controls.Primitives.Popup? _menu;

    /// <summary>Lista desplegable oscura bajo un botón (los menús de WPF por defecto salen claros).</summary>
    private void MostrarMenu(FrameworkElement ancla, IEnumerable<(string Texto, Action Accion)> items)
    {
        if (_menu is { IsOpen: true }) _menu.IsOpen = false;
        var lista = new StackPanel { MinWidth = Math.Max(220, ancla.ActualWidth) };
        foreach (var (texto, accion) in items)
        {
            var elegido = accion;
            var b = new Button
            {
                Content = new TextBlock { Text = texto, FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 13, Foreground = P("TextBrush") },
                Style = (Style)FindResource("BtnIcono"),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 6, 10, 6),
            };
            b.Click += (_, _) => { _menu!.IsOpen = false; elegido(); };
            lista.Children.Add(b);
        }
        _menu = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = ancla,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = P("SurfaceBrush"), BorderBrush = P("LineBrush"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(4), Margin = new Thickness(0, 4, 0, 8), Child = lista,
            },
            IsOpen = true,
        };
    }

    private void RefrescarNavegadores()
    {
        BtnNavGlobal.Content = StackDev.Core.Proc.Navegadores.Nombre(_estado?.Navegador) + "   ▾";
        var propio = _s?.Ctx.Config.Navegador;
        BtnNavProyecto.Content = (string.IsNullOrWhiteSpace(propio) ? "Igual que el de todos los proyectos" : StackDev.Core.Proc.Navegadores.Nombre(propio)) + "   ▾";
        BtnNavProyecto.IsEnabled = _s is not null;
        var efectivo = string.IsNullOrWhiteSpace(propio) ? _estado?.Navegador : propio;
        var nombreNav = string.IsNullOrWhiteSpace(efectivo) ? "Predeterminado" : StackDev.Core.Proc.Navegadores.Nombre(efectivo);
        BtnNavRapido.Content = Iconos.ConGlifo("\uE774", nombreNav + "  ▾");
    }

    /// <summary>Cambio rápido de navegador desde el panel: para este proyecto, o (última opción) para todos.</summary>
    private void NavRapido_Click(object sender, RoutedEventArgs e)
    {
        if (_s is null || _estado is null) return;
        var ops = OpcionesNavegador(v =>
        {
            var copia = ConfigJson.Leer(ConfigJson.Escribir(_s.Ctx.Config));
            copia.Navegador = v;
            _s.AplicarConfig(copia);
            RefrescarNavegadores();
        }, "Igual que el de todos los proyectos");
        ops.Add(("Cambiar el de todos los proyectos…", () => Dispatcher.InvokeAsync(() =>
            MostrarMenu(BtnNavRapido, OpcionesNavegador(v => { _estado.Navegador = v; RefrescarNavegadores(); }, null)))));
        MostrarMenu(BtnNavRapido, ops);
    }

    private List<(string Texto, Action Accion)> OpcionesNavegador(Action<string?> poner, string? textoSinValor)
    {
        var ops = new List<(string, Action)>();
        if (textoSinValor is not null) ops.Add((textoSinValor, () => poner(null)));
        ops.Add(("Predeterminado del sistema", () => poner("")));
        foreach (var n in StackDev.Core.Proc.Navegadores.Instalados())
        {
            var id = n.Id;
            ops.Add((n.Nombre, () => poner(id)));
        }
        ops.Add(("Otro programa…", () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Elige el navegador", Filter = "Programas (*.exe)|*.exe" };
            if (dlg.ShowDialog(this) == true) poner(dlg.FileName);
        }));
        return ops;
    }

    private void NavGlobal_Click(object sender, RoutedEventArgs e)
    {
        if (_estado is null) return;
        MostrarMenu(BtnNavGlobal, OpcionesNavegador(v => { _estado.Navegador = v; RefrescarNavegadores(); }, null));
    }

    private void NavProyecto_Click(object sender, RoutedEventArgs e)
    {
        if (_s is null) return;
        MostrarMenu(BtnNavProyecto, OpcionesNavegador(v =>
        {
            var copia = ConfigJson.Leer(ConfigJson.Escribir(_s.Ctx.Config));
            copia.Navegador = v;
            _s.AplicarConfig(copia);
            RefrescarNavegadores();
        }, "Igual que el de todos los proyectos"));
    }

    /// <summary>Botón de la cabecera: abre la web y/o la API del proyecto en el navegador elegido.</summary>
    private void Navegador_Click(object sender, RoutedEventArgs e)
    {
        if (_s is null) return;
        var s = _s;
        var items = new List<(string Texto, Action Accion)>();
        var abribles = new List<Componente>();
        foreach (var (comp, nombre) in new[] { (Componente.Web, "Web"), (Componente.Api, "API") })
        {
            if (!s.Ctx.Tiene(comp) || s.Ctx.UrlParaAbrir(comp) is not { } url) continue;
            var c = comp;
            abribles.Add(c);
            items.Add(($"{nombre}    {url}", () => s.Servicios.AbrirEnNavegador(c)));
        }
        if (items.Count == 0) { _ui.Escribir("Este proyecto no tiene una URL de web o API para abrir.", Nivel.Aviso); return; }
        if (items.Count == 1) { items[0].Accion(); return; }
        items.Add(("Abrir las dos", () => { foreach (var c2 in abribles) s.Servicios.AbrirEnNavegador(c2); }));
        MostrarMenu(BtnNavegador, items);
    }

    private void NavProbar_Click(object sender, RoutedEventArgs e)
    {
        if (_s is null) return;
        var url = _s.Ctx.UrlParaAbrir(Componente.Web) ?? _s.Ctx.UrlParaAbrir(Componente.Api) ?? "https://example.com";
        _s.Ejecutor.AbrirNavegador(url);
    }

    private async void CfgComponentes_Click(object sender, RoutedEventArgs e)
    {
        if (_s is null || _ocupado) return;
        var nueva = ComponentesDialog.Mostrar(this, _s);
        if (nueva is null) return;
        var s = _s;
        await CorrerAsync("Guardar componentes", x => { x.AplicarConfig(nueva); return true; });
        _mon?.Refrescar();
        ConstruirTodo();
        _ui.Escribir("Componentes actualizados.", Nivel.Ok);
    }

    private void CfgMover_Click(object sender, RoutedEventArgs e)
    {
        if (_s is null) return;
        if (!_s.Store.EnElRepo) { _ui.Escribir("La configuración ya está fuera del proyecto.", Nivel.Info); return; }
        var destino = _s.Store.CopiarAfuera();
        _s.Store.Cargar();
        RefrescarConfig();
        _ui.Escribir($"Copiada a {destino}. Desde ahora se usa esa.", Nivel.Ok);
        _ui.Escribir("El dev-menu.json de la carpeta del proyecto no se borró: elimínalo tú (o agrégalo al .gitignore).", Nivel.Aviso);
    }

    private void CfgEditar_Click(object sender, RoutedEventArgs e) => _s?.AbrirEditorConfig();

    private void CfgRedetectar_Click(object sender, RoutedEventArgs e)
    {
        if (_s is null) return;
        if (!Dialogos.Confirmar(this, "Esto reemplaza dev-menu.json (queda una copia .bak). ¿Seguir?", false)) return;
        _ = Redetectar();
    }

    private async Task Redetectar()
    {
        await CorrerAsync("Detectar de nuevo", s => { s.Redetectar(); return true; });
        ConstruirTodo();
    }

    private void CfgSincronizar_Click(object sender, RoutedEventArgs e)
    {
        if (_s is null) return;
        var r = _estado.Migrar(StateStore.DirAnterior(), forzar: true);
        _ui.Escribir($"Estado de previous-state: {r.Copiados} archivos copiados, {r.Omitidos} sin cambios.", Nivel.Ok);
    }

    private void AbrirCarpeta(string ruta)
    {
        try
        {
            Directory.CreateDirectory(ruta);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ruta}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            _ui.Escribir($"No pude abrir {ruta}: {ex.Message}", Nivel.Error);
        }
    }

    private void CfgLogs_Click(object sender, RoutedEventArgs e)
    {
        if (_s is not null) AbrirCarpeta(Path.GetDirectoryName(_s.Servicios.RutaLog("api"))!);
    }

    private void CfgEstado_Click(object sender, RoutedEventArgs e) => AbrirCarpeta(_estado.Dir);

    // ------------------------------------------------------------ Proyecto y cierre

    // ------------------------------------------------------------ Barra superior propia

    private void Minimizar_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximizar_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ------------------------------------------------------------ Segundo plano (bandeja del sistema)

    /// <summary>La X no cierra: manda la aplicación a la bandeja y todo (terminales, API, emulador) sigue vivo. Mayús+clic sale del todo.</summary>
    private void Cerrar_Click(object sender, RoutedEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) Salir(); else OcultarABandeja();
    }

    private void CrearBandeja()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/stackdev.ico"));
            _bandeja = new System.Windows.Forms.NotifyIcon
            {
                Text = "stackdev",
                Icon = info is null ? null : new System.Drawing.Icon(info.Stream),
                Visible = true,
            };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Abrir stackdev", null, (_, _) => Dispatcher.Invoke(MostrarDesdeBandeja));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Salir", null, (_, _) => Dispatcher.Invoke(Salir));
            _bandeja.ContextMenuStrip = menu;
            _bandeja.MouseClick += (_, ev) =>
            {
                if (ev.Button == System.Windows.Forms.MouseButtons.Left) Dispatcher.Invoke(MostrarDesdeBandeja);
            };
        }
        catch (Exception) { _bandeja = null; }   // sin icono de bandeja la X vuelve a cerrar normalmente
    }

    private void OcultarABandeja()
    {
        if (_bandeja is null) { Salir(); return; }
        Hide();
        if (!_avisoBandeja)
        {
            _avisoBandeja = true;
            _bandeja.ShowBalloonTip(2500, "stackdev sigue en segundo plano",
                "Haz clic en el icono para volver a abrirlo. Clic derecho → Salir para cerrarlo del todo.",
                System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    private void MostrarDesdeBandeja()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;   // la trae al frente aunque otra ventana tenga el foco
    }

    private void Salir()
    {
        _salir = true;
        if (!IsVisible) Show();   // el diálogo de «¿qué hago con lo que corre?» necesita la ventana visible
        Close();
    }

    private void Ventana_StateChanged(object? sender, EventArgs e)
    {
        var maximizada = WindowState == WindowState.Maximized;
        // Maximizada, una ventana con marco propio se pasa unos píxeles del borde de la pantalla.
        var rb = SystemParameters.WindowResizeBorderThickness;
        Raiz.Margin = maximizada ? new Thickness(rb.Left, rb.Top, rb.Right, rb.Bottom + 10) : new Thickness(0);
        BtnMaximizar.Content = maximizada ? "\uE923" : "\uE922";
    }

    private void Lateral_Click(object sender, RoutedEventArgs e)
    {
        _rail = !_rail;
        AplicarRail();
    }

    // ------------------------------------------------------------ Terminales: mitad, completo u ocultas

    private void CambiarModoTerminal(ModoTerminal m)
    {
        _modoTerm = m;
        if (m == ModoTerminal.Completo)
        {
            if (NavTerminales.IsChecked != true) { NavTerminales.IsChecked = true; return; }   // Nav_Checked aplica el modo
        }
        else if (VistaActual() == Vista.Terminales)
        {
            (_s is null ? NavInicio : NavPanel).IsChecked = true;   // salir de la vista Terminales; Nav_Checked aplica el modo
            return;
        }
        AplicarModoTerminal();
    }

    private void AplicarModoTerminal(bool crearPrimera = true)
    {
        var m = _modoTerm;
        Principal.Visibility = m == ModoTerminal.Completo ? Visibility.Collapsed : Visibility.Visible;
        ColPrincipal.MinWidth = m == ModoTerminal.Mitad ? 460 : 0;
        // En mitad el panel cede espacio: 2/5 para las tarjetas, 3/5 para la terminal
        ColPrincipal.Width = m == ModoTerminal.Completo ? new GridLength(0)
            : new GridLength(m == ModoTerminal.Mitad && _modoAplicado == ModoTerminal.Oculto ? 2 : 1, GridUnitType.Star);
        DockTerminal.Visibility = m == ModoTerminal.Oculto ? Visibility.Collapsed : Visibility.Visible;
        ColTerminal.MinWidth = m == ModoTerminal.Oculto ? 0 : 360;
        ColTerminal.Width = m == ModoTerminal.Oculto ? new GridLength(0)
            : new GridLength(m == ModoTerminal.Mitad && _modoAplicado == ModoTerminal.Oculto ? 3 : 1, GridUnitType.Star);
        DivisorTerminal.Visibility = m == ModoTerminal.Mitad ? Visibility.Visible : Visibility.Collapsed;
        _terminales?.MarcarCompleto(m == ModoTerminal.Completo);
        Dispatcher.InvokeAsync(AjustarTarjetas, DispatcherPriority.Loaded);

        var abre = _modoAplicado == ModoTerminal.Oculto && m != ModoTerminal.Oculto;
        var cierra = _modoAplicado != ModoTerminal.Oculto && m == ModoTerminal.Oculto;
        _modoAplicado = m;
        if (abre) { _railAntes = _rail; _rail = true; }       // con las terminales abiertas el menú pasa a iconos
        else if (cierra) _rail = _railAntes;
        AplicarRail();
        if (abre && crearPrimera) _terminales?.AlMostrar();
    }

    /// <summary>Abre (si hace falta, a la mitad) la terminal de una carpeta; si ya hay una pestaña para ella, la muestra.</summary>
    private void AbrirTerminal(string etiqueta, string carpeta)
    {
        if (_terminales is null) return;
        if (_modoTerm == ModoTerminal.Oculto)
        {
            _modoTerm = ModoTerminal.Mitad;
            AplicarModoTerminal(crearPrimera: false);
        }
        _terminales.Abrir(etiqueta, carpeta);
    }

    /// <summary>Abre una pestaña de terminal que ejecuta una línea propia (ssh, logs del servidor…).</summary>
    private void AbrirTerminalComando(string etiqueta, string linea)
    {
        if (_terminales is null) return;
        if (_modoTerm == ModoTerminal.Oculto)
        {
            _modoTerm = ModoTerminal.Mitad;
            AplicarModoTerminal(crearPrimera: false);
        }
        _terminales.AbrirComando(etiqueta, linea);
    }

    /// <summary>Guarda en dev-menu.json a qué servidor se sube este proyecto.</summary>
    private void GuardarDespliegue(StackDev.Core.Config.DespliegueConfig? d)
    {
        if (_s is null) return;
        var copia = StackDev.Core.Config.ConfigJson.Leer(StackDev.Core.Config.ConfigJson.Escribir(_s.Ctx.Config));
        copia.Despliegue = d;
        _s.AplicarConfig(copia);
        _ui.Escribir("Despliegue del proyecto guardado.", Nivel.Ok);
    }

    /// <summary>Atajos de terminales: funcionan igual con el foco en la ventana o dentro de una terminal.</summary>
    private void AtajoTerminal(string accion, int n)
    {
        if (_terminales is null || _s is null) return;
        if (accion == "toggle")
        {
            CambiarModoTerminal(_modoTerm == ModoTerminal.Oculto ? ModoTerminal.Mitad : ModoTerminal.Oculto);
            return;
        }
        var estabaOculto = _modoTerm == ModoTerminal.Oculto;
        if (estabaOculto)
        {
            _modoTerm = ModoTerminal.Mitad;
            AplicarModoTerminal();          // crea la primera terminal si no hay ninguna
            if (accion is "next" or "prev") return;   // ya se muestra la primera; no saltar de pestaña
        }
        _terminales.Atajo(accion, n);
    }

    private void TerminalRaiz_Click(object sender, RoutedEventArgs e)
    {
        if (_s is not null) AbrirTerminal("raíz", _s.Raiz);
    }

    private (string Etiqueta, string Carpeta)? TerminalDeTarjeta(string seccion)
    {
        if (_s is null) return null;
        return seccion switch
        {
            "POSTGRES" => ("raíz", _s.Raiz),
            "API" => ("api", CarpetaTerminal(Componente.Api, "api", "backend", "server", "src")),
            "WEB" => ("web", CarpetaTerminal(Componente.Web, "web", "frontend", "client", "app")),
            "FLUTTER" => ("móvil", CarpetaTerminal(Componente.Movil, "mobile", "movil", "app", "flutter")),
            _ => null,
        };
    }

    /// <summary>
    /// Carpeta configurada del componente; si es la propia raíz (los comandos usan rutas relativas),
    /// se prefiere la subcarpeta con el nombre típico, para que la terminal abra donde se trabaja.
    /// </summary>
    private string CarpetaTerminal(Componente c, params string[] candidatas)
    {
        var raiz = _s!.Raiz;
        var conf = _s.Ctx.Carpeta(c);
        if (!string.Equals(Path.GetFullPath(conf).TrimEnd('\\', '/'), Path.GetFullPath(raiz).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return conf;
        foreach (var n in candidatas)
        {
            var dir = Path.Combine(raiz, n);
            if (Directory.Exists(dir)) return dir;
        }
        return conf;
    }

    private static readonly (string Texto, string Glifo)[] NavIconos =
    {
        ("Proyectos", "\uE8B7"),
        ("Panel", "\uE80A"),
        ("Terminales", "\uE756"),
        ("Diagnóstico", "\uE9D9"),
        ("Servidor", "\uE753"),
        ("Condiciones del dispositivo", "\uE717"),
        ("Consola de comandos", "\uE943"),
        ("Configuración", "\uE713"),
    };

    /// <summary>Menú lateral completo (texto) o reducido a iconos pequeños.</summary>
    private void AplicarRail()
    {
        ColLateral.Width = new GridLength(_rail ? 60 : 240);
        TarjetaProyecto.Visibility = PieLateral.Visibility = _rail ? Visibility.Collapsed : Visibility.Visible;
        LateralInterno.Margin = _rail ? new Thickness(8, 16, 8, 16) : new Thickness(16);
        var botones = new[] { NavInicio, NavPanel, NavTerminales, NavDiag, NavServidor, NavCond, NavConsola, NavConfig };
        for (var i = 0; i < botones.Length; i++)
        {
            var (texto, glifo) = NavIconos[i];
            botones[i].ToolTip = _rail ? texto : null;
            botones[i].Content = _rail
                ? new TextBlock
                {
                    Text = glifo,
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                }
                : ConIconoNav(glifo, texto);
        }
    }

    private static object ConIconoNav(string glifo, string texto)
    {
        var fila = new StackPanel { Orientation = Orientation.Horizontal };
        fila.Children.Add(new TextBlock
        {
            Text = glifo,
            FontFamily = new FontFamily(Iconos.Fuente),
            FontSize = 15,
            Width = 22,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });
        fila.Children.Add(new TextBlock { Text = texto, VerticalAlignment = VerticalAlignment.Center });
        return fila;
    }

    private void Atras_Click(object sender, RoutedEventArgs e) => NavInicio.IsChecked = true;

    private bool EsFavorito(string? ruta)
    {
        var fav = _estado.Favorito;
        if (string.IsNullOrWhiteSpace(fav) || string.IsNullOrWhiteSpace(ruta)) return false;
        return string.Equals(Path.GetFullPath(fav), Path.GetFullPath(ruta), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Solo uno puede ser favorito: marcar otro quita el anterior; volver a pulsar lo quita.</summary>
    private void AlternarFavorito(string ruta)
    {
        if (EsFavorito(ruta))
        {
            _estado.Favorito = null;
            _ui.Escribir("Ya no hay proyecto favorito.", Nivel.Tenue);
        }
        else
        {
            _estado.Favorito = Path.GetFullPath(ruta);
            _ui.Escribir($"Favorito: {ruta}. Se abrirá al iniciar la aplicación.", Nivel.Ok);
        }
        ActualizarFavorito();
        if (VistaActual() == Vista.Inicio) RefrescarInicio();
    }

    // ------------------------------------------------------------ Chip del proyecto (~/proyecto on rama*)

    private string? _raizGit;
    private string? _rama;
    private bool _sucio;
    private DateTime _ultimoGit = DateTime.MinValue;

    private void PintarChip()
    {
        if (_s is null)
        {
            ChipProyecto.Visibility = Visibility.Collapsed;
            TxtTituloVentana.Visibility = Visibility.Visible;
            return;
        }
        ChipProyecto.Visibility = Visibility.Visible;
        TxtTituloVentana.Visibility = Visibility.Collapsed;
        var t = new TextBlock { FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        t.Inlines.Add(new Run("~/") { Foreground = P("MutedBrush") });
        t.Inlines.Add(new Run(_s.Ctx.Nombre) { Foreground = P("TextBrush") });
        if (_rama is not null)
        {
            t.Inlines.Add(new Run("  on  ") { Foreground = P("MutedBrush") });
            t.Inlines.Add(new Run(_rama) { Foreground = P("AccentBrush") });
            if (_sucio) t.Inlines.Add(new Run("*") { Foreground = P("WarnBrush") });
        }
        t.Inlines.Add(new Run("   ▾") { Foreground = P("MutedBrush") });
        ChipProyecto.Content = t;
        ChipProyecto.ToolTip = $"{_s.Raiz}\nCambiar de proyecto (Ctrl+P)";
    }

    private async Task ActualizarGitAsync()
    {
        var raiz = _s?.Raiz;
        if (raiz is null) return;
        var (rama, sucio) = await Task.Run(() => LeerGit(raiz));
        if (!string.Equals(raiz, _s?.Raiz, StringComparison.OrdinalIgnoreCase)) return;
        _rama = rama;
        _sucio = sucio;
        PintarChip();
    }

    private static (string? Rama, bool Sucio) LeerGit(string raiz)
    {
        static string? Git(string raiz, string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("git", $"-C \"{raiz}\" {args}")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (p is null) return null;
                var salida = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return null; }
                return p.ExitCode == 0 ? salida.Trim() : null;
            }
            catch { return null; }
        }

        var rama = Git(raiz, "rev-parse --abbrev-ref HEAD");
        if (string.IsNullOrEmpty(rama)) return (null, false);
        if (rama == "HEAD") rama = Git(raiz, "rev-parse --short HEAD") ?? "HEAD";
        var sucio = !string.IsNullOrEmpty(Git(raiz, "status --porcelain"));
        return (rama, sucio);
    }

    private void ActualizarFavorito()
    {
        BtnFavoritoBarra.Visibility = _s is null ? Visibility.Collapsed : Visibility.Visible;
        BtnCambiarBarra.Visibility = Visibility.Collapsed;   // lo reemplaza el chip del proyecto
        if (!string.Equals(_raizGit, _s?.Raiz, StringComparison.OrdinalIgnoreCase))
        {
            _raizGit = _s?.Raiz;
            _rama = null;
            _sucio = false;
            _ultimoGit = DateTime.UtcNow;
            _ = ActualizarGitAsync();
        }
        PintarChip();
        var fav = _s is not null && EsFavorito(_s.Raiz);
        BtnFavoritoBarra.Content = fav ? "★" : "☆";
        BtnFavoritoBarra.Foreground = P(fav ? "WarnBrush" : "MutedBrush");
    }

    private void FavoritoBarra_Click(object sender, RoutedEventArgs e)
    {
        if (_s is not null) AlternarFavorito(_s.Raiz);
    }

    private void CambiarProyecto_Click(object sender, RoutedEventArgs e) => NavInicio.IsChecked = true;

    private async void AbrirCarpeta_Click(object sender, RoutedEventArgs e)
    {
        if (_ocupado) return;
        var ruta = Dialogos.Proyecto(this, _estado.UltimoProyecto);
        if (ruta is null) return;
        await AbrirDesdeInicio(ruta);
    }

    private async Task AbrirDesdeInicio(string ruta)
    {
        if (_ocupado) { _ui.Escribir("Espera a que termine la acción en curso.", Nivel.Aviso); return; }
        if (!Directory.Exists(ruta)) { _ui.Escribir($"Esa carpeta ya no existe: {ruta}", Nivel.Aviso); RefrescarInicio(); return; }
        if (_s is not null && string.Equals(Path.GetFullPath(ruta), _s.Raiz, StringComparison.OrdinalIgnoreCase))
        {
            NavPanel.IsChecked = true;
            return;
        }
        await AbrirProyectoAsync(ruta);
    }

    private void ActualizarNav()
    {
        var hay = _s is not null;
        TxtBarra.IsEnabled = BtnBarra.IsEnabled = hay && !_ocupado;
        ActualizarFavorito();
        NavPanel.IsEnabled = NavTerminales.IsEnabled = NavDiag.IsEnabled = NavCond.IsEnabled = NavConsola.IsEnabled = NavConfig.IsEnabled = hay;
        if (!hay)
        {
            TxtProyecto.Text = "Sin proyecto";
            TxtRuta.Text = "";
            TxtModo.Text = "—";
            TxtAvd.Text = "—";
            TxtDirBarra.Text = ">";
        }
    }

    private void RefrescarInicio()
    {
        PanelInicio.Children.Clear();
        var lista = _estado.LeerProyectos();
        if (lista.Count == 0)
        {
            PanelInicio.Children.Add(new TextBlock
            {
                Text = "Aún no has abierto ningún proyecto. Usa «Abrir carpeta…» para empezar.",
                Foreground = P("MutedBrush"),
            });
            return;
        }

        foreach (var ruta in lista)
        {
            var carpeta = ruta;
            var existe = Directory.Exists(ruta);
            var nombre = Path.GetFileName(ruta.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(nombre)) nombre = ruta;
            var partes = new List<string>();
            if (existe)
            {
                try
                {
                    var st = new ConfigStore(ruta, _estado.CarpetaConfig(StateStore.Clave(ruta)));
                    if (st.Existe)
                    {
                        st.Cargar();
                        if (st.Error is null)
                        {
                            var c = st.Contexto;
                            nombre = c.Nombre;
                            if (c.Tiene(Componente.Postgres)) partes.Add("Postgres");
                            if (c.Tiene(Componente.Api)) partes.Add("API");
                            if (c.Tiene(Componente.Web)) partes.Add("Web");
                            if (c.Tiene(Componente.Movil)) partes.Add("Móvil");
                        }
                    }
                }
                catch (Exception) { /* una configuración dañada no debe romper la lista */ }
            }

            var abierto = _s is not null && string.Equals(_s.Raiz, Path.GetFullPath(ruta), StringComparison.OrdinalIgnoreCase);

            var esFav = EsFavorito(ruta);
            var cabecera = new Grid();
            cabecera.ColumnDefinitions.Add(new ColumnDefinition());
            cabecera.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cabecera.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cabecera.Children.Add(new TextBlock { Text = nombre, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            if (abierto || !existe)
            {
                var etiqueta = new TextBlock
                {
                    Text = abierto ? "ABIERTO" : "NO EXISTE",
                    FontSize = 10,
                    Foreground = P(abierto ? "OkBrush" : "WarnBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                };
                Grid.SetColumn(etiqueta, 1);
                cabecera.Children.Add(etiqueta);
            }
            var estrella = new Button
            {
                Content = esFav ? "★" : "☆",
                Style = (Style)FindResource("BtnIcono"),
                Foreground = P(esFav ? "WarnBrush" : "MutedBrush"),
                ToolTip = esFav ? "Quitar de favorito" : "Hacer favorito: se abrirá al iniciar la aplicación",
            };
            estrella.Click += (_, _) => AlternarFavorito(carpeta);
            Grid.SetColumn(estrella, 2);
            cabecera.Children.Add(estrella);

            var botones = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            var abrir = new Button { Content = abierto ? "Ir al panel" : "Abrir", Style = (Style)FindResource("BtnChip"), Background = P("AccentBrush"), BorderBrush = P("AccentBrush"), Foreground = P("AccentOnBrush") };
            abrir.Click += async (_, _) => await AbrirDesdeInicio(carpeta);
            var quitar = new Button { Content = "Quitar de la lista", Style = (Style)FindResource("BtnChip") };
            quitar.Click += (_, _) => { _estado.QuitarProyecto(carpeta); RefrescarInicio(); };
            botones.Children.Add(abrir);
            botones.Children.Add(quitar);

            var contenido = new StackPanel();
            contenido.Children.Add(cabecera);
            contenido.Children.Add(new TextBlock { Text = ruta, FontSize = 11, Foreground = P("MutedBrush"), Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = ruta });
            if (partes.Count > 0)
                contenido.Children.Add(new TextBlock { Text = string.Join("  ·  ", partes), FontSize = 12, Foreground = P("MutedBrush"), Margin = new Thickness(0, 8, 0, 0) });
            contenido.Children.Add(botones);

            PanelInicio.Children.Add(new Border
            {
                Style = (Style)FindResource("Card"),
                Width = 380,
                Margin = new Thickness(0, 0, 16, 16),
                Child = contenido,
            });
        }
        Dispatcher.InvokeAsync(AjustarInicio, DispatcherPriority.Loaded);
    }

    private async void AlCerrar(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_salir && !_cerrando && _bandeja is not null)
        {
            e.Cancel = true;      // Alt+F4 o cerrar desde la barra de tareas: también a segundo plano
            OcultarABandeja();
            return;
        }
        if (_cerrando) { _bandeja?.Dispose(); _terminales?.CerrarTodo(); _mon?.Dispose(); return; }
        if (_s is null) { _bandeja?.Dispose(); _terminales?.CerrarTodo(); return; }

        e.Cancel = true;   // se decide primero qué hacer con lo que sigue corriendo
        _salir = false;    // si al final se cancela, la próxima X vuelve a ir a segundo plano
        if (_ocupado)
        {
            if (!Dialogos.Confirmar(this, "Hay una acción en curso. ¿Cancelarla y salir?", false)) return;
            _cts?.Cancel();
            while (_ocupado) await Task.Delay(100);
        }

        var s = _s;
        Instantanea? foto = null;
        _timer.Stop();
        try { foto = await Task.Run(() => Instantanea.Tomar(s)); } catch (Exception) { }

        if (foto is { HayAlgoCorriendo: true })
        {
            var i = Dialogos.Elegir(this, "Hay cosas corriendo. ¿Qué hago?",
                new[] { "Detener todo y salir", "Salir y dejar todo corriendo" }, 0);
            if (i < 0) { _timer.Start(); return; }
            if (i == 0)
            {
                await CorrerAsync("Detener todo", x => x.Todo.Detener());
            }
        }
        _cerrando = true;
        Close();
    }
}
