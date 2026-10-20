using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StackDev.Core;
using StackDev.Core.Config;
using StackDev.Core.Diagnostico;
using StackDev.Core.Docker;

namespace StackDev.Gui;

/// <summary>
/// «Diagnóstico»: compara lo configurado con la realidad (Docker, puertos, carpetas, SDK) y ofrece arreglarlo,
/// y administra los contenedores y volúmenes de Docker (iniciar, detener, logs, eliminar).
/// </summary>
public sealed class DiagnosticoView : ScrollViewer
{
    private const string FuenteIconos = "Segoe Fluent Icons, Segoe MDL2 Assets";

    private readonly Window _owner;
    private readonly Func<Sesion?> _sesion;
    private readonly Func<string, Func<Sesion, bool>, Task<bool>> _correr;
    private readonly StackPanel _raiz = new();
    private bool _cargando;

    public DiagnosticoView(Window owner, Func<Sesion?> sesion, Func<string, Func<Sesion, bool>, Task<bool>> correr)
    {
        _owner = owner;
        _sesion = sesion;
        _correr = correr;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Content = _raiz;
    }

    private static Brush P(string clave) => (Brush)Application.Current.FindResource(clave);
    private static Style Estilo(string clave) => (Style)Application.Current.FindResource(clave);

    private static Brush ColorDe(Gravedad g) => g switch
    {
        Gravedad.Ok => P("OkBrush"),
        Gravedad.Aviso => P("WarnBrush"),
        Gravedad.Error => P("ErrBrush"),
        _ => P("MutedBrush"),
    };

    private static string GlifoDe(Gravedad g) => g switch
    {
        Gravedad.Ok => "",       // check
        Gravedad.Aviso => "",    // advertencia
        Gravedad.Error => "",    // error
        _ => "",                 // info
    };

    /// <summary>Se llama al abrir la sección y después de cada arreglo.</summary>
    public async Task ActualizarAsync()
    {
        var s = _sesion();
        if (s is null || _cargando) return;
        _cargando = true;
        _raiz.Children.Clear();
        _raiz.Children.Add(new TextBlock { Text = "Revisando…", Foreground = P("MutedBrush"), Margin = new Thickness(2, 8, 0, 0) });

        List<Hallazgo> hallazgos;
        List<ContenedorDocker>? contenedores = null;
        List<VolumenDocker>? volumenes = null;
        try
        {
            (hallazgos, contenedores, volumenes) = await Task.Run(() =>
            {
                var h = s.Revisor.Revisar();
                var c = s.Docker.Instalado ? s.Admin.Contenedores() : null;
                var v = c is null ? null : s.Admin.Volumenes();
                return (h, c, v);
            });
        }
        catch (Exception ex)
        {
            hallazgos = new() { new(Gravedad.Error, "General", "El diagnóstico falló", ex.Message) };
        }
        finally { _cargando = false; }

        Construir(s, hallazgos, contenedores, volumenes);
    }

    private void Construir(Sesion s, List<Hallazgo> hallazgos, List<ContenedorDocker>? contenedores, List<VolumenDocker>? volumenes)
    {
        _raiz.Children.Clear();

        // ---- Cabecera
        var cab = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var otra = Boton("Revisar de nuevo", async () => await ActualizarAsync(), primario: false);
        DockPanel.SetDock(otra, Dock.Right);
        cab.Children.Add(otra);
        cab.Children.Add(new TextBlock
        {
            Text = "Lo que encontré",
            FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        });
        _raiz.Children.Add(cab);

        // ---- Hallazgos (errores primero)
        foreach (var h in hallazgos.OrderByDescending(x => x.Gravedad))
            _raiz.Children.Add(TarjetaHallazgo(h));

        // ---- Docker
        _raiz.Children.Add(new TextBlock
        {
            Text = "Docker", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 8),
        });
        if (contenedores is null)
        {
            _raiz.Children.Add(Nota(s.Docker.Instalado
                ? "Docker no responde: no puedo listar contenedores ni volúmenes."
                : "Docker no está instalado."));
            return;
        }

        _raiz.Children.Add(Subtitulo("Contenedores"));
        if (contenedores.Count == 0) _raiz.Children.Add(Nota("No hay contenedores."));
        var pgConfig = s.Ctx.Tiene(Componente.Postgres) ? s.Ctx.Config.Postgres!.ContenedorEf : null;
        foreach (var c in contenedores)
            _raiz.Children.Add(FilaContenedor(s, c, esDelProyecto: c.Nombre == pgConfig));

