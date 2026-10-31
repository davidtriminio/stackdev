using StackDev.Core.Config;
using StackDev.Core.State;
using Xunit;

namespace StackDev.Core.Tests;

public class RelocationTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "sd-reloc-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly StateStore _state;

    public RelocationTests()
    {
        Directory.CreateDirectory(_tmp);
        _state = new StateStore(Path.Combine(_tmp, "state"));
    }

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch (IOException) { } }

    private string Folder(string name) { var d = Path.Combine(_tmp, name); Directory.CreateDirectory(d); return d; }

    private void SaveConfig(string root, string name)
    {
        var store = new ConfigStore(root, _state.ConfigFolder(StateStore.Key(root)));
        store.Save(new ProjectConfig { Name = name, Api = new ApiConfig { Url = "http://localhost:5000", Start = "dotnet run" } });
    }

    [Fact]
    public void Relocate_moves_config_history_and_project_list()
    {
        var old = Path.Combine(_tmp, "old-name");   // does not exist any more
        var fresh = Folder("new-name");
        SaveConfig(old, "old-name");
        _state.AddProject(old);
        _state.Favorite = old;
        _state.AddRecent(StateStore.Key(old), ".", "dotnet test");

        var r = ProjectRelocation.Relocate(_state, old, fresh);

        Assert.True(r.Ok, r.Message);
        var moved = new ConfigStore(fresh, _state.ConfigFolder(StateStore.Key(fresh)));
        Assert.True(moved.Exists);
        moved.Load();
        Assert.Equal("new-name", moved.Config.Name);
        Assert.Equal("http://localhost:5000", moved.Config.Api!.Url);
        Assert.Single(_state.ReadRecent(StateStore.Key(fresh)));
        Assert.Empty(_state.ReadRecent(StateStore.Key(old)));
        Assert.Equal(new[] { fresh }, _state.ReadProjects());
        Assert.Equal(fresh, _state.Favorite);
    }

    [Fact]
    public void Relocate_rejects_a_missing_destination()
    {
        var r = ProjectRelocation.Relocate(_state, Path.Combine(_tmp, "a"), Path.Combine(_tmp, "nope"));
        Assert.False(r.Ok);
    }

    [Fact]
    public void CopyConfig_works_from_a_folder_that_no_longer_exists_and_respects_overwrite()
    {
        var gone = Path.Combine(_tmp, "gone");
        var target = Folder("target");
        SaveConfig(gone, "gone");

        var first = ProjectRelocation.CopyConfig(_state, gone, target, overwrite: false);
        Assert.True(first.Ok, first.Message);
        var copy = new ConfigStore(target, _state.ConfigFolder(StateStore.Key(target)));
        copy.Load();
        Assert.Equal("target", copy.Config.Name);

        Assert.False(ProjectRelocation.CopyConfig(_state, gone, target, overwrite: false).Ok);
        Assert.True(ProjectRelocation.CopyConfig(_state, gone, target, overwrite: true).Ok);
    }
}
