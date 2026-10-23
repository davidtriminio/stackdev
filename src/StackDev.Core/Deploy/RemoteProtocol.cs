namespace StackDev.Core.Deploy;

public enum RemoteEventKind { Log, Step, Info, Warn, Fail, Kv, Api, Backup, Done, EnvBegin, EnvEnd }

/// <summary>One line the remote script printed, already understood.</summary>
public sealed record RemoteEvent(RemoteEventKind Kind, string Text, int Step = 0, int Total = 0, int Code = 0, string? Key = null);

/// <summary>Reads the line protocol of stackdev-remote.sh (lines starting with ##); anything else is plain log.</summary>
public static class RemoteProtocol
{
    public static RemoteEvent Parse(string? line)
    {
        line = (line ?? "").TrimEnd('\r', '\n');
        if (!line.StartsWith("##", StringComparison.Ordinal)) return new(RemoteEventKind.Log, line);

        var space = line.IndexOf(' ');
        var tag = space < 0 ? line : line[..space];
        var rest = space < 0 ? "" : line[(space + 1)..];
        switch (tag)
        {
            case "##STEP":
            {
                var sp = rest.IndexOf(' ');
                var counter = sp < 0 ? rest : rest[..sp];
                var title = sp < 0 ? "" : rest[(sp + 1)..];
                var slash = counter.IndexOf('/');
                if (slash > 0 && int.TryParse(counter[..slash], out var n) && int.TryParse(counter[(slash + 1)..], out var t))
                    return new(RemoteEventKind.Step, title, n, t);
                return new(RemoteEventKind.Log, line);
            }
            case "##INFO": return new(RemoteEventKind.Info, rest);
            case "##WARN": return new(RemoteEventKind.Warn, rest);
            case "##FAIL":
            {
                var sp = rest.IndexOf(' ');
                var code = sp < 0 ? rest : rest[..sp];
                return new(RemoteEventKind.Fail, sp < 0 ? "" : rest[(sp + 1)..], Code: int.TryParse(code, out var c) ? c : 1);
            }
            case "##KV":
            {
                var eq = rest.IndexOf('=');
                return eq <= 0 ? new(RemoteEventKind.Log, line) : new(RemoteEventKind.Kv, rest[(eq + 1)..], Key: rest[..eq]);
            }
            case "##API": return new(RemoteEventKind.Api, rest);
            case "##BACKUP": return new(RemoteEventKind.Backup, rest);
            case "##DONE": return new(RemoteEventKind.Done, "");
            case "##ENV-BEGIN": return new(RemoteEventKind.EnvBegin, "");
            case "##ENV-END": return new(RemoteEventKind.EnvEnd, "");
            default: return new(RemoteEventKind.Log, line);
        }
    }
}

/// <summary>An API service already installed on the server.</summary>
public sealed record RemoteApi(string Service, string Dll, string Directory, string User, string State, string Domain = "")
{
    public static RemoteApi? Parse(string text)
    {
        var p = text.Split('|');
        return p.Length >= 5 ? new RemoteApi(p[0], p[1], p[2], p[3], p[4], p.Length > 5 ? p[5].Trim() : "") : null;
    }
}

/// <summary>A saved previous version of the API on the server.</summary>
public sealed record RemoteBackup(string Id, string Date, long SizeKb)
{
    public static RemoteBackup? Parse(string text)
    {
        var p = text.Split('|');
        return p.Length >= 3 ? new RemoteBackup(p[0], p[1], long.TryParse(p[2], out var kb) ? kb : 0) : null;
    }
}

/// <summary>Everything a remote command reported, in the order it matters.</summary>
public sealed class RemoteResult
{
    public int ExitCode { get; set; }
    public bool Done { get; set; }
    public string? FailMessage { get; set; }
    public Dictionary<string, string> Values { get; } = new();
    public List<RemoteApi> Apis { get; } = new();
    public List<RemoteBackup> Backups { get; } = new();
    /// <summary>The env file for env-read. Never written to a log.</summary>
    public string? EnvText { get; set; }
    /// <summary>The command could not run at all (no ssh, no connection, ...).</summary>
    public string? LaunchError { get; set; }

    public bool Ok => ExitCode == 0 && Done && LaunchError is null;
    public string Value(string key) => Values.TryGetValue(key, out var v) ? v : "";
}
