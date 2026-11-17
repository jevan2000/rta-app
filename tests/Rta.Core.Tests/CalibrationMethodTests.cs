using Rta.Core.Calibration;
using Rta.Core.Dsp;
using Rta.Core.Metering;
using Rta.Core.Signals;

namespace Rta.Core.Tests;

public class CalibrationMethodTests
{
    /// <summary>Captures one or more generators through their own meters, block-aligned like the node.</summary>
    private static (List<MeterBlock>[] blocks, IReadOnlyList<OctaveBand> bands) Capture(double seconds, params SignalGenerator[] gens)
    {
        const double fs = 48000;
        var t = DateTimeOffset.UnixEpoch;
        var meters = gens.Select(_ => new ChannelMeter(fs, 0.1, () => t.AddSeconds(1))).ToArray();
        var blocks = gens.Select(_ => new List<MeterBlock>()).ToArray();
        for (var i = 0; i < meters.Length; i++) { var k = i; meters[i].BlockReady += blocks[k].Add; }
        var buf = new float[480];
        for (var n = 0; n < seconds * 100; n++)
            for (var i = 0; i < gens.Length; i++) { gens[i].Fill(buf); meters[i].Process(buf); }
        return (blocks.Select(b => b.Skip(10).ToList()).ToArray(), meters[0].Bands);
    }

    [Theory]
    [InlineData(CalibratorClass.Class1, CalibrationQuality.High)]
    [InlineData(CalibratorClass.Class2, CalibrationQuality.High)]
    [InlineData(CalibratorClass.Unknown, CalibrationQuality.Medium)]
    public void Calibrator_quality_follows_calibrator_class(CalibratorClass cls, CalibrationQuality expected)
    {
        var (b, bands) = Capture(5, new SignalGenerator(48000) { FrequencyHz = 1000, LevelDbfs = -26 });
        var r = CalibrationCalculator.FromCalibrator(b[0], bands, 94, 1000, cls);
        Assert.Equal(expected, r.Quality);
        Assert.InRange(r.OffsetDb, 119.95, 120.05);
    }

    [Fact]
    public void Pistonphone_at_250Hz_is_accepted_when_tone_is_set()
    {
        var (b, bands) = Capture(5, new SignalGenerator(48000) { FrequencyHz = 250, LevelDbfs = -20 });
        var r = CalibrationCalculator.FromCalibrator(b[0], bands, 114, 250);
        Assert.True(r.Ok, string.Join("; ", r.Warnings));
        Assert.InRange(r.OffsetDb, 133.95, 134.05);
    }

    [Theory]
    [InlineData(MeterClass.Class1, CalibrationQuality.Medium)]
    [InlineData(MeterClass.Class2, CalibrationQuality.Medium)]
    [InlineData(MeterClass.Consumer, CalibrationQuality.Low)]
    public void Meter_quality_follows_meter_class(MeterClass cls, CalibrationQuality expected)
    {
        var (b, bands) = Capture(5, new SignalGenerator(48000, TestSignalKind.PinkNoise) { LevelDbfs = -30 });
        var r = CalibrationCalculator.FromMeterMatch(b[0], bands, 85, Weighting.C, cls);
        Assert.Equal(expected, r.Quality);
        Assert.Equal(cls == MeterClass.Consumer, (r.Notes ?? []).Count > 0);
    }

    [Theory]
    [InlineData(PhoneKind.IPhone, CalibrationQuality.Low)]
    [InlineData(PhoneKind.Android, CalibrationQuality.VeryLow)]
    public void Phone_app_is_always_unreliable(PhoneKind phone, CalibrationQuality expected)
    {
        var (b, bands) = Capture(5, new SignalGenerator(48000, TestSignalKind.PinkNoise) { LevelDbfs = -30 });
        var r = CalibrationCalculator.FromPhoneApp(b[0], bands, 85, Weighting.A, phone);
        Assert.Equal(expected, r.Quality);
        Assert.True(CalibrationAccuracy.IsUnreliable(r.Quality));
        Assert.Contains(r.Notes!, n => n.Contains("unreliable"));
        var aLeq = Db.FromMeanSquare(b[0].Average(x => x.MsA));
        Assert.InRange(r.OffsetDb, 85 - aLeq - 0.01, 85 - aLeq + 0.01);
    }

