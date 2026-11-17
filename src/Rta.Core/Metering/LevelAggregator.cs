using Rta.Core.Calibration;
using Rta.Core.Dsp;

namespace Rta.Core.Metering;

/// <summary>Calibrated readings for one channel at one moment. All levels in dB SPL (or dBFS when uncalibrated).</summary>
public sealed record LevelSnapshot
{
    public DateTimeOffset Time { get; init; }
    public bool Calibrated { get; init; }
    public double OffsetDb { get; init; }

    public double LAF { get; init; }
    public double LCF { get; init; }
    public double LZF { get; init; }
    public double LAS { get; init; }
    public double LCS { get; init; }
    public double LZS { get; init; }

    public double LAeq1m { get; init; }
    public double LCeq1m { get; init; }
    public double LZeq1m { get; init; }
    public double LAeq15m { get; init; }
    public double LCeq15m { get; init; }
    public double LAeqSession { get; init; }
    public double LCeqSession { get; init; }

    /// <summary>Highest C-weighted peak in the last block / last minute / session.</summary>
    public double LCpeak { get; init; }
    public double LCpeakMax1m { get; init; }
    public double LCpeakMaxSession { get; init; }

    /// <summary>LCeq1m − LAeq1m: low-frequency content indicator.</summary>
    public double CMinusA { get; init; }

    public double Coverage1mSeconds { get; init; }
    public double Coverage15mSeconds { get; init; }
    public double SessionSeconds { get; init; }
    public DateTimeOffset SessionStart { get; init; }

    /// <summary>Per-band level of the latest block (corrected).</summary>
    public double[] Bands { get; init; } = [];
    /// <summary>Per-band Leq over the last minute (corrected).</summary>
    public double[] Bands1m { get; init; } = [];

    public bool ClippedRecently { get; init; }
}

/// <summary>
/// Accumulates <see cref="MeterBlock"/>s for one channel: rolling 1-min and 15-min windows and a
/// resettable session. Calibration is applied at snapshot time, so changing a profile is instant.
/// Thread-safe.
/// </summary>
public sealed class LevelAggregator
{
    public static readonly TimeSpan Window1m = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Window15m = TimeSpan.FromMinutes(15);

    private readonly object _lock = new();
    private readonly Queue<MeterBlock> _history = new();
    private readonly IReadOnlyList<OctaveBand> _bands;
    private MeterBlock? _last;
    private DateTimeOffset _lastClip = DateTimeOffset.MinValue;

    private DateTimeOffset _sessionStart;
    private double _sessA, _sessC, _sessZ, _sessDur, _sessPeakC;

    public LevelAggregator(IReadOnlyList<OctaveBand> bands, DateTimeOffset? sessionStart = null)
    {
        _bands = bands;
        _sessionStart = sessionStart ?? DateTimeOffset.UtcNow;
    }

    public IReadOnlyList<OctaveBand> Bands => _bands;

    public void Add(MeterBlock b)
    {
        lock (_lock)
        {
            _last = b;
            _history.Enqueue(b);
            while (_history.Count > 0 && b.End - _history.Peek().End >= Window15m) _history.Dequeue();
            if (b.Clipped) _lastClip = b.End;

            _sessA += b.MsA * b.DurationSeconds;
            _sessC += b.MsC * b.DurationSeconds;
            _sessZ += b.MsZ * b.DurationSeconds;
            _sessDur += b.DurationSeconds;
            if (b.PeakC > _sessPeakC) _sessPeakC = b.PeakC;
        }
    }

    public void ResetSession(DateTimeOffset? start = null)
    {
        lock (_lock)
        {
            _sessionStart = start ?? DateTimeOffset.UtcNow;
            _sessA = _sessC = _sessZ = _sessDur = _sessPeakC = 0;
        }
    }

    public LevelSnapshot? Snapshot(CalibrationProfile? profile)
    {
        lock (_lock)
        {
            if (_last is null) return null;
            var off = profile?.OffsetDb ?? 0;
            var now = _last.End;
            var cut1 = now - Window1m;

            double a1 = 0, c1 = 0, z1 = 0, d1 = 0, a15 = 0, c15 = 0, d15 = 0, pk1 = 0;
            var bands1 = new double[_bands.Count];
            foreach (var b in _history)
            {
                a15 += b.MsA * b.DurationSeconds; c15 += b.MsC * b.DurationSeconds; d15 += b.DurationSeconds;
                if (b.End > cut1)
                {
                    a1 += b.MsA * b.DurationSeconds; c1 += b.MsC * b.DurationSeconds; z1 += b.MsZ * b.DurationSeconds;
                    d1 += b.DurationSeconds;
                    if (b.PeakC > pk1) pk1 = b.PeakC;
                    for (var i = 0; i < bands1.Length && i < b.BandMs.Length; i++) bands1[i] += b.BandMs[i] * b.DurationSeconds;
                }
            }

            double L(double ms) => Db.FromMeanSquare(ms) + off;
            double Leq(double e, double d) => d > 0 ? L(e / d) : Db.FloorDb;

            var corr = new double[_bands.Count];
            for (var i = 0; i < corr.Length; i++)
                corr[i] = off - CorrectionCurve.InterpolateDb(profile?.Correction, _bands[i].CenterHz);

            var bandsNow = new double[_bands.Count];
            for (var i = 0; i < bandsNow.Length; i++)
            {
                bandsNow[i] = Db.FromMeanSquare(i < _last.BandMs.Length ? _last.BandMs[i] : 0) + corr[i];
                bands1[i] = d1 > 0 ? Db.FromMeanSquare(bands1[i] / d1) + corr[i] : Db.FloorDb;
            }

            var la1 = Leq(a1, d1); var lc1 = Leq(c1, d1);
            return new LevelSnapshot
            {
                Time = now,
                Calibrated = profile is not null,
                OffsetDb = off,
                LAF = L(_last.FastA), LCF = L(_last.FastC), LZF = L(_last.FastZ),
                LAS = L(_last.SlowA), LCS = L(_last.SlowC), LZS = L(_last.SlowZ),
                LAeq1m = la1, LCeq1m = lc1, LZeq1m = Leq(z1, d1),
                LAeq15m = Leq(a15, d15), LCeq15m = Leq(c15, d15),
                LAeqSession = Leq(_sessA, _sessDur), LCeqSession = Leq(_sessC, _sessDur),
                LCpeak = Db.FromAmplitude(_last.PeakC) + off,
                LCpeakMax1m = Db.FromAmplitude(pk1) + off,
                LCpeakMaxSession = Db.FromAmplitude(_sessPeakC) + off,
                CMinusA = lc1 - la1,
                Coverage1mSeconds = d1, Coverage15mSeconds = d15,
                SessionSeconds = _sessDur, SessionStart = _sessionStart,
                Bands = bandsNow, Bands1m = bands1,
                ClippedRecently = now - _lastClip < TimeSpan.FromSeconds(3),
            };
        }
    }
}
