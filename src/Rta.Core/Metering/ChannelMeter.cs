using Rta.Core.Dsp;

namespace Rta.Core.Metering;

public static class Db
{
    /// <summary>Floor used instead of -infinity for silent input.</summary>
    public const double FloorDb = -200;

    public static double FromMeanSquare(double ms) => ms > 1e-20 ? 10 * Math.Log10(ms) : FloorDb;
    public static double FromAmplitude(double amp) => amp > 1e-10 ? 20 * Math.Log10(amp) : FloorDb;
    public static double ToMeanSquare(double db) => Math.Pow(10, db / 10);
}

/// <summary>
/// Raw (uncalibrated, dBFS-domain) measurements over one block (default 100 ms).
/// "Mean square" values are relative to a digital full scale of 1.0, so
/// 10*log10(ms) is dB re full-scale RMS (a full-scale sine reads -3.01 dB).
/// </summary>
public sealed record MeterBlock(
    DateTimeOffset End,
    double DurationSeconds,
    double MsZ, double MsA, double MsC,
    double PeakZ, double PeakC,
    double FastZ, double FastA, double FastC,
    double SlowZ, double SlowA, double SlowC,
    double[] BandMs,
    bool Clipped);

/// <summary>
/// Processes one input channel: A/C/Z weighting, Fast/Slow exponential time weighting,
/// peak detection, 1/3-octave bands, and emits a <see cref="MeterBlock"/> every block.
/// Not thread-safe: feed from a single thread.
/// </summary>
public sealed class ChannelMeter
{
    public const double FastTau = 0.125;
    public const double SlowTau = 1.0;

    private readonly BiquadCascade _a;
    private readonly BiquadCascade _c;
    private readonly ThirdOctaveBank _bank;
    private readonly int _blockSamples;
    private readonly double _fastAlpha;
    private readonly double _slowAlpha;
    private readonly Func<DateTimeOffset> _clock;

    private double _sumZ, _sumA, _sumC, _peakZ, _peakC;
    private double _fz, _fa, _fc, _sz, _sa, _sc;
    private readonly double[] _bandSums;
    private int _count;
    private bool _clipped;

    public double SampleRate { get; }
    public IReadOnlyList<OctaveBand> Bands => _bank.Bands;

    public event Action<MeterBlock>? BlockReady;

    public ChannelMeter(double sampleRate, double blockSeconds = 0.1, Func<DateTimeOffset>? clock = null)
    {
        SampleRate = sampleRate;
        _a = FrequencyWeighting.CreateA(sampleRate);
        _c = FrequencyWeighting.CreateC(sampleRate);
        _bank = new ThirdOctaveBank(sampleRate);
        _bandSums = new double[_bank.Count];
        _blockSamples = Math.Max(1, (int)Math.Round(sampleRate * blockSeconds));
        _fastAlpha = 1 - Math.Exp(-1 / (sampleRate * FastTau));
        _slowAlpha = 1 - Math.Exp(-1 / (sampleRate * SlowTau));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void Process(ReadOnlySpan<float> samples)
    {
        foreach (var s in samples) ProcessSample(s);
    }

    /// <summary>Processes every <paramref name="stride"/>-th sample starting at <paramref name="offset"/> (interleaved input).</summary>
    public void ProcessInterleaved(ReadOnlySpan<float> interleaved, int offset, int stride)
    {
        for (var i = offset; i < interleaved.Length; i += stride) ProcessSample(interleaved[i]);
    }

    private void ProcessSample(float sample)
    {
        double x = sample;
        if (sample >= 0.999f || sample <= -0.999f) _clipped = true;
        var xa = _a.Process(x);
        var xc = _c.Process(x);
        double z2 = x * x, a2 = xa * xa, c2 = xc * xc;

        _sumZ += z2; _sumA += a2; _sumC += c2;
        var az = Math.Abs(x); if (az > _peakZ) _peakZ = az;
        var ac = Math.Abs(xc); if (ac > _peakC) _peakC = ac;

        _fz += (z2 - _fz) * _fastAlpha; _fa += (a2 - _fa) * _fastAlpha; _fc += (c2 - _fc) * _fastAlpha;
        _sz += (z2 - _sz) * _slowAlpha; _sa += (a2 - _sa) * _slowAlpha; _sc += (c2 - _sc) * _slowAlpha;

        _bank.ProcessAccumulate(x, _bandSums);

        if (++_count >= _blockSamples) Emit();
    }

    private void Emit()
    {
        var n = (double)_count;
        var bands = new double[_bandSums.Length];
        for (var i = 0; i < bands.Length; i++) { bands[i] = _bandSums[i] / n; _bandSums[i] = 0; }

        var block = new MeterBlock(
            _clock(), n / SampleRate,
            _sumZ / n, _sumA / n, _sumC / n,
            _peakZ, _peakC,
            _fz, _fa, _fc, _sz, _sa, _sc,
            bands, _clipped);

        _sumZ = _sumA = _sumC = _peakZ = _peakC = 0;
        _count = 0;
        _clipped = false;
        BlockReady?.Invoke(block);
    }
}
