using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StackDev.Core.Deploy;

namespace StackDev.Gui;

/// <summary>Every address of the project in one place: the APIs on this computer and the one on the server, each with Open, Test and Copy.</summary>
public static class ApisWindow
{
    public static void Show(Window? owner, IReadOnlyList<ApiEntry> entries)
    {
        var w = ServerDialogs.Base(owner, "Direcciones y pruebas", 760);
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = "Direcciones de este proyecto", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(new TextBlock
        {
            Text = "«Probar» consulta la dirección ahora y dice qué responde y en cuánto tiempo. «Abrir» te deja elegir entre las páginas que existen.",
            FontSize = 11.5, Foreground = ServerDialogs.P("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });

        foreach (var scope in new[] { ApiScope.Local, ApiScope.Server })
        {
            var group = entries.Where(e => e.Scope == scope).ToList();
            root.Children.Add(new TextBlock
            {
                Text = scope == ApiScope.Local ? "EN ESTE EQUIPO (desarrollo)" : "EN EL SERVIDOR",
                FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ServerDialogs.P("AccentBrush"), Margin = new Thickness(0, 10, 0, 6),
            });
            if (group.Count == 0)
            {
                root.Children.Add(new TextBlock
                {
                    Text = scope == ApiScope.Local ? "Este proyecto no tiene API ni web local configuradas en dev-menu.json." : "Este proyecto todavía no sube a ningún servidor.",
                    FontSize = 12, Foreground = ServerDialogs.P("MutedBrush"), TextWrapping = TextWrapping.Wrap,
                });
                continue;
            }
            foreach (var e in group) root.Children.Add(Card(e));
        }

        var close = ServerDialogs.Button("Cerrar", true);
        close.Click += (_, _) => w.Close();
        root.Children.Add(ServerDialogs.ButtonRow(close));
        w.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        w.MaxHeight = SystemParameters.WorkArea.Height - 60;
        w.ShowDialog();
    }

    private static Border Card(ApiEntry e)
    {
        var stack = new StackPanel();
        var head = new WrapPanel();
        head.Children.Add(new TextBlock { Text = e.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 10, 0) });
        var statusText = e.Running switch { true => "● en marcha", false => "○ detenida", _ => "· sin datos" };
        var statusBrush = e.Running switch { true => ServerDialogs.P("OkBrush"), false => ServerDialogs.P("WarnBrush"), _ => ServerDialogs.P("MutedBrush") };
        head.Children.Add(new TextBlock { Text = statusText, FontSize = 12, Foreground = statusBrush });
        stack.Children.Add(head);
        stack.Children.Add(new TextBlock { Text = e.Detail, FontSize = 11.5, Foreground = ServerDialogs.P("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) });

        var result = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed,
        };
        if (string.IsNullOrEmpty(e.Url))
        {
            stack.Children.Add(new TextBlock { Text = "Sin dirección definida. Defínela en la configuración del proyecto (dev-menu.json) o al crear la API en el servidor.", FontSize = 12, Foreground = ServerDialogs.P("WarnBrush"), TextWrapping = TextWrapping.Wrap });
        }
        else
        {
            var url = e.Url!;
            stack.Children.Add(new TextBox { Text = url, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5 });
            var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var open = new Button { Content = "Abrir ▾", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
            open.Click += async (sender, _) => await BrowserMenu.ShowAsync((FrameworkElement)sender, url);
            var test = new Button { Content = "Probar", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
            test.Style = ServerDialogs.GetStyle("BtnPrimary");
            test.Click += async (_, _) =>
            {
                test.IsEnabled = false;
                result.Visibility = Visibility.Visible;
                result.Foreground = ServerDialogs.P("MutedBrush");
                result.Text = "Probando…";
                try
                {
                    var found = await ApiEndpoints.ProbeAsync(url);
                    result.Text = string.Join("\n", ApiCatalog.Describe(found));
                    result.Foreground = found.Count == 0 ? ServerDialogs.P("ErrBrush") : ServerDialogs.P("TextBrush");
                }
                finally { test.IsEnabled = true; }
            };
            var copy = new Button { Content = "Copiar dirección", Padding = new Thickness(14, 6, 14, 6) };
            copy.Click += (_, _) => { try { Clipboard.SetText(url); } catch (System.Runtime.InteropServices.COMException) { } };
            row.Children.Add(open);
            row.Children.Add(test);
            row.Children.Add(copy);
            stack.Children.Add(row);
        }
        stack.Children.Add(result);
        return new Border
        {
            Background = ServerDialogs.P("Surface2Brush"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 12),
            Margin = new Thickness(0, 0, 0, 8), Child = stack,
        };
    }
}
