using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using StackDev.Core.Deploy;

namespace StackDev.Gui;

/// <summary>"Open in the browser" for a deployed API: looks at what the API really answers (documentation, health, home page) and offers exactly that.</summary>
internal static class BrowserMenu
{
    public static async Task ShowAsync(FrameworkElement anchor, string baseUrl)
    {
        baseUrl = baseUrl.TrimEnd('/');
        var found = await ApiEndpoints.ProbeAsync(baseUrl);
        if (found.Count == 0)
        {
            Dialogs.Confirm(Window.GetWindow(anchor), $"{baseUrl} no responde todavía.\n\nSi acabas de crear la API, el certificado HTTPS o el DNS pueden tardar unos minutos.", true);
            return;
        }
        if (found.Count == 1) { Open(baseUrl + found[0].Path); return; }

        // A menu of our own (the Windows one ignores the theme): a popup with one flat row per page that answers.
        var rows = new StackPanel { MinWidth = 280 };
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, StaysOpen = false,
            AllowsTransparency = true, VerticalOffset = 4,
            Child = new Border
            {
                Background = ServerDialogs.P("Surface2Brush"), BorderBrush = ServerDialogs.P("LineBrush"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Padding = new Thickness(6), Margin = new Thickness(0, 0, 12, 12), Child = rows,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.45 },
            },
        };
        foreach (var e in found)
        {
            var url = baseUrl + e.Path;
            var row = new Button
            {
                Content = $"{e.Label}     {e.Path}" + (e.Protected ? "   (pide acceso)" : ""), Style = ServerDialogs.GetStyle("BtnMenuItem"),
            };
            row.Click += (_, _) => { popup.IsOpen = false; Open(url); };
            rows.Children.Add(row);
        }
        if (!found.Any(e => e.Kind == EndpointKind.Docs))
            rows.Children.Add(new Button { Content = "Documentación: esta API no la publica en el servidor", Style = ServerDialogs.GetStyle("BtnMenuItem"), IsEnabled = false });
        popup.IsOpen = true;
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Win32Exception) { }
    }
}
