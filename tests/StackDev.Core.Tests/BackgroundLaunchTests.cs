using StackDev.Core.Proc;
using Xunit;

namespace StackDev.Core.Tests;

public class BackgroundLaunchTests
{
    [Fact]
    public void The_windows_command_line_keeps_the_log_and_the_exit_code_marker()
    {
        var a = BackgroundLaunch.WindowsArguments("dotnet run", @"C:\logs\api.log", @"C:\tmp\api.exit");
        Assert.Equal(@"/v:on /d /s /c ""chcp 65001>nul & (dotnet run) >> ""C:\logs\api.log"" 2>&1 & echo !errorlevel!> ""C:\tmp\api.exit""""", a);
    }

    [Fact]
    public void The_posix_script_appends_to_the_log_and_writes_the_exit_code()
    {
        var s = BackgroundLaunch.PosixScript("npm start", "/home/me/.config/stackdev/logs/web.log", "/tmp/web.exit");
        Assert.Equal("(npm start) >> '/home/me/.config/stackdev/logs/web.log' 2>&1; echo $? > '/tmp/web.exit'", s);
    }

    [Fact]
    public void A_quote_inside_a_path_cannot_break_out_of_the_posix_script()
    {
        var s = BackgroundLaunch.PosixScript("run", "/tmp/it's here/x.log", "/tmp/m");
        Assert.Contains(@"'/tmp/it'\''s here/x.log'", s);
    }

    [Fact]
    public void The_start_info_runs_the_command_in_the_given_folder_without_a_window()
    {
        var psi = BackgroundLaunch.Info("echo hi", Path.GetTempPath(), "log", "mark");
        Assert.Equal(Path.GetTempPath(), psi.WorkingDirectory);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
        if (OperatingSystem.IsWindows()) Assert.Equal("cmd.exe", psi.FileName);
        else Assert.Equal(new[] { "-c", BackgroundLaunch.PosixScript("echo hi", "log", "mark") }, psi.ArgumentList);
    }

    [Fact]
    public void A_background_command_really_runs_and_leaves_its_output_and_exit_code()
    {
        using var t = new TempFolder();
        var log = t.In("out.log"); var mark = t.In("out.exit");
        var psi = BackgroundLaunch.Info("echo hello-from-background", t.FilePath, log, mark);
        using var p = System.Diagnostics.Process.Start(psi)!;
        Assert.True(p.WaitForExit(20000));
        Assert.Contains("hello-from-background", File.ReadAllText(log));
        Assert.Equal("0", File.ReadAllText(mark).Trim());
    }
}