        _raiz.Children.Add(Subtitulo("Volúmenes"));
        if (volumenes is null || volumenes.Count == 0) _raiz.Children.Add(Nota("No hay volúmenes."));
        else
        {
            var pgVol = s.Ctx.Tiene(Componente.Postgres) ? s.Ctx.Config.Postgres!.VolumenEf : "";
            foreach (var v in volumenes)
                _raiz.Children.Add(FilaVolumen(s, v, esDelProyecto: pgVol.Length > 0 && v.Nombre == pgVol));
        }
    }

    // ------------------------------------------------------------ Piezas

    private Border TarjetaHallazgo(Hallazgo h)
    {
        var icono = new TextBlock
        {
            Text = GlifoDe(h.Gravedad), FontFamily = new FontFamily(FuenteIconos), FontSize = 16,
            Foreground = ColorDe(h.Gravedad), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0),
        };
        var texto = new StackPanel();
        texto.Children.Add(new TextBlock
        {
            Text = h.Titulo, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        });
        if (h.Detalle.Length > 0)
            texto.Children.Add(new TextBlock
            {
                Text = h.Detalle, Foreground = P("MutedBrush"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
            });
        var area = new TextBlock
        {
            Text = h.Area.ToUpperInvariant(), FontSize = 10, Foreground = P("MutedBrush"), Margin = new Thickness(0, 0, 0, 3),
        };
        texto.Children.Insert(0, area);

        // El botón de arreglo va debajo del texto: así nunca pisa el texto, sin importar el ancho
        if (h.Reparar is not null && h.AccionTitulo is not null)
        {
            var reparar = h.Reparar;
            var titulo = h.AccionTitulo;
            var b = Boton(titulo, async () =>
            {
                await _correr(titulo, reparar);
                await ActualizarAsync();
            }, primario: h.Gravedad is Gravedad.Error or Gravedad.Aviso);
            b.Margin = new Thickness(0, 10, 0, 0);
            b.HorizontalAlignment = HorizontalAlignment.Left;
            texto.Children.Add(b);
        }

        var fila = new Grid();
        fila.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fila.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fila.Children.Add(icono);
        Grid.SetColumn(texto, 1);
        fila.Children.Add(texto);

        return new Border
        {
            Style = Estilo("Card"), Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(14, 10, 14, 10), Child = fila,
        };
    }

    private Border FilaContenedor(Sesion s, ContenedorDocker c, bool esDelProyecto)
    {
        var punto = new System.Windows.Shapes.Ellipse
        {
            Width = 9, Height = 9, Fill = c.Corriendo ? P("OkBrush") : P("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 10, 0),
        };
        var info = new StackPanel();
        var nombre = new TextBlock { Text = c.Nombre + (esDelProyecto ? "   · de este proyecto" : ""), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        info.Children.Add(nombre);
        info.Children.Add(new TextBlock
        {
            Text = $"{c.Imagen}  ·  {c.Detalle}" + (c.Puertos.Length > 0 ? $"  ·  {c.Puertos}" : ""),
            Foreground = P("MutedBrush"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var botones = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var nom = c.Nombre;
        if (c.Corriendo)
        {
            botones.Children.Add(Boton("Detener", () => Accion($"Detener {nom}", x => x.Admin.Detener(nom))));
            botones.Children.Add(Boton("Reiniciar", () => Accion($"Reiniciar {nom}", x => x.Admin.Reiniciar(nom))));
        }
        else botones.Children.Add(Boton("Iniciar", () => Accion($"Iniciar {nom}", x => x.Admin.Iniciar(nom))));
        botones.Children.Add(Boton("Logs", () => Accion($"Logs de {nom}", x => x.Admin.Logs(nom), refrescar: false)));
        botones.Children.Add(Boton("Eliminar", async () =>
        {
            if (!Dialogos.Confirmar(_owner, $"¿Eliminar el contenedor «{nom}»?\n\nSi no usa un volumen con nombre, sus datos se pierden.", false)) return;
            await Accion($"Eliminar {nom}", x => x.Admin.EliminarContenedor(nom));
        }, peligro: true));

        // Dos filas: el nombre arriba y los botones debajo (se acomodan solos si falta ancho)
        var fila = new Grid();
        fila.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fila.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fila.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        fila.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        fila.Children.Add(punto);
        Grid.SetColumn(info, 1);
        fila.Children.Add(info);
        Grid.SetRow(botones, 1);
        Grid.SetColumn(botones, 1);
        fila.Children.Add(botones);
        return new Border { Style = Estilo("Card"), Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(14, 10, 12, 10), Child = fila };
    }

    private Border FilaVolumen(Sesion s, VolumenDocker v, bool esDelProyecto)
    {
        var nom = v.Nombre;
        var info = new TextBlock
        {
            Text = nom + (esDelProyecto ? "   · de este proyecto" : ""),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var b = Boton("Eliminar", async () =>
        {
            if (!Dialogos.Confirmar(_owner, $"¿Eliminar el volumen «{nom}»?\n\nSe pierden los datos que guarda. No se puede deshacer.", false)) return;
            await Accion($"Eliminar volumen {nom}", x => x.Admin.EliminarVolumen(nom));
        }, peligro: true);
        var fila = new DockPanel();
        b.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(b, Dock.Right);
        fila.Children.Add(b);
        fila.Children.Add(info);
        return new Border { Style = Estilo("Card"), Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(14, 8, 10, 8), Child = fila };
    }

    private async Task Accion(string titulo, Func<Sesion, bool> f, bool refrescar = true)
    {
        await _correr(titulo, f);
        if (refrescar) await ActualizarAsync();
    }

    private Button Boton(string texto, Func<Task> alClic, bool primario = false, bool peligro = false)
    {
        var b = new Button
        {
            Content = texto,
            Style = Estilo(peligro ? "BtnDanger" : primario ? "BtnPrimary" : "BtnChip"),
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 5, 12, 5),
        };
        b.Click += async (_, _) => await alClic();
        return b;
    }

    private Button Boton(string texto, Action alClic, bool primario = false) =>
        Boton(texto, () => { alClic(); return Task.CompletedTask; }, primario);

    private static TextBlock Subtitulo(string t) => new()
    {
        Text = t.ToUpperInvariant(), FontSize = 10, Foreground = P("MutedBrush"), Margin = new Thickness(2, 10, 0, 6),
    };

    private static TextBlock Nota(string t) => new() { Text = t, Foreground = P("MutedBrush"), Margin = new Thickness(2, 0, 0, 8) };
}
