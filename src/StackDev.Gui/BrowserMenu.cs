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

        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var e in found)
        {
            var item = new MenuItem { Header = $"{e.Label}     {e.Path}" + (e.Protected ? "   (pide acceso)" : "") };
            var url = baseUrl + e.Path;
            item.Click += (_, _) => Open(url);
            menu.Items.Add(item);
        }
        if (!found.Any(e => e.Kind == EndpointKind.Docs))
            menu.Items.Add(new MenuItem { Header = "Documentación: esta API no la publica en el servidor", IsEnabled = false });
        menu.IsOpen = true;
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Win32Exception) { }
    }
}
