using Microsoft.Extensions.Logging.Abstractions;
using Rta.App.Hub;
using Rta.Core.Dsp;
using Rta.Core.Protocol;

namespace Rta.Core.Tests;

public class PlanStoreTests
{
    private static PlanZone Zone(int id, string name) => new() { Id = id, Name = name, Points = [new(0, 0), new(4, 0), new(4, 4)] };

    [Fact]
    public void New_zones_get_hub_wide_ids_and_existing_ids_are_kept()
    {
        using var tmp = new TempData();
        var store = new PlanStore(tmp.Paths);
        var saved = store.Save(new Plan { Floors = [new Floor { Zones = [Zone(0, "A"), Zone(-5, "B")] }, new Floor { Name = "Balcony", Zones = [Zone(0, "C")] }] });
        var ids = saved.AllZones.Select(z => z.Id).ToList();
        Assert.Equal(3, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(id > 0));

        var a = saved.AllZones.Single(z => z.Name == "A");
        var again = store.Save(saved with { Floors = [saved.Floors[0] with { Zones = [a, Zone(0, "D")] }] });
        Assert.Equal(a.Id, again.AllZones.Single(z => z.Name == "A").Id);
        Assert.True(again.AllZones.Single(z => z.Name == "D").Id > ids.Max());   // never reuses ids
        var json = (Plan p) => System.Text.Json.JsonSerializer.Serialize(p, Rta.Core.Calibration.CalibrationStore.Json);
        Assert.Equal(json(again), json(new PlanStore(tmp.Paths).Current));     // persisted
    }

    [Theory]
    [InlineData("zone-too-small")]
    [InlineData("duplicate-mic")]
    [InlineData("bad-size")]
    public void Invalid_plans_are_rejected(string kind)
    {
        using var tmp = new TempData();
        var store = new PlanStore(tmp.Paths);
        var plan = kind switch
        {
            "zone-too-small" => new Plan { Floors = [new Floor { Zones = [new PlanZone { Name = "x", Points = [new(0, 0), new(1, 1)] }] }] },
            "duplicate-mic" => new Plan { Floors = [new Floor { Mics = [new("n:0", 1, 1)] }, new Floor { Name = "B", Mics = [new("n:0", 2, 2)] }] },
            _ => new Plan { Floors = [new Floor { Width = 0 }] },
        };
        Assert.Throws<ArgumentException>(() => store.Save(plan));
    }

    [Fact]
    public void Only_real_images_are_accepted()
    {
        using var tmp = new TempData();
        var store = new PlanStore(tmp.Paths);
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
        var id = store.SaveImage(png);
        Assert.EndsWith(".png", id);
        Assert.Equal("image/png", store.GetImage(id)!.Value.ContentType);
        Assert.Throws<ArgumentException>(() => store.SaveImage("<svg onload=alert(1)>"u8.ToArray()));
        Assert.Null(store.GetImage("../hub.db"));
        Assert.Null(store.GetImage("x.svg"));
    }
}

public class ZoneEngineTests
{
    private static readonly double[] Hz = ThirdOctaveBank.NominalCenters;

    private sealed class Rig : IDisposable
    {
        public TempData Tmp = new();
        public HubStore Store;
        public HubState State;
        public PlanStore Plan;
        public ZoneEngine Engine;
        public NodeConnection Conn;
        public Hello Hello;

        public Rig()
        {
            Store = new HubStore(Tmp.Paths);
            var settings = new HubSettingsStore(Tmp.Paths);
            State = new HubState(Store, settings);
            Plan = new PlanStore(Tmp.Paths);
            Hello = new Hello("n", "Node", Proto.Version, "1", "b", 0, 48000, Hz,
            [
                new MicInfo(0, "Front mic", null, "p", null, null, true, 120, "prof-1"),
                new MicInfo(1, "Rover", null, "p", null, null, true, 118, "prof-2"),
            ]);
            Store.UpsertNode(Hello, null, NodeAcceptance.Accepted);
            Conn = new NodeConnection("n", null, _ => Task.CompletedTask) { Hello = Hello };
            State.Register(Conn);
            Plan.Save(new Plan
            {
                Floors = [new Floor
                {
                    Zones = [new PlanZone { Name = "Back", Points = [new(0, 10), new(10, 10), new(10, 20), new(0, 20)] }],
                    Mics = [new("n:0", 15, 2, MicRole.Permanent), new("n:1", 5, 15, MicRole.Roving)],
                }],
            });
            Engine = new ZoneEngine(Store, State, Plan, NullLogger<ZoneEngine>.Instance);
        }

