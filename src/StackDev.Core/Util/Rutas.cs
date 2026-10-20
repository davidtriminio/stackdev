using System.Text.RegularExpressions;

namespace StackDev.Core.Util;

/// <summary>Rutas relativas a la raíz del proyecto (siempre se muestran con "/").</summary>
public static class Rutas
{
    public static StringComparison Comparacion =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Ruta completa sin separador final.</summary>
    public static string Normalizar(string ruta)
    {
        var completa = Path.GetFullPath(ruta);
        var recortada = completa.TrimEnd('\\', '/');
        return recortada.Length == 0 ? completa : recortada;
    }

    /// <summary>"." si es la misma carpeta, la ruta relativa con "/" si está dentro de la raíz, o la ruta completa si está fuera.</summary>
    public static string Relativa(string raiz, string ruta)
    {
        var r = Normalizar(raiz);
        var f = Normalizar(ruta);
        if (string.Equals(f, r, Comparacion)) return ".";
        if (f.StartsWith(r + Path.DirectorySeparatorChar, Comparacion) || f.StartsWith(r + "/", Comparacion))
            return f[r.Length..].TrimStart('\\', '/').Replace('\\', '/');
        return f;
    }

    /// <summary>Convierte una ruta del archivo de configuración en una ruta completa.</summary>
    public static string Resolver(string raiz, string? relativa)
    {
        if (string.IsNullOrWhiteSpace(relativa) || relativa == ".") return raiz;
        if (Path.IsPathRooted(relativa)) return relativa;
        var normal = relativa.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(raiz, normal));
    }

    /// <summary>¿<paramref name="hijo"/> es la misma carpeta que <paramref name="padre"/> o está dentro de ella?</summary>
    public static bool EsIgualOSubcarpeta(string padre, string hijo)
    {
        var p = Normalizar(padre);
        var h = Normalizar(hijo);
        if (string.Equals(h, p, Comparacion)) return true;
        return h.StartsWith(p + Path.DirectorySeparatorChar, Comparacion) || h.StartsWith(p + "/", Comparacion);
    }

    /// <summary>Nombre de la carpeta (último segmento), tolerando separadores finales y raíces de unidad.</summary>
    public static string NombreCarpeta(string ruta)
    {
        var n = Normalizar(ruta);
        var nombre = Path.GetFileName(n);
        return string.IsNullOrEmpty(nombre) ? "raiz" : nombre;
    }

    /// <summary>"Mi Proyecto" -> "mi-proyecto" (igual que el nombre generado por la detección).</summary>
    public static string Slug(string nombre) =>
        Regex.Replace(nombre.ToLowerInvariant(), @"[^\w\-]", "-");
}
