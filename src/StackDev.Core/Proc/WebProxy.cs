using System.Text.RegularExpressions;
using StackDev.Core.Detection;

namespace StackDev.Core.Proc;

/// <summary>
/// The dev server of a web app (ng serve, vite...) forwards "/api" to the backend through a proxy file such as
/// <c>proxy.conf.json</c>. If the API runs on another port than the one written there, every call fails with ECONNREFUSED.
/// </summary>
public static class WebProxy
{
    /// <summary>A proxy entry for "/api..." and the port its target points to.</summary>
    private static readonly Regex ApiTarget = new(
        @"(""/api[^""]*""\s*:\s*\{[^{}]*?""target""\s*:\s*""https?://(?:localhost|127\.0\.0\.1):)(\d+)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public sealed record Stale(string File, int Port);

    /// <summary>Proxy files in the web folder whose "/api" target is a local port other than <paramref name="apiPort"/>.</summary>
    public static List<Stale> Find(string webFolder, int apiPort)
    {
        var res = new List<Stale>();
        if (apiPort <= 0 || !Directory.Exists(webFolder)) return res;
        foreach (var file in FileTree.Search(webFolder, new[] { "proxy.conf*.json" }, 3))
        {
            var m = ApiTarget.Match(FileTree.Read(file));
            if (m.Success && int.Parse(m.Groups[2].Value) != apiPort) res.Add(new Stale(file, int.Parse(m.Groups[2].Value)));
        }
        return res;
    }

    public static string Rewrite(string text, int apiPort) =>
        ApiTarget.Replace(text, m => m.Groups[1].Value + apiPort);

    /// <summary>Points the file's "/api" target to <paramref name="apiPort"/>, keeping the rest of the file untouched.</summary>
    public static void Update(string file, int apiPort)
    {
        var text = File.ReadAllText(file);
        File.WriteAllText(file, Rewrite(text, apiPort), new System.Text.UTF8Encoding(false));
    }
}
