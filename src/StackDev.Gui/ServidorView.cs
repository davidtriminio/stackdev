using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StackDev.Core;
using StackDev.Core.Config;
using StackDev.Core.Servidores;
using StackDev.Core.Ui;

namespace StackDev.Gui;

/// <summary>
/// «Servidor»: lista global de servidores (cifrada, fuera del repo), prueba de conexión SSH,
/// estado del servicio de la API de este proyecto y logs en vivo en una pestaña de terminal.
/// </summary>
public sealed class ServidorView : ScrollViewer
{
    private const string FuenteIconos = "Segoe Fluent Icons, Segoe MDL2 Assets";

    private readonly Window _owner;
    private readonly Func<Sesion?> _sesion;
    private readonly Func<ServidoresStore> _tienda;
    private ServidoresStore _store => _tienda();
    private readonly Action<string, string> _terminal;
    private readonly Action<DespliegueConfig?> _guardarDespliegue;
    private readonly Action<string, Nivel> _log;
    private readonly StackPanel _raiz = new() { Margin = new Thickness(0, 0, 0, 24) };
    private readonly Dictionary<string, string> _pruebas = new();
    private EstadoApi? _estado;
    private string _estadoDe = "";
    private bool _consultando;

    public ServidorView(Window owner, Func<Sesion?> sesion, Func<ServidoresStore> tienda,
        Action<string, string> terminal, Action<DespliegueConfig?> guardarDespliegue, Action<string, Nivel> log)
    {
        _owner = owner;
        _sesion = sesion;
        _tienda = tienda;
        _terminal = terminal;
        _guardarDespliegue = guardarDespliegue;
        _log = log;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Content = _raiz;
    }

    private static Brush P(string clave) => (Brush)Application.Current.FindResource(clave);
    private static Style Estilo(string clave) => (Style)Application.Current.FindResource(clave);

    /// <summary>Se llama al abrir la sección y después de cada cambio.</summary>
    public void Actualizar()
    {
        Dibujar();
        _ = ConsultarEstadoAsync();
    }

    // ------------------------------------------------------------ piezas

    private static TextBlock Texto(string t, double size = 13, Brush? color = null, bool mono = false, FontWeight? peso = null) => new()
    {
        Text = t,
        FontSize = size,
        Foreground = color ?? P("TextBrush"),
        FontFamily = mono ? new FontFamily("Cascadia Mono, Consolas") : new FontFamily("Segoe UI"),
        FontWeight = peso ?? FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap,
    };

    private static Button Boton(string texto, Action accion, bool primario = false, bool peligro = false)
    {
        var b = new Button { Content = texto, Style = Estilo(primario ? "BtnPrimary" : peligro ? "BtnDanger" : "BtnChip") };
        b.Click += (_, _) => accion();
        return b;
    }

    private static Border Tarjeta(string glifo, string colorGlifo, string titulo, string? subtitulo, UIElement cuerpo, UIElement? derecha = null)
    {
        var ficha = new Border
        {
            Style = Estilo("FichaGlifo"),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorGlifo)) { Opacity = 0.14 },
            Child = new TextBlock
            {
                Text = glifo,
                FontFamily = new FontFamily(FuenteIconos),
                FontSize = 16,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorGlifo)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var cab = new Grid();
        cab.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cab.ColumnDefinitions.Add(new ColumnDefinition());
        cab.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ficha.Margin = new Thickness(0, 0, 12, 0);
        cab.Children.Add(ficha);
        var tit = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        tit.Children.Add(Texto(titulo, 15, null, false, FontWeights.SemiBold));
        if (subtitulo is not null) tit.Children.Add(Texto(subtitulo, 11.5, P("MutedBrush"), true));
        Grid.SetColumn(tit, 1);
        cab.Children.Add(tit);
        if (derecha is not null) { Grid.SetColumn(derecha, 2); cab.Children.Add(derecha); }

