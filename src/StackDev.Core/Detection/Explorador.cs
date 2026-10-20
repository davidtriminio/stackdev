namespace StackDev.Core.Detection;

/// <summary>Navegación de carpetas para elegir el proyecto desde la consola.</summary>
public static class Explorador
{
    public static List<string> Unidades()
    {
        try { return DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName).ToList(); }
        catch (IOException) { return new(); }
    }

    public static List<string> Subcarpetas(string dir)
    {
        try
        {
            return Directory.GetDirectories(dir).Select(d => Path.GetFileName(d)!)
                .Where(n => !n.StartsWith('.')).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(); }
    }

    /// <summary>Carpetas con .git o dev-menu.json hasta 3 niveles por debajo (máximo 30).</summary>
    public static List<string> BuscarProyectos(string desde, int profundidad = 3)
    {
        var res = new List<string>();
        var cola = new Queue<(string Dir, int N)>();
        cola.Enqueue((desde, 0));
        while (cola.Count > 0 && res.Count < 30)
        {
            var (dir, n) = cola.Dequeue();
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, Config.ConfigStore.NombreArchivo)))
            {
                res.Add(dir);
                continue;   // no buscar proyectos dentro de un proyecto
            }
            if (n >= profundidad) continue;
            try
            {
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    var nom = Path.GetFileName(sub);
                    if (!nom.StartsWith('.') && !nom.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) cola.Enqueue((sub, n + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return res.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
