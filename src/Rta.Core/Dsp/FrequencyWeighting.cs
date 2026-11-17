using System.Numerics;

namespace Rta.Core.Dsp;

public enum Weighting { Z, A, C }

/// <summary>
/// IEC 61672-1 A and C frequency weightings as digital IIR cascades.
/// Analog pole frequencies from the standard; mapped with the bilinear transform
/// and normalised to 0 dB at 1 kHz.
/// </summary>
public static class FrequencyWeighting
{
    // IEC 61672-1 pole frequencies (Hz)
    private const double F1 = 20.598997;
    private const double F2 = 107.65265;
    private const double F3 = 737.86223;
    private const double F4 = 12194.217;

    public static BiquadCascade Create(Weighting weighting, double sampleRate) => weighting switch
    {
        Weighting.A => CreateA(sampleRate),
        Weighting.C => CreateC(sampleRate),
        Weighting.Z => new BiquadCascade(new Biquad(1, 0, 0, 0, 0)),
        _ => throw new ArgumentOutOfRangeException(nameof(weighting)),
    };

    /// <summary>C: H(s) = k s^2 / ((s + w1)^2 (s + w4)^2)</summary>
    public static BiquadCascade CreateC(double sampleRate)
    {
        var p1 = Pole(F1, sampleRate);
        var p4 = Pole(F4, sampleRate);
        var cascade = new BiquadCascade(
            Biquad.FromZerosPoles(1, 1, p1, p1),
            Biquad.FromZerosPoles(-1, -1, p4, p4));
        cascade.NormalizeAt(1000, sampleRate);
        return cascade;
    }

    /// <summary>A: H(s) = k s^4 / ((s + w1)^2 (s + w2)(s + w3)(s + w4)^2)</summary>
    public static BiquadCascade CreateA(double sampleRate)
    {
        var p1 = Pole(F1, sampleRate);
        var p2 = Pole(F2, sampleRate);
        var p3 = Pole(F3, sampleRate);
        var p4 = Pole(F4, sampleRate);
        var cascade = new BiquadCascade(
            Biquad.FromZerosPoles(1, 1, p1, p1),
            Biquad.FromZerosPoles(1, 1, p2, p3),
            Biquad.FromZerosPoles(-1, -1, p4, p4));
        cascade.NormalizeAt(1000, sampleRate);
        return cascade;
    }

    private static Complex Pole(double fHz, double sampleRate) =>
        BiquadCascade.Bilinear(new Complex(-2 * Math.PI * fHz, 0), sampleRate);

    /// <summary>Exact analog weighting (dB) at frequency f, normalised to 0 dB at 1 kHz. Used for band-based totals and tests.</summary>
    public static double AnalogDb(Weighting weighting, double f)
    {
        if (weighting == Weighting.Z) return 0;
        return RawAnalogDb(weighting, f) - RawAnalogDb(weighting, 1000);
    }

    private static double RawAnalogDb(Weighting w, double f)
    {
        double f2 = f * f;
        double c = (F4 * F4 * f2) / ((f2 + F1 * F1) * (f2 + F4 * F4));
        if (w == Weighting.C) return 20 * Math.Log10(c);
        double a = (F4 * F4 * f2 * f2) /
                   ((f2 + F1 * F1) * Math.Sqrt((f2 + F2 * F2) * (f2 + F3 * F3)) * (f2 + F4 * F4));
        return 20 * Math.Log10(a);
    }
}
