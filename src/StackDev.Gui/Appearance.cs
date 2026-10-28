using System;
using System.Windows;
using System.Windows.Media;

namespace StackDev.Gui;

/// <summary>The accent colour of the interface: terminal green (default) or amber, like an old terminal.</summary>
public static class Appearance
{
    public static bool IsAmber(string? accent) => string.Equals(accent, "ambar", StringComparison.OrdinalIgnoreCase);

    /// <summary>Changes the accent colours in the application resources. Returns false if some brush could not be modified.</summary>
    public static bool Apply(string? accent)
    {
        var amber = IsAmber(accent);
        var ok = true;
        ok &= Put("AccentBrush", amber ? "#F5B94A" : "#6EE787");
        ok &= Put("AccentHoverBrush", amber ? "#F8CB75" : "#8CF0A1");
        ok &= Put("AccentSoftBrush", amber ? "#2A2008" : "#12261A");
        ok &= Put("AccentOnBrush", amber ? "#261A02" : "#07210E");
        ok &= Gradient("AccentGradient", amber ? "#F7C25E" : "#7AEC92", amber ? "#E9A82E" : "#5BD878");
        return ok;
    }

    private static bool Put(string key, string color)
    {
        try
        {
            if (Application.Current.Resources[key] is SolidColorBrush b && !b.IsFrozen)
            {
                b.Color = (Color)ColorConverter.ConvertFromString(color);
                return true;
            }
        }
        catch { }
        return false;
    }

    private static bool Gradient(string key, string since, string until)
    {
        try
        {
            if (Application.Current.Resources[key] is LinearGradientBrush g && !g.IsFrozen && g.GradientStops.Count >= 2)
            {
                g.GradientStops[0].Color = (Color)ColorConverter.ConvertFromString(since);
                g.GradientStops[1].Color = (Color)ColorConverter.ConvertFromString(until);
                return true;
            }
        }
        catch { }
        return false;
    }
}
