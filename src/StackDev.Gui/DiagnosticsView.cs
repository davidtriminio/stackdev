using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StackDev.Core;
using StackDev.Core.Config;
using StackDev.Core.Diagnostics;
using StackDev.Core.Docker;

namespace StackDev.Gui;

/// <summary>
/// "Diagnóstico": compares what is configured with reality (Docker, ports, folders, SDK) and offers to fix it,
/// and manages the Docker containers and volumes (start, stop, logs, delete).
/// </summary>
public sealed class DiagnosticsView : ScrollViewer
{
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    private readonly Window _owner;
    private readonly Func<Session?> _session;
    private readonly Func<string, Func<Session, bool>, Task<bool>> _run;
    private readonly StackPanel _root = new();
    private bool _loading;

    public DiagnosticsView(Window owner, Func<Session?> session, Func<string, Func<Session, bool>, Task<bool>> run)
    {
        _owner = owner;
        _session = session;
        _run = run;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Content = _root;
    }

    private static Brush P(string key) => (Brush)Application.Current.FindResource(key);
    private static Style GetStyle(string key) => (Style)Application.Current.FindResource(key);

    private static Brush ColorOf(Severity g) => g switch
    {
        Severity.Ok => P("OkBrush"),
        Severity.Warning => P("WarnBrush"),
        Severity.Error => P("ErrBrush"),
        _ => P("MutedBrush"),
    };

    private static string GlyphFor(Severity g) => g switch
    {
        Severity.Ok => "",       // check
        Severity.Warning => "",    // advertencia
        Severity.Error => "",    // error
        _ => "",                 // info
    };

    /// <summary>Called when the section is opened and after every fix.</summary>
    public async Task UpdateAsync()
    {
        var s = _session();
        if (s is null || _loading) return;
        _loading = true;
        _root.Children.Clear();
        _root.Children.Add(new TextBlock { Text = "Revisando…", Foreground = P("MutedBrush"), Margin = new Thickness(2, 8, 0, 0) });

        List<Finding> findings;
        List<DockerContainer>? containers = null;
        List<DockerVolume>? volumes = null;
        try
        {
            (findings, containers, volumes) = await Task.Run(() =>
            {
                var h = s.Checker.Review();
                var c = s.Docker.Installed ? s.Admin.Containers() : null;
                var v = c is null ? null : s.Admin.Volumes();
                return (h, c, v);
            });
        }
        catch (Exception ex)
        {
            findings = new() { new(Severity.Error, "General", "El diagnóstico falló", ex.Message) };
        }
        finally { _loading = false; }

        Build(s, findings, containers, volumes);
    }

    private void Build(Session s, List<Finding> findings, List<DockerContainer>? containers, List<DockerVolume>? volumes)
    {
        _root.Children.Clear();

        // ---- Cabecera
        var cab = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var other = Button("Revisar de nuevo", async () => await UpdateAsync(), primary: false);
        DockPanel.SetDock(other, Dock.Right);
        cab.Children.Add(other);
        cab.Children.Add(new TextBlock
        {
            Text = "Lo que encontré",
            FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        });
        _root.Children.Add(cab);

        // ---- Hallazgos (errores primero)
        foreach (var h in findings.OrderByDescending(x => x.Severity))
            _root.Children.Add(FindingCard(h));

        // ---- Docker
        _root.Children.Add(new TextBlock
        {
            Text = "Docker", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 8),
        });
        if (containers is null)
        {
            _root.Children.Add(Note(s.Docker.Installed
                ? "Docker no responde: no puedo listar contenedores ni volúmenes."
                : "Docker no está instalado."));
            return;
        }

        _root.Children.Add(Subtitle("Contenedores"));
        if (containers.Count == 0) _root.Children.Add(Note("No hay contenedores."));
        var pgConfig = s.Ctx.Has(ProjectComponent.Postgres) ? s.Ctx.Config.Postgres!.EfContainer : null;
        foreach (var c in containers)
            _root.Children.Add(ContainerRow(s, c, isOfProject: c.Name == pgConfig));

