using Rta.Core.Calibration;
using Rta.Core.Dsp;
using Rta.Core.Metering;
using Rta.Core.Signals;

namespace Rta.Core.Tests;

public class LevelAggregatorTests
{
    private static readonly IReadOnlyList<OctaveBand> Bands = new ThirdOctaveBank(48000).Bands;

    private static MeterBlock Block(DateTimeOffset end, double db, double peakDb = double.NaN) => new(
        end, 0.1,
        Db.ToMeanSquare(db), Db.ToMeanSquare(db), Db.ToMeanSquare(db),
        0, Math.Pow(10, (double.IsNaN(peakDb) ? db + 3 : peakDb) / 20),
        Db.ToMeanSquare(db), Db.ToMeanSquare(db), Db.ToMeanSquare(db),
        Db.ToMeanSquare(db), Db.ToMeanSquare(db), Db.ToMeanSquare(db),
        Enumerable.Repeat(Db.ToMeanSquare(db - 15), Bands.Count).ToArray(), false);

    [Fact]
    public void Leq_windows_energy_average_and_calibration_offsets_apply()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var agg = new LevelAggregator(Bands, t0);
        // 15 minutes at -40, then 30 s at -20 and 30 s at -30
        var t = t0;
        for (var i = 0; i < 14 * 600; i++) agg.Add(Block(t = t.AddSeconds(0.1), -40));
        for (var i = 0; i < 300; i++) agg.Add(Block(t = t.AddSeconds(0.1), -20));
        for (var i = 0; i < 300; i++) agg.Add(Block(t = t.AddSeconds(0.1), -30));

        var raw = agg.Snapshot(null)!;
        Assert.False(raw.Calibrated);
        var expected1m = 10 * Math.Log10((0.01 + 0.001) / 2);
        Assert.InRange(raw.LCeq1m, expected1m - 0.01, expected1m + 0.01);
        Assert.InRange(raw.Coverage1mSeconds, 59.9, 60.01);
        var expected15m = 10 * Math.Log10((14 * 60 * 1e-4 + 30 * 0.01 + 30 * 0.001) / 900);
        Assert.InRange(raw.LCeq15m, expected15m - 0.02, expected15m + 0.02);
        Assert.InRange(raw.LCpeakMax1m, -17.01, -16.99);
        Assert.InRange(raw.CMinusA, -0.001, 0.001);

        var profile = new CalibrationProfile { Name = "x", OffsetDb = 120 };
        var cal = agg.Snapshot(profile)!;
        Assert.True(cal.Calibrated);
        Assert.InRange(cal.LCeq1m - raw.LCeq1m, 119.999, 120.001);
        Assert.InRange(cal.LCS, 89.99, 90.01);
    }

    [Fact]
    public void History_is_trimmed_to_15_minutes_and_session_resets()
    {
        var t = DateTimeOffset.UnixEpoch;
        var agg = new LevelAggregator(Bands, t);
        for (var i = 0; i < 20 * 600; i++) agg.Add(Block(t = t.AddSeconds(0.1), -20));
        var s = agg.Snapshot(null)!;
        Assert.InRange(s.Coverage15mSeconds, 899.8, 900.01);
        Assert.InRange(s.SessionSeconds, 1199.9, 1200.1);

        agg.ResetSession(t);
        agg.Add(Block(t = t.AddSeconds(0.1), -30));
        s = agg.Snapshot(null)!;
        Assert.InRange(s.SessionSeconds, 0.09, 0.11);
        Assert.InRange(s.LCeqSession, -30.01, -29.99);
    }

    [Fact]
    public void Correction_curve_is_subtracted_from_bands()
    {
        var t = DateTimeOffset.UnixEpoch;
        var agg = new LevelAggregator(Bands, t);
        agg.Add(Block(t.AddSeconds(0.1), -20));
        var profile = new CalibrationProfile
        {
            OffsetDb = 100,
            Correction = [new CorrectionPoint(20, 0), new CorrectionPoint(1000, 0), new CorrectionPoint(10000, 3)],
        };
        var s = agg.Snapshot(profile)!;
        var k1 = Bands.ToList().FindIndex(b => b.NominalHz == 1000);
        var k10 = Bands.ToList().FindIndex(b => b.NominalHz == 10000);
        Assert.InRange(s.Bands[k1], 64.99, 65.01);
        Assert.InRange(s.Bands[k10], 61.95, 62.05);
    }
}

public class CalibrationTests
{
    private static (List<MeterBlock> blocks, IReadOnlyList<OctaveBand> bands) Capture(SignalGenerator gen, double seconds, double fs = 48000)
    {
        var t = DateTimeOffset.UnixEpoch;
        var meter = new ChannelMeter(fs, 0.1, () => t = t.AddSeconds(0.1));
        var blocks = new List<MeterBlock>();
        meter.BlockReady += blocks.Add;
        var buf = new float[480];
        for (var i = 0; i < seconds * 100; i++) { gen.Fill(buf); meter.Process(buf); }
        return (blocks.Skip(10).ToList(), meter.Bands); // drop filter settling
    }

