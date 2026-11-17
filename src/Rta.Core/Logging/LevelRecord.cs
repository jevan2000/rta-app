using Rta.Core.Calibration;
using Rta.Core.Dsp;
using Rta.Core.Metering;

namespace Rta.Core.Logging;

public enum RecordKind { Second, Minute }

/// <summary>
/// One logged interval for one input, calibrated at the node. Seconds carry broadband values;
/// minutes also carry 1/3-octave band Leq (for spectrograms and, later, zone prediction).
/// Levels are dB SPL when <see cref="Calibrated"/>, otherwise dBFS.
/// </summary>
public sealed record LevelRecord
{
    public RecordKind Kind { get; init; }
    public int ChannelIndex { get; init; }
    /// <summary>Start of the interval, Unix seconds (UTC), aligned to the second or minute.</summary>
    public long Ts { get; init; }
    /// <summary>Seconds of audio actually measured in the interval (≤ 1 or ≤ 60).</summary>
    public double Coverage { get; init; }
    public double LAeq { get; init; }
    public double LCeq { get; init; }
    public double LZeq { get; init; }
    public double LCpeak { get; init; }
    public double LASmax { get; init; }
    public double LCSmax { get; init; }
    public bool Calibrated { get; init; }
    public CalibrationQuality? Quality { get; init; }
    public bool Clipped { get; init; }
    /// <summary>Band Leq (dB), minute records only.</summary>
    public double[]? Bands { get; init; }
}

/// <summary>
/// Turns a channel's 100 ms blocks into wall-clock-aligned second and minute records.
/// Calibration is applied when a record is emitted, using the profile current at that moment.
/// Not thread-safe; call from the metering thread.
/// </summary>
public sealed class RecordBuilder
{
    private sealed class Acc
    {
        public long Key = long.MinValue;
        public double A, C, Z, Dur, PeakC, SlowA, SlowC;
        public bool Clipped;
        public double[]? Bands;
        public void Reset(long key, int bands)
        {
            Key = key; A = C = Z = Dur = PeakC = SlowA = SlowC = 0; Clipped = false;
            if (bands > 0) { Bands ??= new double[bands]; Array.Clear(Bands); }
        }
    }

    private readonly int _channel;
    private readonly IReadOnlyList<OctaveBand> _bands;
    private readonly Func<CalibrationProfile?> _profile;
    private readonly Acc _sec = new();
    private readonly Acc _min = new();

    public event Action<LevelRecord>? RecordReady;

    public RecordBuilder(int channel, IReadOnlyList<OctaveBand> bands, Func<CalibrationProfile?> profile)
    {
        _channel = channel;
        _bands = bands;
        _profile = profile;
    }

    public void Add(MeterBlock b)
    {
        // Attribute the block to the second its midpoint falls in.
        var mid = b.End.ToUnixTimeMilliseconds() - (long)(b.DurationSeconds * 500);
        var sec = Math.DivRem(mid, 1000L, out var rem) - (rem < 0 ? 1 : 0);
        var min = sec - ((sec % 60) + 60) % 60;

        if (_sec.Key != sec) { if (_sec.Key != long.MinValue) Emit(_sec, RecordKind.Second); _sec.Reset(sec, 0); }
        if (_min.Key != min) { if (_min.Key != long.MinValue) Emit(_min, RecordKind.Minute); _min.Reset(min, _bands.Count); }

        foreach (var acc in new[] { _sec, _min })
        {
            var d = b.DurationSeconds;
            acc.A += b.MsA * d; acc.C += b.MsC * d; acc.Z += b.MsZ * d; acc.Dur += d;
            if (b.PeakC > acc.PeakC) acc.PeakC = b.PeakC;
            if (b.SlowA > acc.SlowA) acc.SlowA = b.SlowA;
            if (b.SlowC > acc.SlowC) acc.SlowC = b.SlowC;
            acc.Clipped |= b.Clipped;
            if (acc.Bands is not null)
                for (var i = 0; i < acc.Bands.Length && i < b.BandMs.Length; i++) acc.Bands[i] += b.BandMs[i] * d;
        }
    }

    /// <summary>Emits whatever is accumulated (e.g. when audio stops).</summary>
    public void Flush()
    {
        if (_sec.Key != long.MinValue && _sec.Dur > 0) Emit(_sec, RecordKind.Second);
        if (_min.Key != long.MinValue && _min.Dur > 0) Emit(_min, RecordKind.Minute);
        _sec.Key = _min.Key = long.MinValue;
    }

    private void Emit(Acc acc, RecordKind kind)
    {
        if (acc.Dur <= 0) return;
        var p = _profile();
        var off = p?.OffsetDb ?? 0;
        double L(double energy) => Round(Db.FromMeanSquare(energy / acc.Dur) + off);
        double[]? bands = null;
        if (kind == RecordKind.Minute && acc.Bands is not null)
        {
            bands = new double[acc.Bands.Length];
            for (var i = 0; i < bands.Length; i++)
                bands[i] = Round(Db.FromMeanSquare(acc.Bands[i] / acc.Dur) + off - CorrectionCurve.InterpolateDb(p?.Correction, _bands[i].CenterHz));
        }
        RecordReady?.Invoke(new LevelRecord
        {
            Kind = kind,
            ChannelIndex = _channel,
            Ts = acc.Key,
            Coverage = Math.Round(acc.Dur, 2),
            LAeq = L(acc.A), LCeq = L(acc.C), LZeq = L(acc.Z),
            LCpeak = Round(Db.FromAmplitude(acc.PeakC) + off),
            LASmax = Round(Db.FromMeanSquare(acc.SlowA) + off),
            LCSmax = Round(Db.FromMeanSquare(acc.SlowC) + off),
            Calibrated = p is not null,
            Quality = p?.Quality,
            Clipped = acc.Clipped,
            Bands = bands,
        });
    }

    private static double Round(double v) => Math.Round(v, 2);
}

public static class LeqMath
{
    /// <summary>Energy average of levels (dB) weighted by durations.</summary>
    public static double EnergyAverage(IEnumerable<(double Db, double Seconds)> items)
    {
        double e = 0, d = 0;
        foreach (var (db, s) in items)
        {
            if (db <= Db.FloorDb + 1 || s <= 0) continue;
            e += Math.Pow(10, db / 10) * s;
            d += s;
        }
        return d > 0 ? 10 * Math.Log10(e / d) : Db.FloorDb;
    }
}
