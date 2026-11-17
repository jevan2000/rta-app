using Rta.Core.Dsp;
using Rta.Core.Metering;

namespace Rta.Core.Zones;

/// <summary>What one permanent ("reference") mic measured during a survey.</summary>
public sealed record SurveyRef(
    string MicKey,
    double[]? Bands,
    double LA,
    double LC,
    /// <summary>The ref's calibration offset when surveyed; a later change is compensated.</summary>
    double OffsetDbAtSurvey,
    string? ProfileIdAtSurvey,
    /// <summary>Std dev (dB) of the rover−ref C offset across ~5 s chunks: how consistently this ref tracks the zone.</summary>
    double ChunkStdC,
    int Chunks);

/// <summary>One survey of a zone: the roving mic and all refs over the same window.</summary>
public sealed record SurveyMeasurement(
    double[]? RoverBands,
    double RoverLA,
    double RoverLC,
    IReadOnlyList<SurveyRef> Refs,
    double Seconds,
    double Confidence)
{
    public bool HasBands => RoverBands is not null && Refs.Any(r => r.Bands is not null);
}

public sealed record RefModel(
    string MicKey,
    /// <summary>Per-band offset (zone − ref), dB; NaN where the band had too little signal to trust.</summary>
    double[]? BandOffsets,
    double OffsetA,
    double OffsetC,
    double Weight,
    double OffsetDbAtSurvey,
    string? ProfileIdAtSurvey);

/// <summary>The fitted model for one zone.</summary>
public sealed record ZoneModelData(
    IReadOnlyList<RefModel> Refs,
    /// <summary>Broadband minus band-sum correction at the rover (covers bands outside 20 Hz–20 kHz and filter overlap).</summary>
    double KA,
    double KC,
    bool BandMode,
    double Confidence,
    int Surveys);

/// <summary>A reference mic's current reading, as the predictor needs it.</summary>
public sealed record RefReading(double[]? Bands, double LA, double LC, double CurrentOffsetDb);

public sealed record ZonePrediction(double LA, double LC, double[]? Bands, int RefsUsed, int RefsInModel, bool CalibrationCompensated);

/// <summary>
/// Zone prediction from permanent mics. For each reference mic r and band b the survey gives an
/// offset O[r,b] = zone[b] − ref_r[b]. Live, each ref predicts zone[b] = ref_r[b] + O[r,b]; refs are
/// combined by a weighted mean (weights from how steadily each ref tracked the zone during the
/// survey). A/C totals are the weighted band sums plus a small broadband correction measured at
/// the rover. Without band data (manual entries) the model falls back to broadband offsets.
/// </summary>
public static class ZoneModel
{
    /// <summary>Bands more than this far below the loudest band during the survey are treated as noise.</summary>
    public const double BandDynamicRange = 50;

    public static double BandSum(double[] bands, IReadOnlyList<double> bandsHz, Weighting w)
    {
        double e = 0;
        for (var i = 0; i < bands.Length && i < bandsHz.Count; i++)
        {
            if (double.IsNaN(bands[i]) || bands[i] <= Db.FloorDb + 1) continue;
            e += Math.Pow(10, (bands[i] + FrequencyWeighting.AnalogDb(w, ExactCenter(bandsHz[i]))) / 10);
        }
        return e > 0 ? 10 * Math.Log10(e) : Db.FloorDb;
    }

    /// <summary>Exact base-10 centre for a nominal 1/3-octave frequency.</summary>
    public static double ExactCenter(double nominal) =>
        1000 * Math.Pow(10, Math.Round(10 * Math.Log10(nominal / 1000)) / 10);

    public static double RefWeight(double chunkStd, int chunks) =>
        1 / (chunkStd * chunkStd / Math.Max(1, Math.Min(chunks, 12)) + 0.25);

