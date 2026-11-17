using Rta.Core.Dsp;
using Rta.Core.Metering;
using Rta.Core.Signals;

namespace Rta.Core.Tests;

public class WeightingTests
{
    // IEC 61672-1 nominal weightings and Class 1 tolerance (+, -)
    public static TheoryData<double, double, double, double, double> Table => new()
    {
        // f,     A,     C,    tol+, tol-
        { 31.5, -39.4, -3.0, 1.5, 1.5 },
        { 63,   -26.2, -0.8, 1.0, 1.0 },
        { 125,  -16.1, -0.2, 1.0, 1.0 },
        { 250,   -8.6,  0.0, 1.0, 1.0 },
        { 500,   -3.2,  0.0, 1.0, 1.0 },
        { 1000,   0.0,  0.0, 0.7, 0.7 },
        { 2000,   1.2, -0.2, 1.0, 1.0 },
        { 4000,   1.0, -0.8, 1.0, 1.0 },
        { 8000,  -1.1, -3.0, 1.5, 2.5 },
        { 10000, -2.5, -4.4, 2.0, 3.0 },
        { 12500, -4.3, -6.2, 2.0, 5.0 },
        { 16000, -6.6, -8.5, 3.5, 17.0 },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Analog_formula_matches_IEC_table(double nominal, double a, double c, double tolPlus, double tolMinus)
    {
        Assert.True(tolPlus > 0 && tolMinus > 0);
        // IEC tables are computed at exact base-10 frequencies, not the nominal labels
        var f = 1000 * Math.Pow(10, Math.Round(10 * Math.Log10(nominal / 1000)) / 10);
        Assert.InRange(FrequencyWeighting.AnalogDb(Weighting.A, f), a - 0.1, a + 0.1);
        Assert.InRange(FrequencyWeighting.AnalogDb(Weighting.C, f), c - 0.1, c + 0.1);
    }

    [Theory]
    [MemberData(nameof(Table))]
    public void Digital_A_is_within_class1_at_48k_and_96k(double f, double a, double _, double tolPlus, double tolMinus)
    {
        foreach (var fs in new[] { 48000.0, 96000.0 })
        {
            var db = FrequencyWeighting.CreateA(fs).ResponseDb(f, fs);
            Assert.InRange(db, a - tolMinus, a + tolPlus);
        }
    }

    [Theory]
    [MemberData(nameof(Table))]
    public void Digital_C_is_within_class1_at_48k_and_96k(double f, double _, double c, double tolPlus, double tolMinus)
    {
        foreach (var fs in new[] { 48000.0, 96000.0 })
        {
            var db = FrequencyWeighting.CreateC(fs).ResponseDb(f, fs);
            Assert.InRange(db, c - tolMinus, c + tolPlus);
        }
    }

    [Theory]
    [InlineData(31.5)] [InlineData(100)] [InlineData(1000)] [InlineData(4000)] [InlineData(8000)]
    public void Digital_weighting_is_close_to_analog_up_to_8k(double f)
    {
        foreach (var fs in new[] { 48000.0, 96000.0 })
        {
            Assert.InRange(FrequencyWeighting.CreateA(fs).ResponseDb(f, fs) - FrequencyWeighting.AnalogDb(Weighting.A, f), -0.6, 0.1);
            Assert.InRange(FrequencyWeighting.CreateC(fs).ResponseDb(f, fs) - FrequencyWeighting.AnalogDb(Weighting.C, f), -0.6, 0.1);
        }
    }
}

public class ThirdOctaveTests
{
    [Theory]
    [InlineData(48000, 31)]
    [InlineData(96000, 31)]
    [InlineData(44100, 30)]
    public void Band_count_depends_on_sample_rate(double fs, int expected)
    {
        Assert.Equal(expected, new ThirdOctaveBank(fs).Count);
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(96000)]
    public void Each_band_is_0dB_at_centre_and_about_minus3_at_edges(double fs)
    {
        var bank = new ThirdOctaveBank(fs);
        for (var i = 0; i < bank.Count; i++)
        {
            var band = bank.Bands[i];
            var f = bank.Filter(i);
            Assert.InRange(f.ResponseDb(band.CenterHz, fs), -0.01, 0.01);
            Assert.InRange(f.ResponseDb(band.LowerHz, fs), -3.6, -2.4);
            Assert.InRange(f.ResponseDb(band.UpperHz, fs), -3.6, -2.4);
            // one third-octave away should be well rejected; bands near Nyquist are squeezed
            // by bilinear warping (only the 20 kHz band at 48 kHz), so allow less there.
            var limit = band.UpperHz > 0.4 * fs ? -10 : -15;
            var above = band.CenterHz * Math.Pow(2, 1 / 3.0);
            if (above < fs / 2)
                Assert.True(f.ResponseDb(above, fs) < limit, $"band {band.NominalHz} upper rejection");
            var below = f.ResponseDb(band.CenterHz / Math.Pow(2, 1 / 3.0), fs);
            Assert.True(below < limit, $"band {band.NominalHz} lower rejection {below:0.00} dB");
        }
    }
}

public class ChannelMeterTests
{
    private static (ChannelMeter meter, List<MeterBlock> blocks) Meter(double fs)
    {
        var t = DateTimeOffset.UnixEpoch;
        var meter = new ChannelMeter(fs, 0.1, () => t = t.AddSeconds(0.1));
        var blocks = new List<MeterBlock>();
        meter.BlockReady += blocks.Add;
        return (meter, blocks);
    }

