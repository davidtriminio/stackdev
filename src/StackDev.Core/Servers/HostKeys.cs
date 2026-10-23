using System.Text.RegularExpressions;
using StackDev.Core.Proc;

namespace StackDev.Core.Servers;

/// <summary>What a server presented as its identity.</summary>
public sealed record HostKeyInfo(string Fingerprint, string KeyType, string KnownHostsLine);

/// <summary>
/// Pins each server's SSH host key. StackDev keeps its own known_hosts file and connects with StrictHostKeyChecking=yes,
/// so a server is only reachable after the user has checked its fingerprint once, and a changed key is refused.
/// </summary>
public static class HostKeys
{
    private static readonly string[] Preference = { "ssh-ed25519", "ecdsa-sha2-nistp256", "ssh-rsa" };

    /// <summary>known_hosts used by every StackDev connection (inside the personal state folder).</summary>
    public static string KnownHostsPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), State.StateStore.FolderName, "known_hosts");

    /// <summary>The value for <c>-o UserKnownHostsFile=</c>: forward slashes, so ssh does not read backslashes as escapes on Windows.</summary>
    public static string KnownHostsOption => "UserKnownHostsFile=" + KnownHostsPath.Replace('\\', '/');

    /// <summary>How ssh names this server inside known_hosts: the host, or [host]:port for other ports.</summary>
    public static string EntryName(Server s) => s.Port is > 0 and not 22 ? $"[{s.Host}]:{s.Port}" : s.Host;

    public static bool IsTrusted(Server s)
    {
        if (!File.Exists(KnownHostsPath)) return false;
        var r = ProcessRunner.Run("ssh-keygen", new[] { "-F", EntryName(s), "-f", KnownHostsPath }, 10);
        return r.Ok && r.Output.Contains(' ');
    }

    /// <summary>Asks the server for its public keys without logging in. Null when it does not answer.</summary>
    public static HostKeyInfo? Scan(Server s, CancellationToken ct = default)
    {
        var args = new List<string> { "-T", "8", "-t", "ed25519,ecdsa,rsa" };
        if (s.Port is > 0 and not 22) { args.Add("-p"); args.Add(s.Port.ToString()); }
        args.Add(s.Host);
        var scan = ProcessRunner.Run("ssh-keyscan", args, 20, ct: ct);
        if (!scan.Ok) return null;

        var lines = scan.Output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith('#')).ToList();
        foreach (var type in Preference)
        {
            var line = lines.FirstOrDefault(l => l.Split(' ', 3) is { Length: 3 } p && p[1] == type);
            if (line is null) continue;
            var fp = Fingerprint(line);
            if (fp is not null) return new HostKeyInfo(fp, type, line);
        }
        return null;
    }

    private static string? Fingerprint(string knownHostsLine)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "stackdev-hk-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            File.WriteAllText(tmp, knownHostsLine + "\n");
            var r = ProcessRunner.Run("ssh-keygen", new[] { "-l", "-E", "sha256", "-f", tmp }, 10);
            var m = Regex.Match(r.Output, @"(SHA256:[A-Za-z0-9+/=]+)");
            return m.Success ? m.Groups[1].Value : null;
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
    }

    /// <summary>Remembers this key for the server (replacing any earlier one).</summary>
    public static void Trust(Server s, HostKeyInfo key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KnownHostsPath)!);
        Forget(s);
        File.AppendAllText(KnownHostsPath, key.KnownHostsLine.TrimEnd() + "\n");
    }

    /// <summary>Removes what is remembered about the server, so it has to be verified again.</summary>
    public static void Forget(Server s)
    {
        if (!File.Exists(KnownHostsPath)) return;
        ProcessRunner.Run("ssh-keygen", new[] { "-R", EntryName(s), "-f", KnownHostsPath }, 10);
        try { File.Delete(KnownHostsPath + ".old"); } catch (IOException) { }
    }
}