    [Fact]
    public void Calibrator_tone_gives_expected_offset()
    {
        var (blocks, bands) = Capture(new SignalGenerator(48000) { FrequencyHz = 1000, LevelDbfs = -26 }, 6);
        var r = CalibrationCalculator.FromCalibrator(blocks, bands, 94);
        Assert.InRange(r.OffsetDb, 119.95, 120.05);
        Assert.Equal(1000, r.DominantBandHz);
        Assert.True(r.Ok, string.Join("; ", r.Warnings));
    }

    [Fact]
    public void Calibrator_warns_when_tone_is_wrong_frequency()
    {
        var (blocks, bands) = Capture(new SignalGenerator(48000) { FrequencyHz = 250, LevelDbfs = -26 }, 6);
        var r = CalibrationCalculator.FromCalibrator(blocks, bands, 94);
        Assert.Contains(r.Warnings, w => w.Contains("Strongest band"));
    }

    [Fact]
    public void Calibrator_warns_on_short_capture_and_clipping()
    {
        var (blocks, bands) = Capture(new SignalGenerator(48000) { FrequencyHz = 1000, LevelDbfs = 0 }, 2);
        var r = CalibrationCalculator.FromCalibrator(blocks, bands, 94);
        Assert.Contains(r.Warnings, w => w.Contains("clipped"));
        Assert.Contains(r.Warnings, w => w.Contains("only"));
    }

    [Fact]
    public void Meter_match_uses_selected_weighting()
    {
        var (blocks, bands) = Capture(new SignalGenerator(48000, TestSignalKind.PinkNoise) { LevelDbfs = -30 }, 8);
        var c = CalibrationCalculator.FromMeterMatch(blocks, bands, 85, Weighting.C);
        var a = CalibrationCalculator.FromMeterMatch(blocks, bands, 85, Weighting.A);
        var cLeq = Db.FromMeanSquare(blocks.Sum(b => b.MsC) / blocks.Count);
        Assert.InRange(c.OffsetDb, 85 - cLeq - 0.01, 85 - cLeq + 0.01);
        Assert.NotEqual(Math.Round(a.OffsetDb, 2), Math.Round(c.OffsetDb, 2));
    }

    [Fact]
    public void Profile_mismatch_warnings_and_match_scores()
    {
        var p = new CalibrationProfile { Name = "dbx #1", MicName = "dbx RTA-M #1", ChannelIndex = 0, DeviceName = "Focusrite USB ASIO" };
        Assert.Empty(CalibrationChecks.Warnings(p, new InputContext("Focusrite USB ASIO", 0, "dbx RTA-M #1", 48000)));
        var w = CalibrationChecks.Warnings(p, new InputContext("Focusrite USB ASIO", 2, "ECM8000", 48000));
        Assert.Equal(2, w.Count);
        Assert.Single(CalibrationChecks.Warnings(null, new InputContext(null, 0, null, 48000)));
        Assert.Equal(7, CalibrationChecks.MatchScore(p, new InputContext("Focusrite USB ASIO", 0, "dbx rta-m #1", 48000)));
    }

    [Fact]
    public void Correction_file_parsing_and_interpolation()
    {
        const string text = "\"Sens Factor =-1.2dB, SERNO: 7001234\"\n* comment\n10\t-3.0\n100 0.0\n1000,0.5,12\n10000 2.5\nbad line\n";
        var curve = CorrectionCurve.Parse(text);
        Assert.Equal(4, curve.Count);
        Assert.Equal(-3.0, CorrectionCurve.InterpolateDb(curve, 5));
        Assert.Equal(2.5, CorrectionCurve.InterpolateDb(curve, 20000));
        Assert.InRange(CorrectionCurve.InterpolateDb(curve, Math.Sqrt(1000 * 10000)), 1.49, 1.51);
    }

    [Fact]
    public void Store_round_trips_and_rejects_path_tricks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rta-cal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CalibrationStore(dir);
            var saved = store.Save(new CalibrationProfile { Name = "Mic A", OffsetDb = 121.3, Method = CalibrationMethod.Calibrator, ReferenceWeighting = Weighting.Z });
            var exported = store.Export(saved.Id);
            Assert.True(store.Delete(saved.Id));
            var imported = store.Import(exported);
            Assert.Equal(saved, imported with { Correction = saved.Correction });
            Assert.Null(store.Get("..\\..\\evil"));
            Assert.False(store.Delete("../x"));
            var weird = store.Save(new CalibrationProfile { Id = "../../x", Name = "w" });
            Assert.NotEqual("../../x", weird.Id);
        }
        finally { Directory.Delete(dir, true); }
    }
}
