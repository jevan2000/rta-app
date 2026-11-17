using Rta.Core.Dsp;
using Rta.Core.Protocol;
using Rta.Core.Zones;

namespace Rta.Core.Tests;

public class ZoneModelTests
{
    private static readonly double[] Hz = ThirdOctaveBank.NominalCenters;

    private static double[] Flat(double db) => Enumerable.Repeat(db, Hz.Length).ToArray();

    /// <summary>Zone hears the ref 3 dB down below 200 Hz and 8 dB down above (typical under-balcony seat).</summary>
    private static double[] ZoneFrom(double[] refBands) => refBands.Select((v, i) => v - (Hz[i] < 200 ? 3 : 8)).ToArray();

    private static SurveyMeasurement Survey(double[] refBands, double std = 0.5, int chunks = 12, string key = "n:0", double refOffset = 0)
    {
        var zone = ZoneFrom(refBands);
        return new SurveyMeasurement(zone,
            ZoneModel.BandSum(zone, Hz, Weighting.A) + 0.2, ZoneModel.BandSum(zone, Hz, Weighting.C) + 0.3,
            [new SurveyRef(key, refBands, ZoneModel.BandSum(refBands, Hz, Weighting.A), ZoneModel.BandSum(refBands, Hz, Weighting.C), refOffset, "p1", std, chunks)],
            90, 0.9);
    }

    private static RefReading Reading(double[] bands, double offset = 0) =>
        new(bands, ZoneModel.BandSum(bands, Hz, Weighting.A), ZoneModel.BandSum(bands, Hz, Weighting.C), offset);

    [Fact]
    public void Replaying_the_survey_reproduces_the_rover_reading()
    {
        var refBands = Flat(75);
        var s = Survey(refBands);
        var model = ZoneModel.Build([(s, 1)], Hz);
        Assert.True(model.BandMode);
        var p = ZoneModel.Predict(model, new Dictionary<string, RefReading> { ["n:0"] = Reading(refBands) }, Hz)!;
        Assert.InRange(p.LC, s.RoverLC - 0.01, s.RoverLC + 0.01);
        Assert.InRange(p.LA, s.RoverLA - 0.01, s.RoverLA + 0.01);
    }

    [Fact]
    public void Spectrum_changes_are_followed_band_by_band()
    {
        // Surveyed on flat pink noise; later the mix gets 12 dB more sub/bass.
        var model = ZoneModel.Build([(Survey(Flat(75)), 1)], Hz);
        var bassy = Flat(75).Select((v, i) => Hz[i] <= 125 ? v + 12 : v).ToArray();
        var p = ZoneModel.Predict(model, new Dictionary<string, RefReading> { ["n:0"] = Reading(bassy) }, Hz)!;

        var expectedZone = ZoneFrom(bassy);
        Assert.InRange(p.LC, ZoneModel.BandSum(expectedZone, Hz, Weighting.C) + 0.3 - 0.01, ZoneModel.BandSum(expectedZone, Hz, Weighting.C) + 0.3 + 0.01);
        // A single broadband offset would get this wrong: the zone loses less bass than treble.
        var singleNumber = Reading(bassy).LC + (Survey(Flat(75)).RoverLC - Reading(Flat(75)).LC);
        Assert.True(Math.Abs(p.LC - singleNumber) > 0.5, $"band model {p.LC:0.00} vs single-number {singleNumber:0.00}");
    }

    [Fact]
    public void Recalibrating_a_reference_mic_is_compensated()
    {
        var model = ZoneModel.Build([(Survey(Flat(75), refOffset: 120), 1)], Hz);
        var before = ZoneModel.Predict(model, new Dictionary<string, RefReading> { ["n:0"] = Reading(Flat(75), 120) }, Hz)!;
        // New calibration reads 2 dB higher for the same sound.
        var after = ZoneModel.Predict(model, new Dictionary<string, RefReading> { ["n:0"] = Reading(Flat(77), 122) }, Hz)!;
        Assert.InRange(after.LC - before.LC, -0.01, 0.01);
        Assert.True(after.CalibrationCompensated);
        Assert.False(before.CalibrationCompensated);
    }