        _root.Children.Add(Subtitle("Volúmenes"));
        if (volumes is null || volumes.Count == 0) _root.Children.Add(Note("No hay volúmenes."));
        else
        {
            var pgVol = s.Ctx.Has(ProjectComponent.Postgres) ? s.Ctx.Config.Postgres!.EfVolume : "";
            foreach (var v in volumes)
                _root.Children.Add(VolumeRow(s, v, isOfProject: pgVol.Length > 0 && v.Name == pgVol));
        }
    }

    // ------------------------------------------------------------ Piezas

    private Border FindingCard(Finding h)
    {
        var icon = new TextBlock
        {
            Text = GlyphFor(h.Severity), FontFamily = new FontFamily(IconFont), FontSize = 16,
            Foreground = ColorOf(h.Severity), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0),
        };
        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = h.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        });
        if (h.Detail.Length > 0)
            text.Children.Add(new TextBlock
            {
                Text = h.Detail, Foreground = P("MutedBrush"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
            });
        var area = new TextBlock
        {
            Text = h.Area.ToUpperInvariant(), FontSize = 10, Foreground = P("MutedBrush"), Margin = new Thickness(0, 0, 0, 3),
        };
        text.Children.Insert(0, area);

        // The fix button goes below the text: that way it never overlaps the text, whatever the width
        if (h.Repair is not null && h.ActionTitle is not null)
        {
            var repair = h.Repair;
            var title = h.ActionTitle;
            var b = Button(title, async () =>
            {
                await _run(title, repair);
                await UpdateAsync();
            }, primary: h.Severity is Severity.Error or Severity.Warning);
            b.Margin = new Thickness(0, 10, 0, 0);
            b.HorizontalAlignment = HorizontalAlignment.Left;
            text.Children.Add(b);
        }

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(icon);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        return new Border
        {
            Style = GetStyle("Card"), Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(14, 10, 14, 10), Child = row,
        };
    }

    private Border ContainerRow(Session s, DockerContainer c, bool isOfProject)
    {
        var point = new System.Windows.Shapes.Ellipse
        {
            Width = 9, Height = 9, Fill = c.Running ? P("OkBrush") : P("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 10, 0),
        };
        var info = new StackPanel();
        var name = new TextBlock { Text = c.Name + (isOfProject ? "   · de este proyecto" : ""), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        info.Children.Add(name);
        info.Children.Add(new TextBlock
        {
            Text = $"{c.Image}  ·  {c.Detail}" + (c.Ports.Length > 0 ? $"  ·  {c.Ports}" : ""),
            Foreground = P("MutedBrush"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var cname = c.Name;
        if (c.Running)
        {
            buttons.Children.Add(Button("Detener", () => Operation($"Detener {cname}", x => x.Admin.Stop(cname))));
            buttons.Children.Add(Button("Reiniciar", () => Operation($"Reiniciar {cname}", x => x.Admin.Restart(cname))));
        }
        else buttons.Children.Add(Button("Iniciar", () => Operation($"Iniciar {cname}", x => x.Admin.Start(cname))));
        buttons.Children.Add(Button("Logs", () => Operation($"Logs de {cname}", x => x.Admin.Logs(cname), refresh: false)));
        buttons.Children.Add(Button("Eliminar", async () =>
        {
            if (!Dialogs.Confirm(_owner, $"¿Eliminar el contenedor «{cname}»?\n\nSi no usa un volumen con nombre, sus datos se pierden.", false)) return;
            await Operation($"Eliminar {cname}", x => x.Admin.DeleteContainer(cname));
        }, danger: true));

        // Two rows: the name on top and the buttons below (they rearrange themselves if width is short)
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.Children.Add(point);
        Grid.SetColumn(info, 1);
        row.Children.Add(info);
        Grid.SetRow(buttons, 1);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(buttons);
        return new Border { Style = GetStyle("Card"), Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(14, 10, 12, 10), Child = row };
    }

    private Border VolumeRow(Session s, DockerVolume v, bool isOfProject)
    {
        var cname = v.Name;
        var info = new TextBlock
        {
            Text = cname + (isOfProject ? "   · de este proyecto" : ""),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var b = Button("Eliminar", async () =>
        {
            if (!Dialogs.Confirm(_owner, $"¿Eliminar el volumen «{cname}»?\n\nSe pierden los datos que guarda. No se puede deshacer.", false)) return;
            await Operation($"Eliminar volumen {cname}", x => x.Admin.DeleteVolume(cname));
        }, danger: true);
        var row = new DockPanel();
        b.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(b, Dock.Right);
        row.Children.Add(b);
        row.Children.Add(info);
        return new Border { Style = GetStyle("Card"), Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(14, 8, 10, 8), Child = row };
    }

    private async Task Operation(string title, Func<Session, bool> f, bool refresh = true)
    {
        await _run(title, f);
        if (refresh) await UpdateAsync();
    }

    private Button Button(string text, Func<Task> toClick, bool primary = false, bool danger = false)
    {
        var b = new Button
        {
            Content = text,
            Style = GetStyle(danger ? "BtnDanger" : primary ? "BtnPrimary" : "BtnChip"),
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 5, 12, 5),
        };
        b.Click += async (_, _) => await toClick();
        return b;
    }

    private Button Button(string text, Action toClick, bool primary = false) =>
        Button(text, () => { toClick(); return Task.CompletedTask; }, primary);

    private static TextBlock Subtitle(string t) => new()
    {
        Text = t.ToUpperInvariant(), FontSize = 10, Foreground = P("MutedBrush"), Margin = new Thickness(2, 10, 0, 6),
    };

    private static TextBlock Note(string t) => new() { Text = t, Foreground = P("MutedBrush"), Margin = new Thickness(2, 0, 0, 8) };
}
