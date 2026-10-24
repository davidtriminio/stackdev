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
    public static HostKeyInfo? Scan(Server s, CancellationToken ct = default) => Scan(s, out _, ct);

    /// <summary>
    /// Like <see cref="Scan(Server, CancellationToken)"/> and also says why it failed. It tries ssh-keyscan first and, when that gives nothing
    /// (some servers and networks answer ssh but not the scanner), connects with ssh itself into a throwaway known_hosts file,
    /// which records the key before authentication. Nothing is trusted here: the caller shows the fingerprint to the user.
    /// </summary>
    public static HostKeyInfo? Scan(Server s, out string? detail, CancellationToken ct = default)
    {
        var viaScanner = ScanWithKeyscan(s, out var scanDetail, ct);
        if (viaScanner is not null) { detail = null; return viaScanner; }
        var viaSsh = ScanWithSsh(s, out var sshDetail, ct);
        detail = viaSsh is not null ? null : (sshDetail ?? scanDetail);
        return viaSsh;
    }

    private static string FirstLine(string text) =>
        text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => !l.StartsWith('#')) ?? "";

    private static HostKeyInfo? Pick(IEnumerable<string> lines)
    {
        var list = lines.Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        foreach (var type in Preference)
        {
            var line = list.FirstOrDefault(l => l.Split(' ', 3) is { Length: 3 } p && p[1] == type);
            if (line is null) continue;
            var fp = Fingerprint(line);
            if (fp is not null) return new HostKeyInfo(fp, type, line);
        }
        return null;
    }

    private static HostKeyInfo? ScanWithKeyscan(Server s, out string? detail, CancellationToken ct)
    {
        detail = null;
        var args = new List<string> { "-T", "8", "-t", "ed25519,ecdsa,rsa" };
        if (s.Port is > 0 and not 22) { args.Add("-p"); args.Add(s.Port.ToString()); }
        args.Add(s.Host);
        var scan = ProcessRunner.Run("ssh-keyscan", args, 20, ct: ct);
        if (scan.ErrorHome is not null) { detail = "No encuentro ssh-keyscan (instala «Cliente OpenSSH» en las características de Windows)."; return null; }
        var key = Pick(scan.Output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        if (key is null) detail = scan.TimeExpired ? "ssh-keyscan no respondió a tiempo." : FirstLine(scan.Error);
        return key;
    }

    private static HostKeyInfo? ScanWithSsh(Server s, out string? detail, CancellationToken ct)
    {
        detail = null;
        var tmp = Path.Combine(Path.GetTempPath(), "stackdev-hk-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var args = new List<string>
            {
                "-o", "ConnectTimeout=8", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new",
                "-o", "UserKnownHostsFile=" + tmp.Replace('\\', '/'), "-o", "HashKnownHosts=no",
                "-o", "PreferredAuthentications=none", "-o", "IdentitiesOnly=yes",
            };
            if (s.Port is > 0 and not 22) { args.Add("-p"); args.Add(s.Port.ToString()); }
            args.Add($"{s.User}@{s.Host}");
            args.Add("exit");
            var r = ProcessRunner.Run("ssh", args, 25, ct: ct);
            if (r.ErrorHome is not null) { detail = "No encuentro ssh (instala «Cliente OpenSSH» en las características de Windows)."; return null; }
            var key = File.Exists(tmp) ? Pick(File.ReadAllLines(tmp)) : null;
            if (key is null) detail = r.TimeExpired ? "El servidor no respondió a tiempo." : FirstLine(r.Error);
            return key;
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
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