        /// <summary>Feeds one live message: ref at <paramref name="refDb"/> flat, rover 6 dB lower in the highs.</summary>
        public void Feed(double refDb)
        {
            var refBands = Hz.Select(_ => refDb - 15).ToArray();
            var roverBands = Hz.Select((_, i) => refDb - 15 - (Hz[i] < 250 ? 2 : 6)).ToArray();
            LiveMic M(int ch, double[] b) => new(ch, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 60, true, false, b, [],
                new IntervalLevels(0.2, ZoneModel_Sum(b, Weighting.A), ZoneModel_Sum(b, Weighting.C), ZoneModel_Sum(b, Weighting.Z), b));
            Engine.OnLive("n", new LiveUpdate(0, true, null, [M(0, refBands), M(1, roverBands)], []));
        }

        public void Dispose() { Store.Dispose(); Tmp.Dispose(); }
    }

    private static double ZoneModel_Sum(double[] b, Weighting w) => Rta.Core.Zones.ZoneModel.BandSum(b, Hz, w);

    [Fact]
    public void Survey_then_prediction_logging_and_stale_flags()
    {
        using var rig = new Rig();
        var zoneId = rig.Plan.Current.AllZones.Single().Id;

        rig.Feed(90);
        var st = rig.Engine.StartSurvey(zoneId, "n:1", 10);
        Assert.Equal("Back", st.ZoneName);
        Assert.Throws<InvalidOperationException>(() => rig.Engine.StartSurvey(zoneId, "n:1", 10)); // one at a time
        for (var i = 0; i < 30; i++) rig.Feed(90 + i % 5);                                     // 6 s of data

        var status = rig.Engine.SurveyStatus()!;
        Assert.Single(status.Refs);
        Assert.InRange(status.Seconds, 5.9, 6.1);
        var row = rig.Engine.StopSurvey(save: true, note: "test")!;
        Assert.Equal(120, row.Measurement.Refs[0].OffsetDbAtSurvey);     // ref calibration recorded
        Assert.Equal("prof-1", row.Measurement.Refs[0].ProfileIdAtSurvey);
        Assert.True(row.RefPositions.ContainsKey("n:0"));

        // Prediction follows the reference: zone = ref − 2 dB (lows) / − 6 dB (highs).
        rig.Feed(95); rig.Feed(95); rig.Feed(95); rig.Feed(95); rig.Feed(95); rig.Feed(95);
        var v = Assert.Single(rig.Engine.Views());
        Assert.True(v.Surveyed);
        Assert.False(v.Stale);
        Assert.NotNull(v.LC1s);
        var refC = ZoneModel_Sum(Hz.Select(_ => 80.0).ToArray(), Weighting.C);
        Assert.InRange(refC - v.LC1s!.Value, 4, 6.5);   // mostly the −6 dB highs, a little of the −2 dB lows

        // Per-second logging writes the zone as a virtual mic.
        var sec = (long)Math.Floor(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0) - 1;
        rig.Engine.LogSecond(sec);
        rig.Engine.LogSecond(sec + 1);
        var series = rig.Store.GetSeries(HubStore.ZoneKey(zoneId), sec - 5, sec + 5);
        Assert.NotEmpty(series.Ts);

        // Moving the reference mic on the plan flags the zone.
        var plan = rig.Plan.Current;
        rig.Plan.Save(plan with { Floors = [plan.Floors[0] with { Mics = [new("n:0", 20, 2, MicRole.Permanent), plan.Floors[0].Mics[1]] }] });
        System.Threading.Thread.Sleep(450); // views are cached for 400 ms
        var moved = Assert.Single(rig.Engine.Views());
        Assert.True(moved.Stale);
        Assert.Contains(moved.Issues, i => i.Contains("moved"));
    }

    [Fact]
    public void Survey_needs_permanent_mics_and_live_rover()
    {
        using var rig = new Rig();
        var zoneId = rig.Plan.Current.AllZones.Single().Id;
        Assert.Throws<ArgumentException>(() => rig.Engine.StartSurvey(zoneId, "n:1", 60)); // no live data yet
        rig.Feed(90);
        Assert.Throws<ArgumentException>(() => rig.Engine.StartSurvey(999, "n:1", 60));     // unknown zone
        Assert.Throws<InvalidOperationException>(() => rig.Engine.StopSurvey(true, null));   // nothing running
        rig.Engine.StartSurvey(zoneId, "n:1", 60);
        Assert.Null(rig.Engine.StopSurvey(save: false, note: null));                         // discard
        Assert.Empty(rig.Store.ListSurveys());
    }
}