        var pila = new StackPanel();
        pila.Children.Add(cab);
        if (cuerpo is FrameworkElement fe) fe.Margin = new Thickness(0, 12, 0, 0);
        pila.Children.Add(cuerpo);
        return new Border { Style = Estilo("Card"), Child = pila, Margin = new Thickness(0, 0, 0, 12) };
    }

    private static Border Chip(string texto, Brush color) => new()
    {
        Background = new SolidColorBrush(((SolidColorBrush)color).Color) { Opacity = 0.16 },
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(8, 2, 8, 2),
        Margin = new Thickness(0, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = Texto(texto, 11, color, true),
    };

    // ------------------------------------------------------------ dibujo

    private void Dibujar()
    {
        _raiz.Children.Clear();
        var servidores = _store.Leer();
        var s = _sesion();
        var desp = s?.Ctx.Config.Despliegue;

        // --- Este proyecto
        if (s is not null)
            _raiz.Children.Add(TarjetaProyecto(s, servidores, desp));

        // --- Servidores
        var cabecera = new Grid { Margin = new Thickness(0, 6, 0, 10) };
        cabecera.ColumnDefinitions.Add(new ColumnDefinition());
        cabecera.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titulo = new TextBlock { Text = "SERVIDORES", Style = Estilo("Etiqueta"), VerticalAlignment = VerticalAlignment.Center };
        cabecera.Children.Add(titulo);
        var agregar = Boton("Agregar servidor", AgregarServidor, primario: true);
        Grid.SetColumn(agregar, 1);
        cabecera.Children.Add(agregar);
        _raiz.Children.Add(cabecera);

        if (servidores.Count == 0)
        {
            _raiz.Children.Add(Tarjeta("\uE753", "#6FA8FF", "Aún no tienes servidores",
                "se guardan cifrados, fuera del repositorio",
                Texto("Agrega el primero con su IP, usuario y clave SSH. Puedes tener varios (Oracle, VPS…) y cada proyecto elige a cuál se sube.", 12.5, P("MutedBrush"))));
            return;
        }
        foreach (var sv in servidores) _raiz.Children.Add(TarjetaServidor(sv));
    }

    private UIElement TarjetaServidor(Servidor sv)
    {
        var cuerpo = new StackPanel();
        _pruebas.TryGetValue(sv.Id, out var prueba);
        if (!string.IsNullOrEmpty(prueba))
        {
            var ok = prueba.StartsWith("✔");
            var t = Texto(prueba, 12, ok ? P("OkBrush") : P("WarnBrush"));
            t.Margin = new Thickness(0, 0, 0, 10);
            cuerpo.Children.Add(t);
        }
        var botones = new WrapPanel();
        botones.Children.Add(Boton("Probar conexión", () => _ = ProbarAsync(sv), primario: true));
        botones.Children.Add(Boton("Terminal SSH", () => _terminal($"ssh {sv.Nombre}", Ssh.LineaTerminal(sv, ""))));
        botones.Children.Add(Boton("Editar", () => EditarServidor(sv)));
        botones.Children.Add(Boton("Eliminar", () => EliminarServidor(sv), peligro: true));
        cuerpo.Children.Add(botones);

        var chips = new StackPanel { Orientation = Orientation.Horizontal };
        chips.Children.Add(Chip(sv.WebNombre, P("AccentBrush")));
        return Tarjeta("\uE753", "#6FA8FF", sv.Nombre, sv.Destino, cuerpo, chips);
    }

    private UIElement TarjetaProyecto(Sesion s, List<Servidor> servidores, DespliegueConfig? desp)
    {
        var nombre = s.Ctx.Nombre;
        var sv = servidores.FirstOrDefault(x => x.Id == desp?.Servidor);
        var cuerpo = new StackPanel();

        if (desp is null || sv is null)
        {
            cuerpo.Children.Add(Texto(desp is null
                ? "Este proyecto aún no tiene un servidor. Elige a cuál se sube su API y cómo se llama el servicio."
                : "El servidor que usaba este proyecto ya no está en la lista. Elige otro.", 12.5, P("MutedBrush")));
            var fila = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            fila.Children.Add(Boton("Configurar despliegue", () => ConfigurarDespliegue(servidores, desp), primario: true));
            cuerpo.Children.Add(fila);
            return Tarjeta("\uE8B7", "#6EE787", $"Este proyecto · {nombre}", null, cuerpo);
        }

        var datos = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        datos.Children.Add(Chip(sv.Nombre, P("AccentBrush")));
        datos.Children.Add(Chip(desp.Servicio ?? "?", P("MutedBrush")));
        if (!string.IsNullOrEmpty(desp.Dominio)) datos.Children.Add(Chip(desp.Dominio!, P("MutedBrush")));
        cuerpo.Children.Add(datos);

        cuerpo.Children.Add(PanelEstado());

        var botones = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var svc = desp.Servicio ?? "";
        botones.Children.Add(Boton("Logs en vivo", () => _terminal($"logs {svc}", Ssh.LineaTerminal(sv, $"sudo journalctl -u {svc} -f -n 100 --no-pager")), primario: true));
        botones.Children.Add(Boton("Actualizar estado", () => _ = ConsultarEstadoAsync(forzar: true)));
        botones.Children.Add(Boton("Reiniciar", () => _ = AccionAsync(sv, svc, "restart", "reiniciar")));
        botones.Children.Add(Boton("Detener", () => _ = AccionAsync(sv, svc, "stop", "detener")));
        botones.Children.Add(Boton("Iniciar", () => _ = AccionAsync(sv, svc, "start", "iniciar")));
        botones.Children.Add(Boton("Cambiar", () => ConfigurarDespliegue(servidores, desp)));
        cuerpo.Children.Add(botones);

        return Tarjeta("\uE8B7", "#6EE787", $"Este proyecto · {nombre}", $"{sv.Destino}  ·  {sv.WebNombre}", cuerpo);
    }

    private UIElement PanelEstado()
    {
        var marco = new Border { Background = P("Surface2Brush"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10) };
        var pila = new StackPanel();
        marco.Child = pila;

        if (_consultando && _estado is null)
        {
            pila.Children.Add(Texto("Consultando el servidor…", 12.5, P("MutedBrush"), true));
            return marco;
        }
        if (_estado is null)
        {
            pila.Children.Add(Texto("Sin consultar todavía.", 12.5, P("MutedBrush"), true));
            return marco;
        }
        var e = _estado;
        if (e.Problema is not null)
        {
            pila.Children.Add(Texto("✘ " + e.Problema, 12.5, P("ErrBrush")));
            return marco;
        }
        var color = e.Activo ? P("OkBrush") : P("ErrBrush");
        var linea = new WrapPanel();
        linea.Children.Add(Chip((e.Activo ? "● " : "○ ") + e.Estado, color));
        if (!string.IsNullOrEmpty(e.Desde)) linea.Children.Add(Chip("desde " + e.Desde, P("MutedBrush")));
        if (e.Http is int h) linea.Children.Add(Chip("HTTPS " + h, h is >= 200 and < 400 ? P("OkBrush") : P("WarnBrush")));
        if (e.CertVence is DateTime v)
        {
            var dias = (int)Math.Floor((v - DateTime.Now).TotalDays);
            linea.Children.Add(Chip($"certificado: {dias} días", dias < 15 ? P("WarnBrush") : P("OkBrush")));
        }
        pila.Children.Add(linea);
        if (!string.IsNullOrWhiteSpace(e.Errores))
        {
            var t = Texto("Últimos errores del servicio:", 11.5, P("WarnBrush"));
            t.Margin = new Thickness(0, 8, 0, 4);
            pila.Children.Add(t);
            pila.Children.Add(Texto(e.Errores.Trim(), 11.5, P("MutedBrush"), true));
        }
        return marco;
    }

    // ------------------------------------------------------------ acciones

    private async Task ConsultarEstadoAsync(bool forzar = false)
    {
        var s = _sesion();
        var desp = s?.Ctx.Config.Despliegue;
        var sv = _store.Buscar(desp?.Servidor);
        if (s is null || desp is null || sv is null || string.IsNullOrEmpty(desp.Servicio) || _consultando) return;
        var clave = s.Raiz + "|" + sv.Id + "|" + desp.Servicio;
        if (!forzar && _estado is not null && _estadoDe == clave) return;

        _consultando = true;
        if (_estadoDe != clave) _estado = null;
        Dibujar();
        var servicio = desp.Servicio;
        var dominio = desp.Dominio;
        var r = await Task.Run(() => ServidorService.Estado(sv, servicio, dominio));
        _estado = r;
        _estadoDe = clave;
        _consultando = false;
        Dibujar();
    }

    private async Task ProbarAsync(Servidor sv)
    {
        _pruebas[sv.Id] = "Conectando…";
        Dibujar();
        var r = await Task.Run(() => ServidorService.Probar(sv));
        _pruebas[sv.Id] = (r.Ok ? "✔ " : "✘ ") + r.Mensaje;
        Dibujar();
    }

    private async Task AccionAsync(Servidor sv, string servicio, string accion, string verbo)
    {
        _log($"Servidor: {verbo} {servicio} en {sv.Nombre}…", Nivel.Info);
        var r = await Task.Run(() => ServidorService.Accion(sv, servicio, accion));
        _log(r.Ok ? $"✔ {r.Mensaje}" : $"✘ {r.Mensaje}", r.Ok ? Nivel.Ok : Nivel.Error);
        await ConsultarEstadoAsync(forzar: true);
    }

    private void AgregarServidor()
    {
        var nuevo = ServidorDialogos.EditarServidor(_owner, null);
        if (nuevo is null) return;
        var lista = _store.Leer();
        lista.Add(nuevo);
        _store.Guardar(lista);
        Actualizar();
    }

    private void EditarServidor(Servidor sv)
    {
        var editado = ServidorDialogos.EditarServidor(_owner, sv);
        if (editado is null) return;
        var lista = _store.Leer();
        var i = lista.FindIndex(x => x.Id == sv.Id);
        if (i >= 0) lista[i] = editado;
        _store.Guardar(lista);
        _pruebas.Remove(sv.Id);
        _estadoDe = "";
        Actualizar();
    }

    private void EliminarServidor(Servidor sv)
    {
        if (!Dialogos.Confirmar(_owner, $"¿Quitar «{sv.Nombre}» de la lista? No toca nada en el servidor.", false)) return;
        _store.Guardar(_store.Leer().Where(x => x.Id != sv.Id));
        _pruebas.Remove(sv.Id);
        Actualizar();
    }

    private void ConfigurarDespliegue(List<Servidor> servidores, DespliegueConfig? actual)
    {
        var s = _sesion();
        if (s is null) return;
        var sugerencia = new string(s.Ctx.Nombre.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : '-').ToArray()).Trim('-');
        if (sugerencia.Length == 0 || !char.IsLetter(sugerencia[0])) sugerencia = "api";
        var nuevo = ServidorDialogos.EditarDespliegue(_owner, servidores, actual, sugerencia + "-api", "");
        if (nuevo is null) return;
        _guardarDespliegue(nuevo);
        _estado = null;
        _estadoDe = "";
        Actualizar();
    }
}
