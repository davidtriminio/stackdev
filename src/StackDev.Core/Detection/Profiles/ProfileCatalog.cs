using System.Reflection;
using System.Text.Json;
using StackDev.Core.State;

namespace StackDev.Core.Detection.Profiles;

/// <summary>
/// The profiles in use: the ones the user wrote in <c>%APPDATA%\stackdev\profiles\*.json</c> first (so they can override a built-in one),
/// then the ones that ship inside the application. A file that cannot be read is skipped and reported in <see cref="Warnings"/>.
/// </summary>
public sealed class ProfileCatalog
{
    private const string BuiltInResource = "StackDev.Core.Detection.Profiles.profiles.json";
    private static readonly JsonSerializerOptions Json = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public IReadOnlyList<ProjectProfile> Profiles { get; }
    public IReadOnlyList<string> Warnings { get; }

    private ProfileCatalog(List<ProjectProfile> profiles, List<string> warnings) { Profiles = profiles; Warnings = warnings; }

    /// <summary>Where a person puts their own profiles.</summary>
    public static string UserFolder(string? appData = null) => Path.Combine(StateStore.DefaultDir(appData), "profiles");

    /// <summary>The built-in profiles plus the user's.</summary>
    public static ProfileCatalog Load(string? userFolder = null)
    {
        var profiles = new List<ProjectProfile>();
        var warnings = new List<string>();
        userFolder ??= UserFolder();
        if (Directory.Exists(userFolder))
        {
            foreach (var file in Directory.EnumerateFiles(userFolder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                Add(profiles, warnings, FileTree.Read(file), Path.GetFileName(file));
        }
        Add(profiles, warnings, ReadBuiltIn(), "built-in profiles");
        return new(profiles, warnings);
    }

    /// <summary>Only the profiles in <paramref name="json"/> (for tests and tools).</summary>
    public static ProfileCatalog FromJson(string json)
    {
        var profiles = new List<ProjectProfile>();
        var warnings = new List<string>();
        Add(profiles, warnings, json, "json");
        return new(profiles, warnings);
    }

    /// <summary>The names of the files any profile looks at (to search for them in a project).</summary>
    public IEnumerable<string> FileNames => Profiles.SelectMany(p => p.Match.Files).Distinct(StringComparer.OrdinalIgnoreCase);

    private static void Add(List<ProjectProfile> into, List<string> warnings, string json, string source)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var file = JsonSerializer.Deserialize<ProfileFile>(json, Json);
            foreach (var p in file?.Profiles ?? new())
            {
                if (Problem(p) is { } why) { warnings.Add($"{source}: profile «{p.Id}» ignored: {why}"); continue; }
                into.Add(p);
            }
        }
        catch (JsonException ex) { warnings.Add($"{source}: not read ({ex.Message})"); }
    }

    private static string? Problem(ProjectProfile p)
    {
        if (string.IsNullOrWhiteSpace(p.Id)) return "it has no id";
        if (p.Role is not ("api" or "web")) return "role must be \"api\" or \"web\"";
        if (p.Match.Files.Count == 0) return "match.files is empty";
        try
        {
            foreach (var r in p.Match.Contains.Concat(p.Match.NotContains).Concat(p.PortFrom.Select(x => x.Regex)))
                _ = new System.Text.RegularExpressions.Regex(r);
        }
        catch (ArgumentException ex) { return "a pattern is not a valid regular expression (" + ex.Message + ")"; }
        return null;
    }

    private static string ReadBuiltIn()
    {
        using var s = typeof(ProfileCatalog).Assembly.GetManifestResourceStream(BuiltInResource);
        if (s is null) return "";
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private sealed class ProfileFile
    {
        [System.Text.Json.Serialization.JsonPropertyName("profiles")] public List<ProjectProfile> Profiles { get; set; } = new();
    }
}
