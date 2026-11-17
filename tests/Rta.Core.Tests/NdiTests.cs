using Rta.App.Ndi;

namespace Rta.Core.Tests;

/// <summary>Round-trip through the real NDI runtime. Skipped (passes trivially) where NDI isn't installed.</summary>
public class NdiTests
{
    [Fact]
    public void Sender_is_discoverable_and_frames_round_trip()
    {
        if (!NdiRuntime.Available) return;
        var name = "RTA unit test " + Guid.NewGuid().ToString("N")[..6];
        using var sender = new NdiSender(name);

        const int w = 64, h = 36;
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = 255; px[i + 3] = 255; } // blue, opaque
        var audio = new float[2 * 480];
        using var cts = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                sender.SendBgra(px, w, h, w * 4, 10);
                sender.SendAudio(audio, 2, 480, 48000);
                sender.SendMetadata("<rta lc=\"84.2\"/>");
                await Task.Delay(100);
            }
        });
        try
        {
            var found = NdiTools.FindSources(4000).FirstOrDefault(s => s.Name.EndsWith($"({name})"));
            Assert.NotNull(found);
            var probe = NdiTools.Probe(found!.Name, 6000);
            Assert.True(probe.Connected, probe.Message);
            Assert.Equal(w, probe.Width);
            Assert.Equal(h, probe.Height);
            Assert.Contains("rta", probe.Metadata ?? "");
        }
        finally
        {
            cts.Cancel();
            pump.Wait(1000);
        }
    }
}

public class TileRendererTests
{
    private static Rta.App.Ndi.TileData Tile(string name, double? lcs, string? alert, bool calibrated = true) =>
        new(name, "Booth PC", calibrated ? "calibrated" : "UNCAL dBFS", calibrated ? Rta.App.Ndi.TileTone.Ok : Rta.App.Ndi.TileTone.Warn,
            lcs, lcs - 3, lcs - 1, lcs - 2, lcs - 1.5, Enumerable.Range(0, 31).Select(i => 70.0 - Math.Abs(i - 12) * 1.5).ToArray(),
            98, 103, alert, lcs is not null, calibrated);

    [Fact]
    public void Tiles_and_dashboard_render_without_errors()
    {
        SkiaSharp.SKBitmap? tile = null, dash = null;
        Rta.App.Ndi.TileRenderer.Ensure(ref tile, 640, 360);
        Rta.App.Ndi.TileRenderer.DrawTileFrame(tile!, Tile("Front of house 😀", 99.4, "warn"));
        Rta.App.Ndi.TileRenderer.Ensure(ref dash, 1920, 1080);
        Rta.App.Ndi.TileRenderer.DrawDashboard(dash!, new Rta.App.Ndi.DashboardData("Main Hub", "Sunday 11am", DateTimeOffset.Now.AddMinutes(-20),
            [Tile("Mic 1", 94.2, null), Tile("Mic 2", 104.6, "alarm"), Tile("Balcony", 88.1, null), Tile("Rear", null, null), Tile("Uncal", -31.5, null, false)],
            ["ALARM: Mic 2 104.6 (limit 103)"]));
        // Something was drawn: the tile isn't a single flat colour.
        Assert.True(tile!.Pixels.Distinct().Count() > 10);
        Assert.True(dash!.Pixels.Distinct().Count() > 10);
        if (Environment.GetEnvironmentVariable("RTA_REPORT_PREVIEW") is { Length: > 0 } dir)
        {
            File.WriteAllBytes(Path.Combine(dir, "ndi-tile.png"), tile.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90).ToArray());
            File.WriteAllBytes(Path.Combine(dir, "ndi-dashboard.png"), dash.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90).ToArray());
        }
        tile.Dispose(); dash.Dispose();
    }

    [Fact]
    public void Bandwidth_estimates_and_warnings()
    {
        Assert.InRange(Rta.App.Ndi.NdiBandwidth.Video(1920, 1080, 60), 124, 126);
        Assert.InRange(Rta.App.Ndi.NdiBandwidth.Audio(1, 48000), 1.5, 1.6);
        var many = Enumerable.Range(0, 10).Select(i => new Rta.App.Ndi.NdiSenderStatus($"s{i}", "meter", 0, 12)).ToList();
        Assert.Equal(2, Rta.App.Ndi.NdiBandwidth.Warnings(many, 120).Count);
        Assert.Empty(Rta.App.Ndi.NdiBandwidth.Warnings(many.Take(2).ToList(), 5));
    }
}
