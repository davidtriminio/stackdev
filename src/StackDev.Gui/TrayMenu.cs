using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace StackDev.Gui;

/// <summary>
/// The menu of the tray icon, drawn with the app's own theme (the Windows one ignores it). It opens next to the pointer,
/// above it when the icon is near the bottom of the screen, and closes when anything else is clicked or Esc is pressed.
/// </summary>
internal sealed class TrayMenu : Window
{
    private TrayMenu(IReadOnlyList<(string Text, Action? Run)> items)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        SetResourceReference(ForegroundProperty, "TextBrush");
        SetResourceReference(FontFamilyProperty, "UiFont");
        FontSize = 13;

        var rows = new StackPanel { MinWidth = 190 };
        foreach (var (text, run) in items)
        {
            if (run is null)
            {
                rows.Children.Add(new Border { Height = 1, Margin = new Thickness(6, 4, 6, 4), Background = ServerDialogs.P("LineBrush") });
                continue;
            }
            var row = new Button { Content = text, Style = ServerDialogs.GetStyle("BtnMenuItem") };
            row.Click += (_, _) => { Close(); run(); };
            rows.Children.Add(row);
        }
        Content = new Border
        {
            Background = ServerDialogs.P("Surface2Brush"), BorderBrush = ServerDialogs.P("LineBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(6), Margin = new Thickness(10),
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.45 }, Child = rows,
        };
        Deactivated += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>Opens the menu at <paramref name="screenX"/>, <paramref name="screenY"/> (device pixels, as the tray icon reports them).</summary>
    public static void Open(int screenX, int screenY, IReadOnlyList<(string Text, Action? Run)> items)
    {
        var menu = new TrayMenu(items);
        var dpi = VisualTreeHelper.GetDpi(Application.Current.MainWindow ?? menu);
        var x = screenX / dpi.DpiScaleX;
        var y = screenY / dpi.DpiScaleY;
        menu.Left = x;
        menu.Top = y;
        menu.Loaded += (_, _) =>
        {
            // Keep it on the screen: above the pointer when it would run off the bottom, left of it at the right edge.
            var area = SystemParameters.WorkArea;
            var w = menu.ActualWidth; var h = menu.ActualHeight;
            menu.Left = Math.Max(area.Left, Math.Min(x, area.Right - w));
            menu.Top = y + h > area.Bottom ? Math.Max(area.Top, y - h) : y;
            menu.Activate();
            SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(menu).Handle);   // from the tray the app is in the background: without this the menu never loses focus, so it would never close
        };
        menu.Show();
    }
}
