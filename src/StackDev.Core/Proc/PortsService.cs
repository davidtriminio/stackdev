using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using StackDev.Core.Config;
using StackDev.Core.State;
using StackDev.Core.Ui;
using StackDev.Core.Util;

namespace StackDev.Core.Proc;

/// <summary>
/// Suggests ports that clash with nothing: processes already listening, ports published by containers
/// (stopped ones too: starting them would take the ports again), other stackdev projects, this same project
/// and the ranges Windows reserves (Hyper-V / WSL).
/// </summary>
public sealed class PortsService
{
    private readonly Session _s;
    public PortsService(Session s) { _s = s; }

    /// <summary>A snapshot of what is in use; taken once and good for several questions.</summary>
    public sealed class Map
    {
        internal readonly Dictionary<int, string> ReservedPorts = new();
        internal readonly List<(int From, int Hasta)> WindowsRanges = new();

        /// <summary>Who holds the port (text to display), or null if it is free.</summary>
        public string? Conflict(int port)
        {
            if (port <= 0) return null;
            if (Ports.IsListening(port)) return "ya lo usa " + OwnerProcess(port);
            if (ReservedPorts.TryGetValue(port, out var q)) return q;
            if (WindowsRanges.Any(r => port >= r.From && port <= r.Hasta)) return "Windows lo tiene reservado (Hyper-V/WSL)";
            return null;
        }

        /// <summary>Only what is reserved by configuration, containers or Windows (what is listening right now is not counted, since it may be the service itself).</summary>
        public string? ReservedReason(int port)
        {
            if (port <= 0) return null;
            if (ReservedPorts.TryGetValue(port, out var q)) return q;
            return WindowsRanges.Any(r => port >= r.From && port <= r.Hasta) ? "Windows lo tiene reservado (Hyper-V/WSL)" : null;
        }

        public bool Free(int port) => Conflict(port) is null;

        /// <summary>The first free port from <paramref name="preferred"/> upwards (up to 300 attempts); null if none.</summary>
        public int? Suggest(int preferred)
        {
            var p = preferred <= 0 ? 5432 : preferred;
            for (var i = 0; i < 300 && p <= 65535; i++, p++)
                if (Free(p)) return p;
            return null;
        }

        private static string OwnerProcess(int port)
        {
            try
            {
                var pid = Ports.Pids(port).FirstOrDefault();
                if (pid > 0) return $"el proceso {Process.GetProcessById(pid).ProcessName} (PID {pid})";
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return "otro proceso";
        }
    }

    /// <param name="ignoreContainer">Container whose ports do not count (the one about to be recreated).</param>
    /// <param name="ignoreComponent">Component of this project whose port does not count (the one being changed).</param>
    public Map Take(string? ignoreContainer = null, ProjectComponent? ignoreComponent = null)
    {
        var m = new Map();

        // The other components of this project
        var ctx = _s.Ctx;
        var mine = new List<(int Port, string Text)>();   // added at the end: they must not hide other projects or containers
        void Mine(ProjectComponent c, int port, string name)
        {
            if (c == ignoreComponent || port <= 0) return;
            mine.Add((port, $"lo usa {name} de este proyecto"));
        }
        if (ctx.Has(ProjectComponent.Postgres)) Mine(ProjectComponent.Postgres, ctx.Config.Postgres!.EfPort, "Postgres");
        if (ctx.Has(ProjectComponent.Api)) Mine(ProjectComponent.Api, ctx.Port(ProjectComponent.Api), "la API");
        if (ctx.Has(ProjectComponent.Web)) Mine(ProjectComponent.Web, ctx.Port(ProjectComponent.Web), "la web");

        // Other stackdev projects
        try
        {
            foreach (var root in _s.State.ReadProjects())
            {
                if (string.Equals(Path.GetFullPath(root), _s.Root, StringComparison.OrdinalIgnoreCase)) continue;
                var other = new ConfigStore(root, _s.State.ConfigFolder(StateStore.Key(root)));
                if (!other.Exists) continue;
                other.Load();
                var c = other.Context;
                var name = c.Name;
                void Foreign(int port, string that)
                {
                    if (port > 0) m.ReservedPorts.TryAdd(port, $"lo usa {that} del proyecto «{name}»");
                }
                if (c.Has(ProjectComponent.Postgres)) Foreign(c.Config.Postgres!.EfPort, "Postgres");
                if (c.Has(ProjectComponent.Api)) Foreign(c.Port(ProjectComponent.Api), "la API");
                if (c.Has(ProjectComponent.Web)) Foreign(c.Port(ProjectComponent.Web), "la web");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }

        // Containers (stopped ones too)
        if (_s.Docker.Installed)
        {
            var ids = _s.Docker.Run(new[] { "ps", "-a", "-q" }, 8);
            var list = ids.Ok ? ids.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList() : new();
            if (list.Count > 0)
            {
                var args = new List<string> { "inspect", "--format", "{{.Name}}\t{{json .HostConfig.PortBindings}}" };
                args.AddRange(list);
                var r = _s.Docker.Run(args, 12);
                foreach (var line in r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    var name = line[..tab].Trim().TrimStart('/');
                    if (ignoreContainer is not null && name == ignoreContainer) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line[(tab + 1)..].Trim());
                        if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                        foreach (var prop in doc.RootElement.EnumerateObject())
                            if (prop.Value.ValueKind == JsonValueKind.Array)
                                foreach (var b in prop.Value.EnumerateArray())
                                    if (b.TryGetProperty("HostPort", out var hp) && int.TryParse(hp.GetString(), out var port) && port > 0)
                                        m.ReservedPorts.TryAdd(port, $"lo publica el contenedor «{name}»");
                    }
                    catch (JsonException) { }
                }
            }
        }

