namespace Rta.Core.Signals;

public enum TestSignalKind { Sine, PinkNoise, WhiteNoise, Silence }

/// <summary>
/// Deterministic test-signal source. Level is the RMS level in dB re full scale
/// (10·log10 of mean square), matching the metering convention.
/// </summary>
public sealed class SignalGenerator
{
    private readonly double _sampleRate;
    private readonly Random _rng;
    private double _phase;
    // Paul Kellet's refined pink-noise filter state (pink is approximate; fine for test use)
    private double _b0, _b1, _b2, _b3, _b4, _b5, _b6;

    public TestSignalKind Kind { get; set; }
    public double FrequencyHz { get; set; } = 1000;
    public double LevelDbfs { get; set; } = -20;

    public SignalGenerator(double sampleRate, TestSignalKind kind = TestSignalKind.Sine, int seed = 1234)
        : this(sampleRate, kind, seed, measureNorm: true) { }

    private SignalGenerator(double sampleRate, TestSignalKind kind, int seed, bool measureNorm)
    {
        _pinkNorm = measureNorm ? MeasurePinkNorm() : 1;
        _sampleRate = sampleRate;
        Kind = kind;
        _rng = new Random(seed);
    }

    public void Fill(Span<float> buffer)
    {
        var rms = Math.Pow(10, LevelDbfs / 20);
        switch (Kind)
        {
            case TestSignalKind.Sine:
                var amp = rms * Math.Sqrt(2);
                var inc = 2 * Math.PI * FrequencyHz / _sampleRate;
                for (var i = 0; i < buffer.Length; i++)
                {
                    buffer[i] = (float)(amp * Math.Sin(_phase));
                    _phase += inc;
                    if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
                }
                break;
            case TestSignalKind.WhiteNoise:
                // uniform [-1,1] has rms 1/sqrt(3)
                for (var i = 0; i < buffer.Length; i++)
                    buffer[i] = (float)(rms * Math.Sqrt(3) * (_rng.NextDouble() * 2 - 1));
                break;
            case TestSignalKind.PinkNoise:
                for (var i = 0; i < buffer.Length; i++)
                    buffer[i] = (float)(rms * NextPink(_rng) * _pinkNorm);
                break;
            default:
                buffer.Clear();
                break;
        }
    }

    private readonly double _pinkNorm;

    private double NextPink(Random rng)
    {
        var white = rng.NextDouble() * 2 - 1;
        _b0 = 0.99886 * _b0 + white * 0.0555179;
        _b1 = 0.99332 * _b1 + white * 0.0750759;
        _b2 = 0.96900 * _b2 + white * 0.1538520;
        _b3 = 0.86650 * _b3 + white * 0.3104856;
        _b4 = 0.55000 * _b4 + white * 0.5329522;
        _b5 = -0.7616 * _b5 - white * 0.0168980;
        var pink = _b0 + _b1 + _b2 + _b3 + _b4 + _b5 + _b6 + white * 0.5362;
        _b6 = white * 0.115926;
        return pink;
    }

    /// <summary>Scale factor giving unit-RMS pink noise, measured on a throwaway generator.</summary>
    private static double MeasurePinkNorm()
    {
        var g = new SignalGenerator(48000, TestSignalKind.Silence, seed: 99, measureNorm: false);
        var rng = new Random(99);
        for (var i = 0; i < 20000; i++) g.NextPink(rng); // settle
        double sum = 0; const int n = 400000;
        for (var i = 0; i < n; i++) { var v = g.NextPink(rng); sum += v * v; }
        return 1 / Math.Sqrt(sum / n);
    }
}
