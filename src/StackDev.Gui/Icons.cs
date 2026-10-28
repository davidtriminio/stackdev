using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dibujo = System.Windows.Shapes.Path;

namespace StackDev.Gui;

/// <summary>
/// The application's iconography: each component has its own vector drawing and colour (recognisable at a glance),
/// and the action buttons carry a small Segoe Fluent Icons glyph.
/// </summary>
public static class Icons
{
    public const string Font = "Segoe Fluent Icons, Segoe MDL2 Assets";

    // Thin-stroke drawings on a 24×24 grid
    private static readonly Dictionary<string, (string Data, Color Color)> Components = new()
    {
        // Database: cylinder
        ["POSTGRES"] = ("M12,3 C7.5,3 4,4.3 4,6 V18 C4,19.7 7.5,21 12,21 C16.5,21 20,19.7 20,18 V6 C20,4.3 16.5,3 12,3 Z M4,6 C4,7.7 7.5,9 12,9 C16.5,9 20,7.7 20,6 M4,12 C4,13.7 7.5,15 12,15 C16.5,15 20,13.7 20,12",
                        Color.FromRgb(0x6F, 0xA8, 0xFF)),
        // API: code braces  < / >
        ["API"] = ("M8,7 L3,12 L8,17 M16,7 L21,12 L16,17 M14,5 L10,19",
                   Color.FromRgb(0xB7, 0x9C, 0xFF)),
        // Web: globo
        ["WEB"] = ("M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z M3,12 H21 M12,3 C8.5,6.5 8.5,17.5 12,21 M12,3 C15.5,6.5 15.5,17.5 12,21",
                   Color.FromRgb(0x5F, 0xD6, 0xA8)),
        // Emulator: phone
        ["EMULADOR"] = ("M7.5,2.5 H16.5 A2,2 0 0 1 18.5,4.5 V19.5 A2,2 0 0 1 16.5,21.5 H7.5 A2,2 0 0 1 5.5,19.5 V4.5 A2,2 0 0 1 7.5,2.5 Z M10.5,18.5 H13.5",
                        Color.FromRgb(0xF5, 0xB9, 0x4A)),
        // Flutter: dos cintas inclinadas
        ["FLUTTER"] = ("M14,3 L5,12 L8,15 L20,3 Z M14,12 L9.5,16.5 L14,21 H20 L14.8,15.8 L17.5,13.1 L14,12 Z",
                       Color.FromRgb(0x22, 0xD3, 0xEE)),
        // Credenciales: llave
        ["CREDENCIALES"] = ("M15.5,3.5 A5,5 0 1 1 15.5,13.5 A5,5 0 1 1 15.5,3.5 Z M12,12.5 L3.5,21 M6.5,18 L9,20.5 M9,15 L11,17",
                            Color.FromRgb(0xF4, 0x72, 0xB6)),
    };

    public static Color ColorOf(string component) =>
        Components.TryGetValue(component, out var c) ? c.Color : Color.FromRgb(0x8B, 0x93, 0xA5);

    /// <summary>A small tile with the component's drawing, tinted with its colour.</summary>
    public static FrameworkElement Badge(string component, double size = 34)
    {
        if (!Components.TryGetValue(component, out var c)) c = ("M4,12 H20", Color.FromRgb(0x8B, 0x93, 0xA5));
        var stroke = new SolidColorBrush(c.Color);
        var drawing = new Dibujo
        {
            Data = Geometry.Parse(c.Data),
            Stroke = stroke,
            StrokeThickness = 1.7,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 24,
            Height = 24,
        };
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size > 40 ? 12 : 7),
            Background = new SolidColorBrush(Color.FromArgb(0x26, c.Color.R, c.Color.G, c.Color.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, c.Color.R, c.Color.G, c.Color.B)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Viewbox { Width = size * 0.56, Height = size * 0.56, Child = drawing },
        };
    }

    /// <summary>Small glyph for an action button by its name (start, stop, logs...); null if there is no clear one.</summary>
    public static string? ActionGlyph(string name) => name switch
    {
        "start" or "run" or "up" => "",
        "stop" or "down" => "",
        "kill" => "",
        "restart" or "recreate" or "cold" or "switch" => "",
        "open" => "",
        "logs" => "",
        "logs-tab" => "",
        "test" or "check" => "",
        "psql" => "",
        "install" => "",
        "backup" => "",
        "restore" => "",
        "devices" => "",
        "build" or "gen" or "pub-get" or "clean" => "",
        _ => null,
    };

    /// <summary>Content of a button: glyph (optional) + text.</summary>
    public static object WithGlyph(string? glyph, string text)
    {
        if (glyph is null) return text;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily(Font),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
        });
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }
}
