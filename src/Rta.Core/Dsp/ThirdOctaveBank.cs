using System.Numerics;

namespace Rta.Core.Dsp;

/// <summary>One 1/3-octave band (base-10, IEC 61260).</summary>
public sealed record OctaveBand(int Index, double NominalHz, double CenterHz, double LowerHz, double UpperHz);

/// <summary>
/// 1/3-octave filter bank: 6th-order (order-3 prototype) Butterworth band-pass per band,
/// designed with a pre-warped bilinear transform. Bands whose upper edge is too close to
/// Nyquist are omitted.
/// </summary>
public sealed class ThirdOctaveBank
{
    /// <summary>Nominal centre frequencies 20 Hz .. 20 kHz.</summary>
    public static readonly double[] NominalCenters =
    [
        20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800,
        1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000,
    ];

    private const int PrototypeOrder = 3;
    private readonly BiquadCascade[] _filters;

    public IReadOnlyList<OctaveBand> Bands { get; }
    public double SampleRate { get; }

    public ThirdOctaveBank(double sampleRate)
    {
        SampleRate = sampleRate;
        var bands = new List<OctaveBand>();
        var filters = new List<BiquadCascade>();
        for (var i = 0; i < NominalCenters.Length; i++)
        {
            var n = i - 17; // 1 kHz is index 17
            var fc = 1000 * Math.Pow(10, n / 10.0);
            var lo = fc * Math.Pow(10, -1 / 20.0);
            var hi = fc * Math.Pow(10, 1 / 20.0);
            if (hi >= 0.48 * sampleRate) continue;
            bands.Add(new OctaveBand(bands.Count, NominalCenters[i], fc, lo, hi));
            filters.Add(DesignBandpass(lo, hi, sampleRate));
        }
        Bands = bands;
        _filters = filters.ToArray();
    }

    public int Count => _filters.Length;

    /// <summary>Processes one sample through every band; writes squared outputs into <paramref name="sumSquares"/> (accumulating).</summary>
    public void ProcessAccumulate(double x, double[] sumSquares)
    {
        for (var b = 0; b < _filters.Length; b++)
        {
            var y = _filters[b].Process(x);
            sumSquares[b] += y * y;
        }
    }

    public BiquadCascade Filter(int band) => _filters[band];

    public void Reset() { foreach (var f in _filters) f.Reset(); }

    private static BiquadCascade DesignBandpass(double loHz, double hiHz, double fs)
    {
        // Pre-warp band edges
        double w1 = 2 * fs * Math.Tan(Math.PI * loHz / fs);
        double w2 = 2 * fs * Math.Tan(Math.PI * hiHz / fs);
        double w0Sq = w1 * w2;
        double bw = w2 - w1;

        var analogPoles = new List<Complex>();
        for (var k = 1; k <= PrototypeOrder; k++)
        {
            var theta = Math.PI * (2 * k + PrototypeOrder - 1) / (2 * PrototypeOrder);
            var p = Complex.FromPolarCoordinates(1, theta);
            // s^2 - p*B*s + w0^2 = 0
            var pb = p * bw;
            var disc = Complex.Sqrt(pb * pb - 4 * w0Sq);
            analogPoles.Add((pb + disc) / 2);
            analogPoles.Add((pb - disc) / 2);
        }

        var upper = analogPoles
            .Select(s => BiquadCascade.Bilinear(s, fs))
            .Where(z => z.Imaginary > 0)
            .ToList();
        if (upper.Count != PrototypeOrder)
            throw new InvalidOperationException($"Band-pass design produced {upper.Count} upper-half poles.");

        var sections = upper
            .Select(z => Biquad.FromZerosPoles(1, -1, z, Complex.Conjugate(z)))
            .ToArray();
        var cascade = new BiquadCascade(sections);
        cascade.NormalizeAt(Math.Sqrt(loHz * hiHz), fs);
        return cascade;
    }
}
