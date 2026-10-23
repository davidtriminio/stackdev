using System.Text.RegularExpressions;

namespace StackDev.Core.Deploy;

public enum GuardLevel
{
    /// <summary>Must not reach the server. The deployment stops unless the file is removed from the publish folder.</summary>
    Block,
    /// <summary>Worth knowing about, does not stop the deployment.</summary>
    Warn,
}

public sealed record GuardFinding(GuardLevel Level, string RelativePath, string Reason);

/// <summary>Looks through a publish folder for files that should never be uploaded: environment files, private keys, local databases, debug symbols.</summary>
public static class PublishGuard
{
    private static readonly (Regex Pattern, GuardLevel Level, string Reason)[] Rules =
    {
        (new(@"(^|/)\.env(\..+)?$", RegexOptions.IgnoreCase), GuardLevel.Block, "environment file (secrets); the server has its own"),
        (new(@"(^|/)appsettings\.Development\.json$", RegexOptions.IgnoreCase), GuardLevel.Block, "development settings must not run in production"),
        (new(@"(^|/)secrets\.json$", RegexOptions.IgnoreCase), GuardLevel.Block, "user secrets file"),
        (new(@"\.(pem|key|pfx|p12|jks|keystore|ppk)$", RegexOptions.IgnoreCase), GuardLevel.Block, "private key or certificate"),
        (new(@"(^|/)id_(rsa|dsa|ecdsa|ed25519)(\.pub)?$", RegexOptions.IgnoreCase), GuardLevel.Block, "SSH key"),
        (new(@"\.pdb$", RegexOptions.IgnoreCase), GuardLevel.Block, "debug symbols (expose source paths)"),
        (new(@"\.(db|sqlite|sqlite3|db-wal|db-shm|mdf|ldf)$", RegexOptions.IgnoreCase), GuardLevel.Block, "local database; it would replace or leak data"),
        (new(@"\.(log|bak)$", RegexOptions.IgnoreCase), GuardLevel.Warn, "log or backup file"),
        (new(@"(^|/)launchSettings\.json$", RegexOptions.IgnoreCase), GuardLevel.Warn, "local launch profile"),
        (new(@"\.(user|suo)$", RegexOptions.IgnoreCase), GuardLevel.Warn, "per-user IDE file"),
        (new(@"(^|/)wwwroot/uploads/.+", RegexOptions.IgnoreCase), GuardLevel.Warn, "uploaded user files; the server keeps its own"),
    };

    private static readonly Regex InlineSecret = new(
        @"""(?<k>[A-Za-z0-9_.]*(password|pwd|secret|token|apikey|api_key|privatekey|jwtkey)[A-Za-z0-9_.]*|key)""\s*:\s*""(?<v>[^""]{6,})""",
        RegexOptions.IgnoreCase);

    private static readonly Regex ConnectionPassword = new(@"(password|pwd)\s*=\s*(?<v>[^;""]+)", RegexOptions.IgnoreCase);

    private static readonly string[] Placeholders = { "change", "your", "xxx", "***", "<", "{", "secret_here", "placeholder", "todo", "example", "dummy" };

    public static List<GuardFinding> Scan(string publishDir)
    {
        var result = new List<GuardFinding>();
        if (!Directory.Exists(publishDir)) return result;

        foreach (var file in Directory.EnumerateFiles(publishDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(publishDir, file).Replace('\\', '/');
            foreach (var (pattern, level, reason) in Rules)
            {
                if (!pattern.IsMatch(rel)) continue;
                result.Add(new GuardFinding(level, rel, reason));
                break;
            }
            if (Regex.IsMatch(rel, @"^appsettings(\.[^/]+)?\.json$", RegexOptions.IgnoreCase) && !rel.EndsWith("Development.json", StringComparison.OrdinalIgnoreCase))
                result.AddRange(ScanSettings(file, rel));
        }
        return result.OrderBy(f => f.Level).ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<GuardFinding> ScanSettings(string file, string rel)
    {
        string text;
        try { text = File.ReadAllText(file); }
        catch (IOException) { yield break; }

        foreach (Match m in InlineSecret.Matches(text))
        {
            var v = m.Groups["v"].Value;
            if (IsPlaceholder(v)) continue;
            yield return new GuardFinding(GuardLevel.Warn, rel, $"«{m.Groups["k"].Value}» has a real-looking value; secrets belong in the server .env");
        }
        foreach (Match m in ConnectionPassword.Matches(text))
        {
            if (IsPlaceholder(m.Groups["v"].Value)) continue;
            yield return new GuardFinding(GuardLevel.Warn, rel, "a connection string includes a password; move it to the server .env");
            break;
        }
    }

    private static bool IsPlaceholder(string v) =>
        Placeholders.Any(p => v.Contains(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Deletes the blocking files from the publish folder (it is a throwaway copy, never the project). Returns what was removed.</summary>
    public static List<string> RemoveBlocked(string publishDir, IEnumerable<GuardFinding> findings)
    {
        var removed = new List<string>();
        var root = Path.GetFullPath(publishDir);
        foreach (var f in findings.Where(f => f.Level == GuardLevel.Block))
        {
            var full = Path.GetFullPath(Path.Combine(root, f.RelativePath));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;   // never leave the publish folder
            try { if (File.Exists(full)) { File.Delete(full); removed.Add(f.RelativePath); } }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }
}
