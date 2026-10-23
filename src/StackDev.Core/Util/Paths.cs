using System.Text.RegularExpressions;

namespace StackDev.Core.Util;

/// <summary>Rutas relativas a la raíz del proyecto (siempre se muestran con "/").</summary>
public static class Paths
{
    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Ruta completa sin separador final.</summary>
    public static string Normalize(string path)
    {
        var complete = Path.GetFullPath(path);
        var trimmed = complete.TrimEnd('\\', '/');
        return trimmed.Length == 0 ? complete : trimmed;
    }

    /// <summary>"." si es la misma carpeta, la ruta relativa con "/" si está dentro de la raíz, o la ruta completa si está fuera.</summary>
    public static string Relative(string root, string path)
    {
        var r = Normalize(root);
        var f = Normalize(path);
        if (string.Equals(f, r, Comparison)) return ".";
        if (f.StartsWith(r + Path.DirectorySeparatorChar, Comparison) || f.StartsWith(r + "/", Comparison))
            return f[r.Length..].TrimStart('\\', '/').Replace('\\', '/');
        return f;
    }

    /// <summary>Convierte una ruta del archivo de configuración en una ruta completa.</summary>
    public static string Resolve(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative == ".") return root;
        if (Path.IsPathRooted(relative)) return relative;
        var normal = relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(root, normal));
    }

    /// <summary>¿<paramref name="child"/> es la misma carpeta que <paramref name="parent"/> o está dentro de ella?</summary>
    public static bool IsSameOrSubfolder(string parent, string child)
    {
        var p = Normalize(parent);
        var h = Normalize(child);
        if (string.Equals(h, p, Comparison)) return true;
        return h.StartsWith(p + Path.DirectorySeparatorChar, Comparison) || h.StartsWith(p + "/", Comparison);
    }

    /// <summary>Nombre de la carpeta (último segmento), tolerando separadores finales y raíces de unidad.</summary>
    public static string FolderName(string path)
    {
        var n = Normalize(path);
        var name = Path.GetFileName(n);
        return string.IsNullOrEmpty(name) ? "raiz" : name;
    }

    /// <summary>"Mi Proyecto" -> "mi-proyecto" (igual que el nombre generado por la detección).</summary>
    public static string Slug(string name) =>
        Regex.Replace(name.ToLowerInvariant(), @"[^\w\-]", "-");
}
