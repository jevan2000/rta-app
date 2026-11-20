using Microsoft.Extensions.Logging.Abstractions;
using Rta.App.Node;
using Rta.Audio;
using Rta.Core.Calibration;

namespace Rta.Core.Tests;

public class MultiDeviceTests
{
    private static DeviceConfig Dev(int slot, AudioBackend b, string id) => new() { Slot = slot, Backend = b, DeviceId = id };

    [Fact]
    public void Input_numbers_keep_the_first_interface_unchanged()
    {
        Assert.Equal("3", InputNumber.Label(2));
        Assert.Equal("2·3", InputNumber.Label(InputNumber.Of(1, 2)));
        Assert.Equal(102, InputNumber.Of(1, 2));
        Assert.Equal((1, 2), (InputNumber.Slot(102), InputNumber.DeviceChannel(102)));
    }

    [Fact]
    public void Settings_from_1_0_become_device_zero_and_bad_combinations_are_refused()
    {
        var old = new NodeConfig { Backend = AudioBackend.Asio, DeviceId = "Focusrite USB ASIO" }.Normalized();
        Assert.Equal(Dev(0, AudioBackend.Asio, "Focusrite USB ASIO"), Assert.Single(old.Devices));
        Assert.Null(old.DeviceProblem());

        var two = old with { Devices = [.. old.Devices, Dev(1, AudioBackend.Wasapi, "{behringer}")] };
        Assert.Null(two.DeviceProblem());
        Assert.Contains("ASIO", (old with { Devices = [.. old.Devices, Dev(1, AudioBackend.Asio, "UMC ASIO Driver")] }).DeviceProblem());
        Assert.Contains("twice", (old with { Devices = [.. old.Devices, Dev(1, AudioBackend.Asio, "focusrite usb asio")] }).DeviceProblem());
        Assert.Contains("slot", (old with { Devices = [.. old.Devices, Dev(0, AudioBackend.Wasapi, "x")] }).DeviceProblem());

        // The first device is mirrored into the 1.0.x fields, whichever slot it is.
        var moved = new NodeConfig { Devices = [Dev(3, AudioBackend.Wasapi, "b"), Dev(1, AudioBackend.TestSignal, TestSignalInput.DeviceId)] }.Normalized();
        Assert.Equal([1, 3], moved.Devices.Select(d => d.Slot));
        Assert.Equal(AudioBackend.TestSignal, moved.Backend);
    }

    [Theory]
    [InlineData("Microphone (2- UMC404HD 192k)", "Microphone (UMC404HD 192k)", true)]
    [InlineData("Line In (3- Focusrite USB Audio)", "Line In (Focusrite USB Audio)", true)]
    [InlineData("2- Scarlett 4i4 USB", "Scarlett 4i4 USB", true)]
    [InlineData("Microphone (UMC404HD 192k)", "Microphone (Realtek(R) Audio)", false)]
    [InlineData("Microphone (UMC404HD 192k)", "Line In (UMC404HD 192k)", false)]
    [InlineData(null, "Microphone (UMC404HD 192k)", false)]
    public void A_renumbered_interface_is_the_same_model(string? a, string b, bool same) =>
        Assert.Equal(same, Rta.Audio.DeviceMatch.SameModel(a, b));

    private static MeteringService Service(TempData tmp, NodeConfig cfg)
    {
        var store = new NodeConfigStore(tmp.Paths);
        store.Save(cfg with { NodeId = "n1", AutoStart = false });
        return new MeteringService(store, new CalibrationStore(tmp.Paths.CalibrationDir), NullLogger<MeteringService>.Instance);
    }

    private static async Task<IReadOnlyList<ChannelStatus>> Levels(MeteringService m, int count)
    {
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(100);
            var ch = m.Channels();
            if (ch.Count == count && ch.All(c => c.Levels is { LCS: > -100 })) { await Task.Delay(1500); return m.Channels(); }
        }
        return m.Channels();
    }

    [Fact]
    public async Task Two_interfaces_are_metered_side_by_side()
    {
        using var tmp = new TempData();
        var m = Service(tmp, new NodeConfig
        {
            Devices = [Dev(0, AudioBackend.TestSignal, TestSignalInput.DeviceId), Dev(1, AudioBackend.TestSignal, TestSignalInput.SecondDeviceId)],
        });
        try
        {
            m.Start();
            var ch = await Levels(m, 8);
            Assert.Equal([0, 1, 2, 3, 100, 101, 102, 103], ch.Select(c => c.ChannelIndex));
            Assert.Equal("2·1", ch.Single(c => c.ChannelIndex == 100).Label);
            Assert.Equal("Test signal 2 (virtual)", ch.Single(c => c.ChannelIndex == 100).DeviceName);
            Assert.StartsWith("Test", m.Config.Inputs.Single(i => i.ChannelIndex == 100).Name);   // default names per interface
            var front = ch.Single(c => c.ChannelIndex == 0).Levels!.LCS;
            var second = ch.Single(c => c.ChannelIndex == 100).Levels!.LCS;
            Assert.InRange(front - second, 2, 6);                                                 // second interface is 4 dB quieter

            var st = m.Status();
            Assert.True(st.Running);
            Assert.Null(st.Error);
            Assert.Equal(2, st.Devices.Count(d => d.Running));
            Assert.Equal(8, st.DeviceChannels);
            // Each interface's name is remembered, so a moved one can be recognised later.
            Assert.Equal(["Test signal (virtual)", "Test signal 2 (virtual)"], m.Config.Devices.Select(d => d.DeviceName));
            Assert.Equal(["Test signal (virtual)", "Test signal 2 (virtual)"], new NodeConfigStore(tmp.Paths).Load().Devices.Select(d => d.DeviceName));
        }
        finally { m.Stop(); }
    }

    [Fact]
    public async Task One_interface_failing_leaves_the_others_running()
    {
        using var tmp = new TempData();
        var m = Service(tmp, new NodeConfig
        {
            Devices = [Dev(0, AudioBackend.TestSignal, TestSignalInput.DeviceId), Dev(1, AudioBackend.Wasapi, "{no-such-device}")],
        });
        try
        {
            m.Start();                                     // doesn't throw: one interface is up
            var ch = await Levels(m, 4);
            Assert.All(ch, c => Assert.Equal(0, InputNumber.Slot(c.ChannelIndex)));
            var st = m.Status();
            Assert.True(st.Running);
            Assert.Contains("no-such-device", st.Error);
            Assert.False(st.Devices.Single(d => d.Slot == 1).Running);

            // Settings with two ASIO drivers are refused before anything restarts.
            var bad = m.Config with { Devices = [Dev(0, AudioBackend.Asio, "A"), Dev(1, AudioBackend.Asio, "B")] };
            Assert.Throws<ArgumentException>(() => m.UpdateConfig(bad));
            Assert.Equal(AudioBackend.TestSignal, m.Config.Devices[0].Backend);
        }
        finally { m.Stop(); }
    }

    [Fact]
    public void Nothing_starting_is_an_error()
    {
        using var tmp = new TempData();
        var m = Service(tmp, new NodeConfig { Devices = [Dev(0, AudioBackend.Wasapi, "{no-such-device}")] });
        Assert.Throws<InvalidOperationException>(m.Start);
        Assert.False(m.Status().Running);
        Assert.Contains("no-such-device", m.Status().Error);
    }
}