    /// <summary>Fits one zone from one or more surveys (each weighted, e.g. by confidence).</summary>
    public static ZoneModelData Build(IReadOnlyList<(SurveyMeasurement Survey, double Weight)> surveys, IReadOnlyList<double> bandsHz)
    {
        if (surveys.Count == 0) throw new ArgumentException("No surveys.");
        var bandMode = surveys.All(s => s.Survey.HasBands);
        var nb = bandsHz.Count;

        // Per survey, per ref: offsets.
        var perRef = new Dictionary<string, List<(double W, double[]? Bands, double A, double C, double SW, SurveyRef R)>>();
        double kaSum = 0, kcSum = 0, kW = 0;
        foreach (var (s, w) in surveys)
        {
            if (bandMode)
            {
                kaSum += (s.RoverLA - BandSum(s.RoverBands!, bandsHz, Weighting.A)) * w;
                kcSum += (s.RoverLC - BandSum(s.RoverBands!, bandsHz, Weighting.C)) * w;
                kW += w;
            }
            foreach (var r in s.Refs)
            {
                double[]? off = null;
                if (bandMode && r.Bands is not null)
                {
                    off = new double[nb];
                    var roverMax = s.RoverBands!.Max();
                    var refMax = r.Bands.Max();
                    for (var b = 0; b < nb; b++)
                    {
                        var zv = s.RoverBands![b]; var rv = r.Bands[b];
                        var ok = zv > roverMax - BandDynamicRange && rv > refMax - BandDynamicRange && zv > Db.FloorDb + 1 && rv > Db.FloorDb + 1;
                        off[b] = ok ? zv - rv : double.NaN;
                    }
                }
                var la = double.IsNaN(s.RoverLA) ? double.NaN : s.RoverLA - r.LA;
                var lc = double.IsNaN(s.RoverLC) ? double.NaN : s.RoverLC - r.LC;
                if (!perRef.TryGetValue(r.MicKey, out var list)) perRef[r.MicKey] = list = [];
                list.Add((w, off, la, lc, RefWeight(r.ChunkStdC, r.Chunks), r));
            }
        }

        var refs = new List<RefModel>();
        foreach (var (key, list) in perRef)
        {
            double[]? bands = null;
            if (bandMode)
            {
                bands = new double[nb];
                for (var b = 0; b < nb; b++)
                {
                    double sum = 0, ws = 0;
                    foreach (var x in list) if (x.Bands is not null && !double.IsNaN(x.Bands[b])) { sum += x.Bands[b] * x.W; ws += x.W; }
                    bands[b] = ws > 0 ? sum / ws : double.NaN;
                }
            }
            double Avg(Func<(double W, double[]? Bands, double A, double C, double SW, SurveyRef R), double> f)
            {
                double sum = 0, ws = 0;
                foreach (var x in list) { var v = f(x); if (!double.IsNaN(v)) { sum += v * x.W; ws += x.W; } }
                return ws > 0 ? sum / ws : double.NaN;
            }
            var offC = Avg(x => x.C);
            var offA = Avg(x => x.A);
            if (double.IsNaN(offA)) offA = offC;   // manual entry with only dBC
            if (double.IsNaN(offC)) offC = offA;
            var latest = list[^1].R;
            refs.Add(new RefModel(key, bands, offA, offC, Avg(x => x.SW), latest.OffsetDbAtSurvey, latest.ProfileIdAtSurvey));
        }

        var conf = surveys.Sum(s => s.Survey.Confidence * s.Weight) / Math.Max(1e-9, surveys.Sum(s => s.Weight));
        // Averaging several surveys adds a little confidence.
        if (surveys.Count > 1) conf = Math.Min(1, conf * (1 + 0.1 * (surveys.Count - 1)));
        return new ZoneModelData(refs, kW > 0 ? kaSum / kW : 0, kW > 0 ? kcSum / kW : 0, bandMode, conf, surveys.Count);
    }

    /// <summary>Predicts the zone from whichever reference mics are currently available.</summary>
    public static ZonePrediction? Predict(ZoneModelData model, IReadOnlyDictionary<string, RefReading> readings, IReadOnlyList<double> bandsHz)
    {
        var used = model.Refs.Where(r => readings.ContainsKey(r.MicKey)).ToList();
        if (used.Count == 0) return null;
        var compensated = false;

        if (model.BandMode && used.All(r => readings[r.MicKey].Bands is not null))
        {
            var nb = bandsHz.Count;
            var bands = new double[nb];
            for (var b = 0; b < nb; b++)
            {
                double sum = 0, ws = 0;
                foreach (var r in used)
                {
                    var rd = readings[r.MicKey];
                    if (b >= rd.Bands!.Length || rd.Bands[b] <= Db.FloorDb + 1) continue;
                    var comp = rd.CurrentOffsetDb - r.OffsetDbAtSurvey;
                    if (Math.Abs(comp) > 0.01) compensated = true;
                    var off = r.BandOffsets is not null && !double.IsNaN(r.BandOffsets[b]) ? r.BandOffsets[b] : r.OffsetC;
                    sum += (rd.Bands[b] - comp + off) * r.Weight;
                    ws += r.Weight;
                }
                bands[b] = ws > 0 ? sum / ws : Db.FloorDb;
            }
            return new ZonePrediction(
                BandSum(bands, bandsHz, Weighting.A) + model.KA,
                BandSum(bands, bandsHz, Weighting.C) + model.KC,
                bands, used.Count, model.Refs.Count, compensated);
        }

        double la = 0, lc = 0, w = 0;
        double[]? bandsB = null;
        foreach (var r in used)
        {
            var rd = readings[r.MicKey];
            var comp = rd.CurrentOffsetDb - r.OffsetDbAtSurvey;
            if (Math.Abs(comp) > 0.01) compensated = true;
            la += (rd.LA - comp + r.OffsetA) * r.Weight;
            lc += (rd.LC - comp + r.OffsetC) * r.Weight;
            w += r.Weight;
            if (rd.Bands is not null)
            {
                bandsB ??= new double[rd.Bands.Length];
                for (var b = 0; b < bandsB.Length && b < rd.Bands.Length; b++) bandsB[b] += (rd.Bands[b] - comp + r.OffsetC) * r.Weight;
            }
        }
        if (bandsB is not null) for (var b = 0; b < bandsB.Length; b++) bandsB[b] /= w;
        return new ZonePrediction(la / w, lc / w, bandsB, used.Count, model.Refs.Count, compensated);
    }

    /// <summary>0–1 score for a measured survey.</summary>
    public static double Confidence(double seconds, double minSeconds, IReadOnlyList<SurveyRef> refs, double validBandFraction)
    {
        if (refs.Count == 0) return 0;
        var duration = Math.Min(1, seconds / Math.Max(1, minSeconds));
        var best = refs.Where(r => r.Chunks >= 2).Select(r => r.ChunkStdC / Math.Sqrt(r.Chunks)).DefaultIfEmpty(2).Min();
        var stability = 1 / (1 + (best / 0.5) * (best / 0.5));
        return Math.Round(Math.Clamp(duration * stability * (0.5 + 0.5 * validBandFraction), 0, 1), 3);
    }
}