    [Fact]
    public void Transfer_copies_reference_level_and_inherits_accuracy()
    {
        // Same pink programme into both "mics"; the target's preamp is 7 dB lower.
        var refGen = new SignalGenerator(48000, TestSignalKind.PinkNoise, seed: 5) { LevelDbfs = -25 };
        var tgtGen = new SignalGenerator(48000, TestSignalKind.PinkNoise, seed: 5) { LevelDbfs = -32 };
        var (b, bands) = Capture(6, tgtGen, refGen);
        var source = new CalibrationProfile { Name = "dbx #1", OffsetDb = 115, UncertaintyDb = 0.6, Quality = CalibrationQuality.High };

        var r = CalibrationCalculator.FromTransfer(b[0], b[1], bands, source, Weighting.C);

        Assert.InRange(r.OffsetDb, 121.95, 122.05);          // reads the same SPL as the reference
        Assert.InRange(r.StabilityDb, 0, 0.05);              // identical programme → no spread
        Assert.InRange(r.UncertaintyDb!.Value, 0.78, 0.8);   // sqrt(0.6² + 0.5²)
        Assert.Equal(CalibrationQuality.Medium, r.Quality);
        Assert.True(r.Ok, string.Join("; ", r.Warnings));
    }

    [Fact]
    public void Transfer_from_unreliable_source_stays_unreliable_and_warns_on_spread()
    {
        var refGen = new SignalGenerator(48000, TestSignalKind.PinkNoise, seed: 1) { LevelDbfs = -25 };
        var tgtGen = new SignalGenerator(48000, TestSignalKind.PinkNoise, seed: 2) { LevelDbfs = -25 };
        var (b, bands) = Capture(6, tgtGen, refGen);
        // make the target's level jump around so the mics "disagree"
        var noisy = b[0].Select((x, i) => (i / 10) % 2 == 0 ? x : x with { MsC = x.MsC * 4 }).ToList();
        var source = new CalibrationProfile { Name = "phone", OffsetDb = 110, UncertaintyDb = 3, Quality = CalibrationQuality.Low };

        var r = CalibrationCalculator.FromTransfer(noisy, b[1], bands, source, Weighting.C);

        Assert.True(CalibrationAccuracy.IsUnreliable(r.Quality));
        Assert.Contains(r.Warnings, w => w.Contains("disagreed"));
        Assert.Contains(r.Notes!, n => n.Contains("unreliable"));
    }

    [Fact]
    public void Transfer_from_unknown_accuracy_source_is_unknown()
    {
        var gen = new SignalGenerator(48000, TestSignalKind.PinkNoise) { LevelDbfs = -25 };
        var (b, bands) = Capture(4, gen);
        var r = CalibrationCalculator.FromTransfer(b[0], b[0], bands, new CalibrationProfile { Name = "manual", OffsetDb = 100 });
        Assert.Equal(CalibrationQuality.Unknown, r.Quality);
        Assert.Null(r.UncertaintyDb);
        Assert.InRange(r.OffsetDb, 99.99, 100.01);
    }

    [Fact]
    public void Spec_estimate_matches_hand_calculation_and_is_very_rough()
    {
        // 10 mV/Pa = -40 dBV/Pa; +9 dBu max in at min gain; +40 dB gain
        // full-scale sine = 9 - 2.2185 - 40 = -33.2185 dBV → SPL = 94 + (-33.2185) - (-40) = 100.78
        // offset = 100.78 + 3.01 = 103.79
        var r = CalibrationCalculator.FromSpecs(10, 9, 40);
        Assert.InRange(r.OffsetDb, 103.78, 103.80);
        Assert.Equal(CalibrationQuality.VeryLow, r.Quality);
        Assert.Null(r.MeasuredDb);
        Assert.Empty(r.Warnings);
        Assert.Throws<ArgumentOutOfRangeException>(() => CalibrationCalculator.FromSpecs(0, 9, 40));
        Assert.NotEmpty(CalibrationCalculator.FromSpecs(500, 9, 120).Warnings);
    }

    [Fact]
    public void Unreliable_profiles_add_a_warning_on_the_input()
    {
        var input = new InputContext(null, 0, null, 48000);
        var phone = new CalibrationProfile { Method = CalibrationMethod.PhoneApp, Quality = CalibrationQuality.Low, UncertaintyDb = 3 };
        Assert.Contains(CalibrationChecks.Warnings(phone, input), w => w.Contains("Unreliable") && w.Contains("phone app") && w.Contains("±3"));
        var good = new CalibrationProfile { Method = CalibrationMethod.Calibrator, Quality = CalibrationQuality.High, UncertaintyDb = 0.6 };
        Assert.Empty(CalibrationChecks.Warnings(good, input));
    }
}
