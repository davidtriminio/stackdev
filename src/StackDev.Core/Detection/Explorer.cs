namespace StackDev.Core.Detection;

/// <summary>Folder browsing to choose the project from the console.</summary>
public static class Explorer
{
    public static List<string> Units()
    {
        try { return DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName).ToList(); }
        catch (IOException) { return new(); }
    }

    public static List<string> Subfolders(string dir)
    {
        try
        {
            return Directory.GetDirectories(dir).Select(d => Path.GetFileName(d)!)
                .Where(n => !n.StartsWith('.')).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(); }
    }

    /// <summary>Folders with .git or dev-menu.json up to 3 levels below (at most 30).</summary>
    public static List<string> SearchProjects(string since, int depth = 3)
    {
        var res = new List<string>();
        var queue = new Queue<(string Dir, int N)>();
        queue.Enqueue((since, 0));
        while (queue.Count > 0 && res.Count < 30)
        {
            var (dir, n) = queue.Dequeue();
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, Config.ConfigStore.FileName)))
            {
                res.Add(dir);
                continue;   // do not search for projects inside a project
            }
            if (n >= depth) continue;
            try
            {
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    var cname = Path.GetFileName(sub);
                    if (!cname.StartsWith('.') && !cname.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) queue.Enqueue((sub, n + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return res.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
