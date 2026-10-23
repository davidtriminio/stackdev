using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace StackDev.Core.Deploy;

/// <summary>A web project found in a folder that can be deployed as an API.</summary>
public sealed record ApiCandidate(
    string ProjectPath,
    string ProjectName,
    string AssemblyName,
    string Dll,
    string? TargetFramework,
    string SuggestedService,
    int Score,
    string Reason)
{
    /// <summary>Major version of the runtime the server needs (net9.0 gives 9), or null when unknown.</summary>
    public int? RuntimeMajor => ApiDetector.RuntimeMajor(TargetFramework);
}

/// <summary>Looks for the main API of a solution: the ASP.NET Core project that is meant to be hosted, not a library or a test project.</summary>
public static class ApiDetector
{
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", "packages", "publish", "dist", "build", ".dart_tool", "android", "ios", "$RECYCLE.BIN",
    };

    /// <summary>All deployable web projects under <paramref name="root"/> (or the project itself when it is a .csproj), best match first.</summary>
    public static List<ApiCandidate> Find(string root, int maxDepth = 6)
    {
        var found = new List<ApiCandidate>();
        if (File.Exists(root) && root.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            if (Analyze(root) is { } one) found.Add(one);
            return found;
        }
        if (!Directory.Exists(root)) return found;

        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.csproj"))
                    if (Analyze(f) is { } c) found.Add(c);
                if (depth >= maxDepth) continue;
                foreach (var d in Directory.EnumerateDirectories(dir))
                    if (!SkippedFolders.Contains(Path.GetFileName(d))) stack.Push((d, depth + 1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable folder: skip it */ }
        }
        return found.OrderByDescending(c => c.Score).ThenBy(c => c.ProjectPath.Length).ThenBy(c => c.ProjectPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Null when the project is not a deployable web application.</summary>
    public static ApiCandidate? Analyze(string csprojPath)
    {
        XDocument doc;
        try { doc = XDocument.Load(csprojPath); }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException) { return null; }

        var projectEl = doc.Root;
        if (projectEl is null || projectEl.Name.LocalName != "Project") return null;
        var sdk = (string?)projectEl.Attribute("Sdk") ?? "";
        var isWeb = sdk.StartsWith("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase);
        if (!isWeb) return null;

        string? Prop(string name) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
        var name = Path.GetFileNameWithoutExtension(csprojPath);
        if (Regex.IsMatch(name, @"(^|\.)(Tests?|UnitTests|IntegrationTests|Specs?)$", RegexOptions.IgnoreCase)) return null;
        if (string.Equals(Prop("IsTestProject"), "true", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(Prop("OutputType"), "Library", StringComparison.OrdinalIgnoreCase)) return null;

        var assembly = string.IsNullOrWhiteSpace(Prop("AssemblyName")) ? name : Prop("AssemblyName")!;
        var tfm = Prop("TargetFramework") ?? Prop("TargetFrameworks")?.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        var dir = Path.GetDirectoryName(csprojPath) ?? "";
        var score = 50;
        var reasons = new List<string> { "Web SDK" };
        if (Regex.IsMatch(name, @"\.(Api|Web|Server|Backend|Host)$", RegexOptions.IgnoreCase)) { score += 20; reasons.Add("name ends in .Api/.Web"); }
        if (File.Exists(Path.Combine(dir, "Program.cs")) && File.ReadAllText(Path.Combine(dir, "Program.cs")).Contains("WebApplication")) { score += 10; reasons.Add("Program.cs"); }
        if (Directory.Exists(Path.Combine(dir, "Controllers"))) { score += 5; reasons.Add("Controllers"); }
        if (doc.Descendants().Any(e => e.Name.LocalName == "ProjectReference")) { score += 5; reasons.Add("references other projects"); }
        if (File.Exists(Path.Combine(dir, "appsettings.json"))) { score += 3; reasons.Add("appsettings.json"); }

        return new ApiCandidate(csprojPath, name, assembly, assembly + ".dll", tfm, ToKebab(assembly), score, string.Join(", ", reasons));
    }

    /// <summary>MyCompany.Api becomes my-company-api: lowercase, digits and hyphens, starting with a letter.</summary>
    public static string ToKebab(string name)
    {
        var k = Regex.Replace(name, "([a-z0-9])([A-Z])", "$1-$2");
        k = Regex.Replace(k, "[^A-Za-z0-9]+", "-").Trim('-').ToLowerInvariant();
        if (k.Length == 0) return "api";
        if (!char.IsLetter(k[0])) k = "api-" + k;
        return k;
    }

    /// <summary>net9.0 gives 9; net472 and other non-Core targets give null.</summary>
    public static int? RuntimeMajor(string? tfm)
    {
        if (string.IsNullOrWhiteSpace(tfm)) return null;
        var m = Regex.Match(tfm, @"^net(\d+)\.\d+", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }
}
