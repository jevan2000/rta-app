using System.Numerics;

namespace Rta.Core.Dsp;

/// <summary>
/// Second-order IIR section, transposed direct form II, double precision.
/// Coefficients are normalised so a0 = 1.
/// </summary>
public sealed class Biquad
{
    public double B0 { get; private set; }
    public double B1 { get; private set; }
    public double B2 { get; private set; }
    public double A1 { get; }
    public double A2 { get; }

    private double _z1;
    private double _z2;

    public Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        B0 = b0; B1 = b1; B2 = b2; A1 = a1; A2 = a2;
    }

    /// <summary>Builds a section from two zeros and two poles in the z-plane (each pair must be real or conjugate).</summary>
    public static Biquad FromZerosPoles(Complex q1, Complex q2, Complex p1, Complex p2)
    {
        var bSum = q1 + q2; var bProd = q1 * q2;
        var aSum = p1 + p2; var aProd = p1 * p2;
        return new Biquad(1.0, -bSum.Real, bProd.Real, -aSum.Real, aProd.Real);
    }

    /// <summary>Scales the numerator (overall gain of this section).</summary>
    public void ScaleGain(double g)
    {
        B0 *= g; B1 *= g; B2 *= g;
    }

    public double Process(double x)
    {
        var y = B0 * x + _z1;
        _z1 = B1 * x - A1 * y + _z2;
        _z2 = B2 * x - A2 * y;
        return y;
    }

    public void Reset() { _z1 = 0; _z2 = 0; }

    /// <summary>Complex frequency response at <paramref name="frequencyHz"/>.</summary>
    public Complex Response(double frequencyHz, double sampleRate)
    {
        var w = 2 * Math.PI * frequencyHz / sampleRate;
        var zInv = Complex.FromPolarCoordinates(1, -w);
        var zInv2 = zInv * zInv;
        var num = B0 + B1 * zInv + B2 * zInv2;
        var den = 1 + A1 * zInv + A2 * zInv2;
        return num / den;
    }
}

/// <summary>A cascade of biquads processed in series.</summary>
public sealed class BiquadCascade
{
    private readonly Biquad[] _sections;

    public BiquadCascade(params Biquad[] sections) => _sections = sections;

    public IReadOnlyList<Biquad> Sections => _sections;

    public double Process(double x)
    {
        for (var i = 0; i < _sections.Length; i++) x = _sections[i].Process(x);
        return x;
    }

    public void Reset() { foreach (var s in _sections) s.Reset(); }

    public Complex Response(double frequencyHz, double sampleRate)
    {
        Complex h = Complex.One;
        foreach (var s in _sections) h *= s.Response(frequencyHz, sampleRate);
        return h;
    }

    public double ResponseDb(double frequencyHz, double sampleRate) =>
        20 * Math.Log10(Response(frequencyHz, sampleRate).Magnitude);

    /// <summary>Scales the cascade so |H(f)| = 1 at the given frequency.</summary>
    public void NormalizeAt(double frequencyHz, double sampleRate)
    {
        var mag = Response(frequencyHz, sampleRate).Magnitude;
        _sections[0].ScaleGain(1.0 / mag);
    }

    /// <summary>Bilinear transform of an s-plane point to the z-plane: z = (2fs + s) / (2fs - s).</summary>
    public static Complex Bilinear(Complex s, double sampleRate)
    {
        var k = 2 * sampleRate;
        return (k + s) / (k - s);
    }
}