        foreach (var (port, text) in mine) m.ReservedPorts.TryAdd(port, text);

        // Ranges reserved by Windows
        if (OperatingSystem.IsWindows())
        {
            var r = ProcessRunner.Run("netsh.exe", new[] { "interface", "ipv4", "show", "excludedportrange", "protocol=tcp" }, 8);
            foreach (Match x in Regex.Matches(r.Output, @"^\s*(\d+)\s+(\d+)", RegexOptions.Multiline))
                m.WindowsRanges.Add((int.Parse(x.Groups[1].Value), int.Parse(x.Groups[2].Value)));
        }
        return m;
    }

    // ------------------------------------------------------------ Project port plan

    public sealed record PortChange(ProjectComponent ProjectComponent, int Current, int Fresh, string Reason);

    /// <summary>
    /// Which ports of this project would have to move so they do not clash with other projects, containers or Windows reservations,
    /// even if nothing is running. What is already fine is left alone (a project's port is fixed as long as it does not clash).
    /// Postgres only takes part if its container does not exist yet (if it exists, changing the port forces recreating it).
    /// </summary>
    public List<PortChange> Plan()
    {
        var ctx = _s.Ctx;
        var changes = new List<PortChange>();
        var pgExists = false;
        if (ctx.Has(ProjectComponent.Postgres) && _s.Docker.Installed)
            pgExists = _s.Docker.Run(new[] { "inspect", "-f", "{{.Id}}", ctx.Config.Postgres!.EfContainer }, 6).Ok;

        var map = Take(ignoreContainer: ctx.Has(ProjectComponent.Postgres) ? ctx.Config.Postgres!.EfContainer : null);
        // This project's ports are already in the map as "this project": each one is evaluated without its own
        var own = new List<(ProjectComponent C, int Port)>();
        if (ctx.Has(ProjectComponent.Postgres) && !pgExists) own.Add((ProjectComponent.Postgres, ctx.Config.Postgres!.EfPort));
        if (ctx.Has(ProjectComponent.Api) && ctx.Port(ProjectComponent.Api) > 0) own.Add((ProjectComponent.Api, ctx.Port(ProjectComponent.Api)));
        if (ctx.Has(ProjectComponent.Web) && ctx.Port(ProjectComponent.Web) > 0) own.Add((ProjectComponent.Web, ctx.Port(ProjectComponent.Web)));

        foreach (var (comp, port) in own)
        {
            var reason = map.ReservedReason(port);
            // "used by X of this project" points to another component of the same project, unless it is the same shared port
            if (reason is not null && reason.Contains("de este proyecto") && own.Count(x => x.Port == port) < 2) reason = null;
            if (reason is null) continue;
            var fresh = map.Suggest(port + 1);
            if (fresh is null) continue;
            map.ReservedPorts[fresh.Value] = "lo usa este proyecto";
            changes.Add(new PortChange(comp, port, fresh.Value, reason));
        }
        return changes;
    }

