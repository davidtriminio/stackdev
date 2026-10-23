using System.Diagnostics;
using StackDev.Core.Proc;
using Xunit;

namespace StackDev.Core.Tests;

public class ProcessRunnerTests
{
    // A process that stays alive and silent for a long time, on any OS.
    private static (string Exe, string[] Args) Silent() => OperatingSystem.IsWindows()
        ? ("ping", new[] { "-n", "30", "127.0.0.1" })
        : ("sleep", new[] { "30" });

    [Fact]
    public void RunCapturing_stops_a_process_that_exceeds_the_total_limit()
    {
        var (exe, args) = Silent();
        var sw = Stopwatch.StartNew();
        var code = ProcessRunner.RunCapturing(exe, args, null, null, CancellationToken.None, _ => { }, timeoutSec: 1, closeStdin: true);
        Assert.Equal(ProcessRunner.TimedOut, code);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
    }

    [Fact]
    public void RunCapturing_stops_a_process_that_goes_silent()
    {
        if (!OperatingSystem.IsWindows()) return;   // the silence is only measured between lines; ping prints one per second
        var lines = new List<string>();
        var code = ProcessRunner.RunCapturing("ping", new[] { "-n", "30", "-w", "10000", "10.255.255.1" }, null, null, CancellationToken.None, lines.Add, idleSec: 2);
        Assert.Equal(ProcessRunner.TimedOut, code);
    }

    [Fact]
    public void RunCapturing_can_be_cancelled()
    {
        var (exe, args) = Silent();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var code = ProcessRunner.RunCapturing(exe, args, null, null, cts.Token, _ => { }, timeoutSec: 60, closeStdin: true);
        Assert.Equal(130, code);
    }

    [Fact]
    public void RunCapturing_without_limits_returns_the_exit_code_and_the_lines()
    {
        var lines = new List<string>();
        var (exe, args) = OperatingSystem.IsWindows() ? ("cmd", new[] { "/c", "echo hola" }) : ("echo", new[] { "hola" });
        var code = ProcessRunner.RunCapturing(exe, args, null, null, CancellationToken.None, lines.Add, timeoutSec: 20, closeStdin: true);
        Assert.Equal(0, code);
        Assert.Contains("hola", lines);
    }
}