    private static void Run(ChannelMeter meter, SignalGenerator gen, double seconds, double fs)
    {
        var buf = new float[(int)(fs / 100)];
        for (var i = 0; i < seconds * 100; i++) { gen.Fill(buf); meter.Process(buf); }
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(96000)]
    public void Sine_1k_reads_its_rms_level_in_all_weightings(double fs)
    {
        var (meter, blocks) = Meter(fs);
        var gen = new SignalGenerator(fs) { FrequencyHz = 1000, LevelDbfs = -20 };
        Run(meter, gen, 6, fs);
        var b = blocks[^1];
        Assert.InRange(Db.FromMeanSquare(b.MsZ), -20.05, -19.95);
        Assert.InRange(Db.FromMeanSquare(b.MsA), -20.1, -19.9);
        Assert.InRange(Db.FromMeanSquare(b.MsC), -20.1, -19.9);
        Assert.InRange(Db.FromMeanSquare(b.SlowC), -20.2, -19.8);
        Assert.InRange(Db.FromAmplitude(b.PeakZ), -17.05, -16.9); // crest factor 3.01 dB
        var k = meter.Bands.ToList().FindIndex(x => x.NominalHz == 1000);
        Assert.InRange(Db.FromMeanSquare(b.BandMs[k]), -20.2, -19.8);
        Assert.True(Db.FromMeanSquare(b.BandMs[k + 3]) < -50, "1 octave away must be strongly attenuated");
        Assert.False(b.Clipped);
    }

    [Fact]
    public void Sine_100Hz_is_attenuated_by_A_weighting()
    {
        const double fs = 48000;
        var (meter, blocks) = Meter(fs);
        Run(meter, new SignalGenerator(fs) { FrequencyHz = 100, LevelDbfs = -20 }, 3, fs);
        var b = blocks[^1];
        var expectedA = -20 + FrequencyWeighting.AnalogDb(Weighting.A, 100);
        Assert.InRange(Db.FromMeanSquare(b.MsA), expectedA - 0.2, expectedA + 0.2);
        Assert.InRange(Db.FromMeanSquare(b.MsC), -20.6, -20.2);
    }

    [Fact]
    public void Fast_and_slow_decay_at_standard_rates()
    {
        const double fs = 48000;
        var (meter, blocks) = Meter(fs);
        var gen = new SignalGenerator(fs) { FrequencyHz = 1000, LevelDbfs = -20 };
        Run(meter, gen, 5, fs);
        var before = blocks[^1];
        gen.Kind = TestSignalKind.Silence;
        Run(meter, gen, 0.5, fs);
        var after = blocks[^1];
        // exponential decay: 10*log10(e)/tau dB per second
        var fastDrop = Db.FromMeanSquare(before.FastZ) - Db.FromMeanSquare(after.FastZ);
        var slowDrop = Db.FromMeanSquare(before.SlowZ) - Db.FromMeanSquare(after.SlowZ);
        Assert.InRange(fastDrop, 17.37 - 0.5, 17.37 + 0.5);
        Assert.InRange(slowDrop, 2.17 - 0.2, 2.17 + 0.2);
    }

    [Fact]
    public void Full_scale_input_flags_clipping()
    {
        const double fs = 48000;
        var (meter, blocks) = Meter(fs);
        Run(meter, new SignalGenerator(fs) { FrequencyHz = 1000, LevelDbfs = 0 }, 0.2, fs);
        Assert.Contains(blocks, b => b.Clipped);
    }

    [Fact]
    public void Pink_noise_generator_hits_requested_level()
    {
        const double fs = 48000;
        var (meter, blocks) = Meter(fs);
        Run(meter, new SignalGenerator(fs, TestSignalKind.PinkNoise) { LevelDbfs = -20 }, 10, fs);
        var leq = Db.FromMeanSquare(blocks.Skip(10).Average(b => b.MsZ));
        Assert.InRange(leq, -20.5, -19.5);
    }
}
