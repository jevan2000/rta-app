using System.Net;
using Rta.App.Monitor;

namespace Rta.Core.Tests;

public class MonitorSearchTests
{
    [Fact]
    public void A_24_is_searched_whole_without_this_pc()
    {
        var r = MonitorService.SearchRange(IPAddress.Parse("10.10.12.204"), 24).Select(a => a.ToString()).ToList();
        Assert.Equal(253, r.Count);
        Assert.Equal("10.10.12.1", r[0]);
        Assert.Equal("10.10.12.254", r[^1]);
        Assert.DoesNotContain("10.10.12.204", r);
    }

    [Fact]
    public void Bigger_networks_are_searched_around_this_pc()
    {
        var r = MonitorService.SearchRange(IPAddress.Parse("10.10.12.204"), 16).Select(a => a.ToString()).ToList();
        Assert.Equal(1021, r.Count);                       // the /22 around this PC
        Assert.Equal("10.10.12.1", r[0]);
        Assert.Equal("10.10.15.254", r[^1]);
        Assert.Contains("10.10.13.7", r);
    }
}
