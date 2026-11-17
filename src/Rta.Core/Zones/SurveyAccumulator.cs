using Rta.Core.Metering;
using Rta.Core.Protocol;

namespace Rta.Core.Zones;

/// <summary>
/// Collects gap-free interval levels from the roving mic and every reference mic during a survey,
/// in hub-clock 5-second chunks, and turns them into a <see cref="SurveyMeasurement"/>.
/// </summary>
public sealed class SurveyAccumulator
{
    public const double ChunkSeconds = 5;

    private sealed class MicAcc(int bands)
    {
        public double A, C, Dur;
        public readonly double[] Bands = new double[bands];
        public readonly Dictionary<long, (double C, double Dur)> Chunks = [];
    }

    private readonly object _lock = new();
    private readonly string _rover;
    private readonly HashSet<string> _refs;
    private readonly Dictionary<string, MicAcc> _mics = [];
    private readonly int _bands;
    private readonly double _start;

    public SurveyAccumulator(string roverKey, IEnumerable<string> refKeys, int bandCount, double startUnixSeconds)
    {
        _rover = roverKey;
        _refs = refKeys.Where(k => k != roverKey).ToHashSet();
        _bands = bandCount;
        _start = startUnixSeconds;
    }

    public IReadOnlyCollection<string> RefKeys => _refs;

    public void Add(string micKey, IntervalLevels iv, double hubTimeUnixSeconds)
    {
        if (micKey != _rover && !_refs.Contains(micKey)) return;
        if (iv.Seconds <= 0 || hubTimeUnixSeconds < _start) return;
        lock (_lock)
        {
            if (!_mics.TryGetValue(micKey, out var m)) _mics[micKey] = m = new MicAcc(_bands);
            var d = iv.Seconds;
            m.A += Db.ToMeanSquare(iv.LA) * d;
            m.C += Db.ToMeanSquare(iv.LC) * d;
            m.Dur += d;
            for (var i = 0; i < _bands && i < iv.Bands.Length; i++) m.Bands[i] += Db.ToMeanSquare(iv.Bands[i]) * d;
            var chunk = (long)Math.Floor((hubTimeUnixSeconds - _start) / ChunkSeconds);
            var c = m.Chunks.GetValueOrDefault(chunk);
            m.Chunks[chunk] = (c.C + Db.ToMeanSquare(iv.LC) * d, c.Dur + d);
        }
    }

    public double RoverSeconds { get { lock (_lock) return _mics.TryGetValue(_rover, out var m) ? m.Dur : 0; } }

    public IReadOnlyCollection<string> RefsWithData { get { lock (_lock) return _mics.Keys.Where(_refs.Contains).ToList(); } }

    /// <summary>Snapshot of the survey so far (also used for the live confidence bar).</summary>
    public SurveyMeasurement? Measure(double minSeconds, IReadOnlyList<double> bandsHz)
    {
        lock (_lock)
        {
            if (!_mics.TryGetValue(_rover, out var rover) || rover.Dur <= 0) return null;
            double[] Bands(MicAcc m) => m.Bands.Select(e => Db.FromMeanSquare(e / m.Dur)).ToArray();
            var roverBands = Bands(rover);
            var refs = new List<SurveyRef>();
            foreach (var key in _refs)
            {
                if (!_mics.TryGetValue(key, out var r) || r.Dur < rover.Dur * 0.8) continue; // ref missed too much
                var offsets = new List<double>();
                foreach (var (chunk, rc) in rover.Chunks)
                    if (rc.Dur >= ChunkSeconds * 0.6 && r.Chunks.TryGetValue(chunk, out var fc) && fc.Dur >= ChunkSeconds * 0.6)
                        offsets.Add(Db.FromMeanSquare(rc.C / rc.Dur) - Db.FromMeanSquare(fc.C / fc.Dur));
                var mean = offsets.Count > 0 ? offsets.Average() : 0;
                var std = offsets.Count > 1 ? Math.Sqrt(offsets.Sum(o => (o - mean) * (o - mean)) / (offsets.Count - 1)) : 2;
                refs.Add(new SurveyRef(key, Bands(r), Db.FromMeanSquare(r.A / r.Dur), Db.FromMeanSquare(r.C / r.Dur), 0, null, Math.Round(std, 3), offsets.Count));
            }

            // Fraction of the speech/music bands (50 Hz–10 kHz) with usable signal at the rover.
            var max = roverBands.Max();
            var core = Enumerable.Range(0, Math.Min(roverBands.Length, bandsHz.Count)).Where(i => bandsHz[i] is >= 50 and <= 10000).ToList();
            var valid = core.Count == 0 ? 0 : core.Count(i => roverBands[i] > max - ZoneModel.BandDynamicRange) / (double)core.Count;

            return new SurveyMeasurement(roverBands, Db.FromMeanSquare(rover.A / rover.Dur), Db.FromMeanSquare(rover.C / rover.Dur), refs, rover.Dur,
                ZoneModel.Confidence(rover.Dur, minSeconds, refs, valid));
        }
    }
}