    [Fact]
    public void Steadier_reference_gets_more_weight_and_missing_refs_are_skipped()
    {
        var refA = Flat(75);
        var refB = Flat(70);
        var zone = ZoneFrom(refA);
        var s = new SurveyMeasurement(zone, ZoneModel.BandSum(zone, Hz, Weighting.A), ZoneModel.BandSum(zone, Hz, Weighting.C),
        [
            new SurveyRef("a", refA, 0, 0, 0, null, 0.3, 12),
            new SurveyRef("b", refB, 0, 0, 0, null, 3.0, 12),
        ], 90, 0.8);
        var model = ZoneModel.Build([(s, 1)], Hz);
        // Weight = 1 / (stderr² + 0.25): the 0.25 floor stands for ~0.5 dB of model error no survey removes.
        Assert.True(model.Refs.Single(r => r.MicKey == "a").Weight > 3.5 * model.Refs.Single(r => r.MicKey == "b").Weight);

        // Live: ref "a" reads 2 dB up, ref "b" unchanged → prediction leans to "a".
        var both = ZoneModel.Predict(model, new Dictionary<string, RefReading> { ["a"] = Reading(Flat(77)), ["b"] = Reading(refB) }, Hz)!;
        Assert.Equal(2, both.RefsUsed);
        Assert.InRange(both.LC - s.RoverLC, 1.5, 1.7);   // 2 dB × 3.88/(3.88+1)

        var onlyB = ZoneModel.Predict(model, new Dictionary<string, RefReading> { ["b"] = Reading(Flat(72)) }, Hz)!;
        Assert.Equal(1, onlyB.RefsUsed);
        Assert.InRange(onlyB.LC - s.RoverLC, 1.99, 2.01);
        Assert.Null(ZoneModel.Predict(model, new Dictionary<string, RefReading>(), Hz));
    }

    [Fact]
    public void Manual_entries_use_broadband_offsets()
    {
        var s = new SurveyMeasurement(null, double.NaN, 92, [new SurveyRef("n:0", null, 90, 96, 0, null, 1, 0)], 60, 0.3);
        var model = ZoneModel.Build([(s, 1)], Hz);
        Assert.False(model.BandMode);
        var p = ZoneModel.Predict(model, new Dictionary<string, RefReading> { ["n:0"] = new(null, 91, 98, 0) }, Hz)!;
        Assert.Equal(94, p.LC, 3);       // 98 − 4
        Assert.Equal(87, p.LA, 3);       // no dBA entered: dBC offset used for A as well
    }

    [Fact]
    public void Quiet_bands_fall_back_to_the_broadband_offset()
    {
        var refBands = Flat(75);
        refBands[0] = 10;                // 20 Hz: far below everything else (noise)
        var model = ZoneModel.Build([(Survey(refBands), 1)], Hz);
        Assert.True(double.IsNaN(model.Refs[0].BandOffsets![0]));
        Assert.False(double.IsNaN(model.Refs[0].BandOffsets![10]));
    }

    [Fact]
    public void Averaging_surveys_combines_offsets_by_weight()
    {
        var s1 = Survey(Flat(75));
        var zone2 = Flat(75).Select(v => v - 10).ToArray();   // second survey heard the zone 10 dB down everywhere
        var s2 = new SurveyMeasurement(zone2, ZoneModel.BandSum(zone2, Hz, Weighting.A), ZoneModel.BandSum(zone2, Hz, Weighting.C),
            [new SurveyRef("n:0", Flat(75), 0, 0, 0, null, 0.5, 12)], 90, 0.9);
        var model = ZoneModel.Build([(s1, 3), (s2, 1)], Hz);
        // 1 kHz band: s1 offset −8, s2 offset −10 → (−8·3 − 10·1)/4 = −8.5
        var k = Array.IndexOf(Hz, 1000.0);
        Assert.Equal(-8.5, model.Refs[0].BandOffsets![k], 6);
        Assert.Equal(2, model.Surveys);
    }

    [Fact]
    public void Accumulator_measures_offsets_and_confidence_grows_with_time()
    {
        var acc = new SurveyAccumulator("rover", ["ref", "rover"], Hz.Length, 1000);
        IntervalLevels Iv(double c, double[] bands) => new(0.2, c - 2, c, c, bands);
        var refBands = Flat(70);
        var roverBands = ZoneFrom(refBands);
        var t = 1000.0;
        for (var i = 0; i < 100; i++, t += 0.2) // 20 s
        {
            var swell = 5 * Math.Sin(i / 15.0);
            acc.Add("ref", Iv(85 + swell, refBands.Select(v => v + swell).ToArray()), t);
            acc.Add("rover", Iv(80 + swell, roverBands.Select(v => v + swell).ToArray()), t);
            acc.Add("other", Iv(50, refBands), t); // not part of the survey
        }
        var short20 = acc.Measure(60, Hz)!;
        Assert.InRange(short20.Seconds, 19.9, 20.1);
        var r = Assert.Single(short20.Refs);
        Assert.Equal("ref", r.MicKey);
        Assert.InRange(r.ChunkStdC, 0, 0.05);                 // rover tracks the ref exactly
        Assert.InRange(short20.RoverLC - r.LC, -5.05, -4.95);

        for (var i = 0; i < 250; i++, t += 0.2)              // to 70 s
        {
            acc.Add("ref", Iv(85, refBands), t);
            acc.Add("rover", Iv(80, roverBands), t);
        }
        var full = acc.Measure(60, Hz)!;
        Assert.True(full.Confidence > short20.Confidence, $"{full.Confidence} > {short20.Confidence}");
        Assert.InRange(full.Confidence, 0.9, 1.0);
        var m = ZoneModel.Build([(full, 1)], Hz);
        Assert.Equal(-8, m.Refs[0].BandOffsets![Array.IndexOf(Hz, 1000.0)], 3);
    }
}