    private static string ChangePortInText(string? text, int old, int fresh) =>
        text is null ? "" : Regex.Replace(text, $@"(?<=:|--port[ =]|-p )(?:{old})(?!\d)", fresh.ToString());

    /// <summary>Saves the changes: Postgres port, API/web URL and the times the old port appears in their commands and variables.</summary>
    public void Apply(IReadOnlyList<PortChange> changes)
    {
        if (changes.Count == 0) return;
        var copy = ConfigJson.Read(ConfigJson.Write(_s.Ctx.Config));
        foreach (var c in changes)
        {
            switch (c.ProjectComponent)
            {
                case ProjectComponent.Postgres when copy.Postgres is not null:
                    copy.Postgres.Port = c.Fresh;
                    break;
                case ProjectComponent.Api when copy.Api is not null:
                    copy.Api.Url = Regex.Replace(copy.Api.Url ?? "", @"(://[^/:]+):\d+", $"$1:{c.Fresh}");
                    copy.Api.Start = ChangePortInText(copy.Api.Start, c.Current, c.Fresh) is { Length: > 0 } i1 ? i1 : copy.Api.Start;
                    if (copy.Api.Env is not null)
                        foreach (var k in copy.Api.Env.Keys.ToList())
                            copy.Api.Env[k] = ChangePortInText(copy.Api.Env[k], c.Current, c.Fresh);
                    break;
                case ProjectComponent.Web when copy.Web is not null:
                    copy.Web.Url = Regex.Replace(copy.Web.Url ?? "", @"(://[^/:]+):\d+", $"$1:{c.Fresh}");
                    copy.Web.Start = ChangePortInText(copy.Web.Start, c.Current, c.Fresh) is { Length: > 0 } i2 ? i2 : copy.Web.Start;
                    break;
            }
        }
        _s.ApplyConfig(copy);
        foreach (var c in changes)
        {
            _s.Ui.Write($"{Session.Names[c.ProjectComponent]}: puerto {c.Current} → {c.Fresh}", Level.Ok);
            if (c.ProjectComponent == ProjectComponent.Postgres) continue;
            var refs = References(c.Current);
            if (refs.Count > 0)
            {
                _s.Ui.Write($"  El puerto {c.Current} aparece todavía en estos archivos del proyecto (revísalos a mano):", Level.Warning);
                foreach (var r in refs) _s.Ui.Write($"    {r}", Level.Muted);
            }
        }
    }

    private static readonly string[] Extensions = { ".json", ".ts", ".js", ".env", ".dart", ".cs", ".yml", ".yaml", ".html", ".config", ".properties" };
    private static readonly string[] Excluded = { "node_modules", "bin", "obj", ".git", ".dart_tool", "build", "dist", ".angular", ".idea", ".vs", ".gradle", "Pods" };

    /// <summary>Project files where <c>:port</c> appears (launchSettings, environment.ts, CORS...). At most 10.</summary>
    public List<string> References(int port)
    {
        var res = new List<string>();
        var pattern = new Regex($@":{port}(?!\d)");
        foreach (var comp in new[] { ProjectComponent.Api, ProjectComponent.Web, ProjectComponent.Mobile })
        {
            if (!_s.Ctx.Has(comp)) continue;
            var dir = _s.Ctx.Folder(comp);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Files(dir))
            {
                if (res.Count >= 10) return res;
                try
                {
                    if (new FileInfo(f).Length > 300_000) continue;
                    var lines = File.ReadLines(f).Take(4000).ToList();
                    var n = lines.FindIndex(l => pattern.IsMatch(l));
                    if (n >= 0) res.Add($"{_s.Rel(f)}:{n + 1}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return res;
    }

    private static IEnumerable<string> Files(string dir)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            string[] subs, files;
            try { subs = Directory.GetDirectories(d); files = Directory.GetFiles(d); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files)
                if (Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) || Path.GetFileName(f).StartsWith(".env")) yield return f;
            foreach (var sd in subs)
                if (!Excluded.Contains(Path.GetFileName(sd), StringComparer.OrdinalIgnoreCase)) stack.Push(sd);
        }
    }
}
