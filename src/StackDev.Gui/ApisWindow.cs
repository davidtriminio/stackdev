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
    /// <param name="load">Builds the list again (it is called after every save).</param>
    /// <param name="save">Saves the address (and, if asked, the folder and start command) of a local app. Returns an error text, or null when saved.</param>
    /// <param name="setupServer">Opens the assistant that picks the server and the API (the window closes first).</param>
    public static void Show(Window? owner, Func<IReadOnlyList<ApiEntry>> load, Func<ApiEntry, string, string?, string?, string?> save, Action setupServer,
        Func<Diagnosis?>? diagnose = null, Func<DiagnosisIssue, string>? repair = null)
    {
        var w = ServerDialogs.Base(owner, "Direcciones y pruebas", 760);
        var root = new StackPanel { Margin = new Thickness(22) };

        string? notice = null;
        void Build()
        {
            root.Children.Clear();
            var entries = load();
            root.Children.Add(new TextBlock { Text = "Direcciones de este proyecto", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            root.Children.Add(new TextBlock
            {
                Text = "«Probar» consulta la dirección ahora y dice qué responde y en cuánto tiempo. «Abrir» te deja elegir entre las páginas que existen. Lo que falte se completa aquí mismo.",
                FontSize = 11.5, Foreground = ServerDialogs.P("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            });
            if (notice is not null)
                root.Children.Add(new TextBlock { Text = notice, FontSize = 12.5, Foreground = ServerDialogs.P("OkBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
            foreach (var scope in new[] { ApiScope.Local, ApiScope.Server })
            {
                root.Children.Add(new TextBlock
                {
                    Text = scope == ApiScope.Local ? "EN ESTE EQUIPO (desarrollo)" : "EN EL SERVIDOR",
                    FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ServerDialogs.P("AccentBrush"), Margin = new Thickness(0, 10, 0, 6),
                });
                foreach (var e in entries.Where(x => x.Scope == scope))
                    root.Children.Add(e.Configured ? Card(e, e.Scope == ApiScope.Server ? diagnose : null, repair) : FormCard(e, w, save, Build, setupServer, text => notice = text));
            }
            var close = ServerDialogs.Button("Cerrar", true);
            close.IsDefault = false;   // Enter inside a form must not close the window
            close.Click += (_, _) => w.Close();
            root.Children.Add(ServerDialogs.ButtonRow(close));
        }

        Build();
        w.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        w.MaxHeight = SystemParameters.WorkArea.Height - 60;
        w.ShowDialog();
    }

    /// <summary>What is not configured yet, as a small form (or a button for the server) instead of a file to edit.</summary>
    private static Border FormCard(ApiEntry e, Window w, Func<ApiEntry, string, string?, string?, string?> save, Action rebuild, Action setupServer, Action<string> saved)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = e.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = "○ sin configurar", FontSize = 12, Foreground = ServerDialogs.P("WarnBrush"), Margin = new Thickness(0, 2, 0, 0) });
        stack.Children.Add(new TextBlock { Text = e.Detail, FontSize = 11.5, Foreground = ServerDialogs.P("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) });

        if (e.Scope == ApiScope.Server)
        {
            var go = new Button { Content = "Elegir servidor y API (asistente)", HorizontalAlignment = HorizontalAlignment.Left };
            go.Style = ServerDialogs.GetStyle("BtnPrimary");
            go.Click += (_, _) => { w.Close(); setupServer(); };
            stack.Children.Add(go);
        }
        else
        {
            var url = ServerDialogs.Field(stack, "Dirección con la que se abre (URL)", e.Suggestion ?? "", e.Suggestion is null ? "Por ejemplo http://localhost:5000" : "Sugerida a partir de tu proyecto. Cámbiala si es otra.", 200);
            TextBox? folder = null, start = null;
            if (e.NeedsFolder)
            {
                folder = ServerDialogs.Field(stack, "Carpeta donde está (relativa al proyecto)", "", "Por ejemplo backend", 200);
                start = ServerDialogs.Field(stack, "Comando para iniciarla", "", "Por ejemplo dotnet run --project src/Api", 300);
            }
            var error = ServerDialogs.Error();
            stack.Children.Add(error);
            var ok = new Button { Content = "Guardar", HorizontalAlignment = HorizontalAlignment.Left };
            ok.Style = ServerDialogs.GetStyle("BtnPrimary");
            ok.Click += (_, _) =>
            {
                string? problem;
                try { problem = save(e, url.Text, folder?.Text, start?.Text); }
                catch (Exception ex) { problem = "No se pudo guardar: " + ex.Message; }
                if (problem is not null) { error.Text = problem; error.Visibility = Visibility.Visible; return; }
                saved($"✔ Guardé «{e.Name}»: {url.Text.Trim().TrimEnd('/')}");
                rebuild();
            };
            url.KeyDown += (_, key) => { if (key.Key == System.Windows.Input.Key.Enter) ok.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); };
            stack.Children.Add(ok);
        }
        return new Border
        {
            Background = ServerDialogs.P("Surface2Brush"), BorderBrush = ServerDialogs.P("WarnBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(0, 0, 0, 8), Child = stack,
        };
    }

    private static Border Card(ApiEntry e, Func<Diagnosis?>? diagnose, Func<DiagnosisIssue, string>? repair)
    {
        var stack = new StackPanel();
        var head = new WrapPanel();
        head.Children.Add(new TextBlock { Text = e.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 10, 0) });
        var statusText = e.Running switch { true => "● en marcha", false => "○ detenida", _ => "· sin datos" };
        var statusBrush = e.Running switch { true => ServerDialogs.P("OkBrush"), false => ServerDialogs.P("WarnBrush"), _ => ServerDialogs.P("MutedBrush") };
        head.Children.Add(new TextBlock { Text = statusText, FontSize = 12, Foreground = statusBrush });
        stack.Children.Add(head);
        stack.Children.Add(new TextBlock { Text = e.Detail, FontSize = 11.5, Foreground = ServerDialogs.P("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) });

        var fixes = new StackPanel();
        var result = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed,
        };
        if (string.IsNullOrEmpty(e.Url))
        {
            stack.Children.Add(new TextBlock { Text = "Sin dirección todavía.", FontSize = 12, Foreground = ServerDialogs.P("WarnBrush") });
        }
        else
        {
            var url = e.Url!;
            stack.Children.Add(new TextBox { Text = url, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5 });
            var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var open = new Button { Content = "Abrir ▾", Style = ServerDialogs.GetStyle("BtnChip") };
            open.Click += async (sender, _) => await BrowserMenu.ShowAsync((FrameworkElement)sender, url);
            var test = new Button { Content = "Probar", Style = ServerDialogs.GetStyle("BtnPrimarySm") };

            test.Click += async (_, _) =>
            {
                test.IsEnabled = false;
                result.Visibility = Visibility.Visible;
                result.Foreground = ServerDialogs.P("MutedBrush");
                result.Text = "Probando…";
                try
                {
                    fixes.Children.Clear();
                    if (diagnose is not null)
                    {
                        // The server's address is checked from the outside step by step, and what is wrong comes with its repair button.
                        var d = await Task.Run(diagnose);
                        if (d is not null)
                        {
                            result.Text = string.Join("\n", d.Facts);
                            result.Foreground = d.Healthy ? ServerDialogs.P("TextBrush") : ServerDialogs.P("WarnBrush");
                            foreach (var issue in d.Issues)
                            {
                                fixes.Children.Add(new TextBlock { Text = "⚠ " + issue.Title, FontWeight = FontWeights.SemiBold, FontSize = 12.5, Margin = new Thickness(0, 10, 0, 2), Foreground = ServerDialogs.P("WarnBrush") });
                                fixes.Children.Add(new TextBlock { Text = issue.Detail, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = ServerDialogs.P("MutedBrush"), Margin = new Thickness(0, 0, 0, 6) });
                                if (!issue.Fixable || repair is null) continue;
                                var fix = new Button { Content = issue.FixLabel, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) };
                                fix.Style = ServerDialogs.GetStyle("BtnPrimary");
                                var captured = issue;
                                fix.Click += (_, _) =>
                                {
                                    fix.IsEnabled = false;
                                    var message = repair(captured);
                                    result.Text = message + "\nPulsa «Probar» para comprobar otra vez.";
                                    result.Foreground = ServerDialogs.P("TextBrush");
                                    fixes.Children.Clear();
                                };
                                fixes.Children.Add(fix);
                            }
                            return;
                        }
                    }
                    var found = await ApiEndpoints.ProbeAsync(url);
                    result.Text = string.Join("\n", ApiCatalog.Describe(found));
                    result.Foreground = found.Count == 0 ? ServerDialogs.P("ErrBrush") : ServerDialogs.P("TextBrush");
                }
                finally { test.IsEnabled = true; }
            };
            var copy = new Button { Content = "Copiar dirección", Style = ServerDialogs.GetStyle("BtnChip") };
            copy.Click += (_, _) => { try { Clipboard.SetText(url); } catch (System.Runtime.InteropServices.COMException) { } };
            row.Children.Add(open);
            row.Children.Add(test);
            row.Children.Add(copy);
            stack.Children.Add(row);
        }
        stack.Children.Add(result);
        stack.Children.Add(fixes);
        return new Border
        {
            Background = ServerDialogs.P("Surface2Brush"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 12),
            Margin = new Thickness(0, 0, 0, 8), Child = stack,
        };
    }
}
