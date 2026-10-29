using StackDev.Core.Android;
using Xunit;

namespace StackDev.Core.Tests;

public class MdnsTests
{
    [Fact]
    public void Parses_connect_and_pairing_services()
    {
        var text = "List of discovered mdns services\r\n" +
                   "adb-R5CT1234-AbCdEf\t_adb-tls-connect._tcp.\t192.168.1.20:41234\r\n" +
                   "adb-R5CT1234-AbCdEf\t_adb-tls-pairing._tcp\t192.168.1.20:37001\r\n" +
                   "something else\r\n";
        var list = AdbClient.ParseMdns(text);
        Assert.Equal(2, list.Count);
        Assert.False(list[0].Pairing);
        Assert.Equal("192.168.1.20:41234", list[0].Address);
        Assert.True(list[1].Pairing);
    }

    [Fact]
    public void Empty_output_gives_no_services() => Assert.Empty(AdbClient.ParseMdns("List of discovered mdns services\n"));
}
