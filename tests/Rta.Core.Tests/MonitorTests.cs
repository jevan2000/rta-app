using Rta.App;
using Rta.App.Monitor;

namespace Rta.Core.Tests;

public class MonitorTests
{
    [Theory]
    [InlineData("192.168.1.20", "http://192.168.1.20:5180")]
    [InlineData("192.168.1.20:5200", "http://192.168.1.20:5200")]
    [InlineData("http://hub-pc:5180/", "http://hub-pc:5180")]
    [InlineData("hub-pc", "http://hub-pc:5180")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void Hub_addresses_are_normalised(string input, string? expected) => Assert.Equal(expected, MonitorService.HubBase(input));

    [Fact]
    public void A_monitor_serves_the_dashboard_but_runs_no_node_or_hub()
    {
        var m = new AppMode(AppMode.Parse("monitor"), 5180);
        Assert.True(m.IsMonitor);
        Assert.True(m.ServesDashboard);
        Assert.False(m.RunsHub);
        Assert.False(m.RunsNode);
        Assert.False(new AppMode(AppRole.Node, 5180).ServesDashboard);
    }

    [Fact]
    public void A_monitor_opens_the_dashboard_at_sign_in_by_default()
    {
        using var tmp = new TempData();
        Assert.Equal("/", new PcSettingsStore(tmp.Paths, new AppMode(AppRole.Monitor, 5180)).StartupPath());
        Assert.Null(new PcSettingsStore(tmp.Paths, new AppMode(AppRole.Hub, 5180)).StartupPath());
    }
}
