using System.Text.RegularExpressions;
using StackDev.Core.Config;
using StackDev.Core.Util;

namespace StackDev.Core.Detection.Profiles;

/// <summary>A profile and the project folder where it was recognised.</summary>
public sealed record ProfileHit(ProjectProfile Profile, string Folder);

/// <summary>Recognises projects from <see cref="ProjectProfile"/> data and turns a match into configuration.</summary>
public static class ProfileDetector
{
    private const RegexOptions Ci = RegexOptions.IgnoreCase;

    /// <summary>
    /// The first profile (in catalog order) of <paramref name="role"/> that matches a file in <paramref name="files"/>;
    /// among its files the one closest to the root wins.
    /// </summary>
    public static ProfileHit? Find(IReadOnlyList<string> files, ProfileCatalog catalog, string role)
    {
        foreach (var profile in catalog.Profiles.Where(p => p.Role == role))
        {
            var hit = files
                .Where(f => profile.Match.Files.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                .Where(f => Matches(profile.Match, f))
                .OrderBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal)
                .FirstOrDefault();
            if (hit is not null) return new(profile, Path.GetDirectoryName(hit)!);
        }
        return null;
    }

    private static bool Matches(ProfileMatch m, string file)
    {
        var text = FileTree.Read(file);
        var dir = Path.GetDirectoryName(file)!;
        return m.Contains.All(p => Regex.IsMatch(text, p, Ci))
            && !m.NotContains.Any(p => Regex.IsMatch(text, p, Ci))
            && m.AlsoFiles.All(f => File.Exists(Path.Combine(dir, f)));
    }

    /// <summary>The "api" slot for a match; also adds the profile's tasks (migrations, install...).</summary>
    public static ApiConfig BuildApi(string root, ProfileHit hit, List<TaskConfig> tasks)
    {
        var (p, dir) = (hit.Profile, hit.Folder);
        var pm = Detector.PackageManager(root, dir);
        var folder = Paths.Relative(root, dir);
        AddTasks(tasks, p, folder, dir, pm, installAsTask: true);
        return new ApiConfig
        {
            Folder = folder,
            Url = UrlWithPort(p, dir),
            Open = p.Open,
            Env = new Dictionary<string, string>(),
            Start = p.Commands.Start?.Resolve(dir, pm) ?? "",
            Test = p.Commands.Test?.Resolve(dir, pm) ?? "",
            Build = p.Commands.Build?.Resolve(dir, pm) ?? "",
        };
    }

    /// <summary>The "web" slot for a match.</summary>
    public static WebConfig BuildWeb(string root, ProfileHit hit, List<TaskConfig> tasks)
    {
        var (p, dir) = (hit.Profile, hit.Folder);
        var pm = Detector.PackageManager(root, dir);
        var folder = Paths.Relative(root, dir);
        AddTasks(tasks, p, folder, dir, pm, installAsTask: false);
        return new WebConfig
        {
            Folder = folder,
            Url = UrlWithPort(p, dir),
            Start = p.Commands.Start?.Resolve(dir, pm) ?? "",
            Test = p.Commands.Test?.Resolve(dir, pm) ?? "",
            Build = p.Commands.Build?.Resolve(dir, pm) ?? "",
            Install = p.Commands.Install?.Resolve(dir, pm) ?? "",
        };
    }

    private static void AddTasks(List<TaskConfig> tasks, ProjectProfile p, string folder, string dir, string pm, bool installAsTask)
    {
        // The api slot has no "install" field, so the install command is offered as a task.
        if (installAsTask && p.Commands.Install?.Resolve(dir, pm) is { } install)
            tasks.Add(new TaskConfig { Name = "Instalar dependencias", Folder = folder, Command = install });
        foreach (var t in p.Tasks)
        {
            if (t.When is not null && !File.Exists(Path.Combine(dir, t.When))) continue;
            tasks.Add(new TaskConfig { Name = t.Name, Folder = folder, Command = t.Command.Replace("{pm}", pm, StringComparison.Ordinal) });
        }
    }

    /// <summary>The profile's address with the port found by its <c>portFrom</c> rules, if any.</summary>
    internal static string UrlWithPort(ProjectProfile p, string dir)
    {
        foreach (var rule in p.PortFrom)
        {
            var m = Regex.Match(FileTree.Read(Path.Combine(dir, rule.File)), rule.Regex, Ci);
            if (m.Success && m.Groups.Count > 1 && int.TryParse(m.Groups[1].Value, out var port) && port is > 0 and < 65536)
                return Regex.Replace(p.Url, @":\d+(?=/|$)", ":" + port);
        }
        return p.Url;
    }
}
