using System;
using System.Windows;
using System.Windows.Media;

namespace StackDev.Gui;

/// <summary>El color de acento de la interfaz: verde terminal (predeterminado) o ámbar, como una terminal vieja.</summary>
public static class Apariencia
{
    public static bool EsAmbar(string? acento) => string.Equals(acento, "ambar", StringComparison.OrdinalIgnoreCase);

    /// <summary>Cambia los colores del acento en los recursos de la aplicación. Devuelve false si algún pincel no se pudo modificar.</summary>
    public static bool Aplicar(string? acento)
    {
        var ambar = EsAmbar(acento);
        var ok = true;
        ok &= Poner("AccentBrush", ambar ? "#F5B94A" : "#6EE787");
        ok &= Poner("AccentHoverBrush", ambar ? "#F8CB75" : "#8CF0A1");
        ok &= Poner("AccentSoftBrush", ambar ? "#2A2008" : "#12261A");
        ok &= Poner("AccentOnBrush", ambar ? "#261A02" : "#07210E");
        ok &= Degradado("AccentGradient", ambar ? "#F7C25E" : "#7AEC92", ambar ? "#E9A82E" : "#5BD878");
        return ok;
    }

    private static bool Poner(string clave, string color)
    {
        try
        {
            if (Application.Current.Resources[clave] is SolidColorBrush b && !b.IsFrozen)
            {
                b.Color = (Color)ColorConverter.ConvertFromString(color);
                return true;
            }
        }
        catch { }
        return false;
    }

    private static bool Degradado(string clave, string desde, string hasta)
    {
        try
        {
            if (Application.Current.Resources[clave] is LinearGradientBrush g && !g.IsFrozen && g.GradientStops.Count >= 2)
            {
                g.GradientStops[0].Color = (Color)ColorConverter.ConvertFromString(desde);
                g.GradientStops[1].Color = (Color)ColorConverter.ConvertFromString(hasta);
                return true;
            }
        }
        catch { }
        return false;
    }
}
