using StackDev.Core.Credentials;
using Xunit;

namespace StackDev.Core.Tests;

public class CredentialsOrderTests
{
    private static List<string> L(params string[] x) => x.ToList();

    [Theory]
    [InlineData(0, 3, "b,c,a,d")]   // first one dropped before the fourth
    [InlineData(0, 4, "b,c,d,a")]   // dropped at the end
    [InlineData(3, 0, "d,a,b,c")]   // last one dropped at the very top
    [InlineData(2, 1, "a,c,b,d")]   // one step up
    [InlineData(1, 3, "a,c,b,d")]   // one step down
    public void Moves_an_item_to_the_gap_it_is_dropped_on(int from, int slot, string expected)
    {
        var list = L("a", "b", "c", "d");
        Assert.True(CredentialsStore.Reorder(list, from, slot));
        Assert.Equal(expected, string.Join(',', list));
    }

    [Theory]
    [InlineData(1, 1)]    // dropped before itself
    [InlineData(1, 2)]    // dropped right after itself: same place
    [InlineData(-1, 0)]
    [InlineData(4, 0)]
    [InlineData(0, 5)]
    [InlineData(0, -1)]
    public void Leaves_the_list_alone_when_nothing_would_change(int from, int slot)
    {
        var list = L("a", "b", "c", "d");
        Assert.False(CredentialsStore.Reorder(list, from, slot));
        Assert.Equal("a,b,c,d", string.Join(',', list));
    }

    [Fact]
    public void The_new_order_is_saved_and_read_back()
    {
        using var t = new TempFolder();
        var store = new CredentialsStore(t.FilePath);
        store.Save(new[] { new Credential { Name = "one" }, new Credential { Name = "two" }, new Credential { Name = "three" } });

        Assert.True(store.Move(2, 0));
        Assert.Equal(new[] { "three", "one", "two" }, store.Read().Select(c => c.Name));
        Assert.False(store.Move(0, 1));   // already there: nothing is rewritten
    }
}
