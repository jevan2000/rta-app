using Rta.Core.Dsp;
using Rta.Core.Metering;

namespace Rta.Core.Calibration;

public sealed record CalibrationResult(
    double? MeasuredDb,
    double OffsetDb,
    double StabilityDb,
    double? DominantBandHz,
    double DurationSeconds,
    IReadOnlyList<string> Warnings,
    double? UncertaintyDb = null,
    CalibrationQuality Quality = CalibrationQuality.Unknown,
    /// <summary>Notes that always apply to the method (e.g. "phone apps are unreliable"); not problems with this capture.</summary>
    IReadOnlyList<string>? Notes = null)
{
    public bool Ok => Warnings.Count == 0;
    public string QualityLabel => CalibrationAccuracy.Label(Quality);
}

/// <summary>Turns captured raw blocks into a calibration offset, for every supported method.</summary>
public static class CalibrationCalculator
{
    /// <summary>
    /// Acoustic calibrator (e.g. 94 or 114 dB at 1 kHz; pistonphones at 250 Hz). Uses the
    /// Z-weighted Leq of the capture.
    /// </summary>
    public static CalibrationResult FromCalibrator(IReadOnlyList<MeterBlock> blocks, IReadOnlyList<OctaveBand> bands,
        double referenceDb, double expectedToneHz = 1000, CalibratorClass calibratorClass = CalibratorClass.Class2)
    {
        var warnings = new List<string>();
        var (leq, dur, stability) = Summarise(blocks, b => b.MsZ);
        var dominant = DominantBand(blocks, bands, out var dominantFraction);

        if (dominant is double hz && Math.Abs(Math.Log2(hz / expectedToneHz)) > 0.2)
            warnings.Add($"Strongest band is {hz:0.#} Hz, expected ~{expectedToneHz:0} Hz. Is the calibrator on and seated on the mic?");
        else if (dominantFraction < 0.9)
            warnings.Add($"Only {dominantFraction:P0} of energy is in the calibrator band — background noise may be affecting the reading.");

        CommonChecks(blocks, dur, stability, warnings);
        var u = CalibrationAccuracy.Calibrator(calibratorClass);
        return new CalibrationResult(leq, referenceDb - leq, stability, dominant, dur, warnings, u, CalibrationAccuracy.FromUncertainty(u));
    }

    /// <summary>
    /// Match against a reference SPL meter. The meter should show Leq (or Slow on a steady signal
    /// such as pink noise) over the same period with the same weighting.
    /// </summary>
    public static CalibrationResult FromMeterMatch(IReadOnlyList<MeterBlock> blocks, IReadOnlyList<OctaveBand> bands,
        double referenceDb, Weighting weighting, MeterClass meterClass = MeterClass.Class2)
    {
        var u = CalibrationAccuracy.Meter(meterClass);
        var notes = new List<string>();
        if (meterClass == MeterClass.Consumer)
            notes.Add("Consumer or unrated meters can be several dB off. Treat readings as approximate.");
        return MatchReference(blocks, bands, referenceDb, weighting, u, notes);
    }

    /// <summary>Match against a phone sound-meter app. Always marked unreliable.</summary>
    public static CalibrationResult FromPhoneApp(IReadOnlyList<MeterBlock> blocks, IReadOnlyList<OctaveBand> bands,
        double referenceDb, Weighting weighting, PhoneKind phone)
    {
        var notes = new List<string>
        {
            phone == PhoneKind.IPhone
                ? "Phone apps are an unreliable reference (about ±3 dB on iPhone with a good app such as NIOSH SLM). Recalibrate with a meter or calibrator when you can."
                : "Phone apps are an unreliable reference; Android phone mics vary widely (±6 dB or worse). Recalibrate with a meter or calibrator when you can.",
        };
        return MatchReference(blocks, bands, referenceDb, weighting, CalibrationAccuracy.Phone(phone), notes);
    }

    private static CalibrationResult MatchReference(IReadOnlyList<MeterBlock> blocks, IReadOnlyList<OctaveBand> bands,
        double referenceDb, Weighting weighting, double uncertainty, List<string> notes)
    {
        var warnings = new List<string>();
        var (leq, dur, stability) = Summarise(blocks, Selector(weighting));
        var dominant = DominantBand(blocks, bands, out _);
        CommonChecks(blocks, dur, stability, warnings, stabilityLimit: 3.0);
        return new CalibrationResult(leq, referenceDb - leq, stability, dominant, dur, warnings,
            uncertainty, CalibrationAccuracy.FromUncertainty(uncertainty), notes);
    }

    /// <summary>
    /// Copies a calibration from an already-calibrated mic to one placed right next to it. Both
    /// must be captured over the same period (blocks paired by index). Uncertainty is the source's
    /// plus the transfer step plus how much the two mics disagreed second to second.
    /// </summary>
    public static CalibrationResult FromTransfer(IReadOnlyList<MeterBlock> target, IReadOnlyList<MeterBlock> reference,
        IReadOnlyList<OctaveBand> bands, CalibrationProfile referenceProfile, Weighting weighting = Weighting.C)
    {
        var warnings = new List<string>();
        var n = Math.Min(target.Count, reference.Count);
        var t = target.Take(n).ToList();
        var r = reference.Take(n).ToList();
        var sel = Selector(weighting);

        var (targetLeq, dur, _) = Summarise(t, sel);
        var (refLeq, _, _) = Summarise(r, sel);
        var refSpl = refLeq + referenceProfile.OffsetDb;

        // Mic-to-mic agreement over ~1 s groups (100 ms blocks are too noisy with music).
        var diffs = new List<double>();
        for (var i = 0; i + 10 <= n; i += 10)
        {
            var tg = Db.FromMeanSquare(t.Skip(i).Take(10).Average(sel));
            var rg = Db.FromMeanSquare(r.Skip(i).Take(10).Average(sel));
            diffs.Add(rg - tg);
        }
        var spread = diffs.Count > 1 ? StdDev(diffs) : 0;

        CommonChecks(t, dur, 0, warnings);
        if (r.Any(b => b.Clipped)) warnings.Add("The reference mic clipped during capture.");
        if (spread > 1.0)
            warnings.Add($"The two mics disagreed by ±{spread:0.0} dB second to second. Put the capsules closer together (within a few cm, pointing the same way) and use steady pink noise.");
        if (refSpl < 60)
            warnings.Add($"Signal at the reference mic was only {refSpl:0} dB. Use louder pink noise (75–90 dB) so room noise doesn't dominate.");

        var notes = new List<string> { $"Copied from '{referenceProfile.Name}'. This mic can only be as accurate as that one." };
        double? u = referenceProfile.UncertaintyDb is double su
            ? Math.Sqrt(su * su + CalibrationAccuracy.TransferStep * CalibrationAccuracy.TransferStep + spread * spread / Math.Max(1, diffs.Count))
            : null;
        if (u is null) notes.Add("The source profile's accuracy is unknown, so this one's is too.");
        else if (CalibrationAccuracy.IsUnreliable(referenceProfile.Quality))
            notes.Add("The source was calibrated with an unreliable method, so this profile is unreliable too.");

        return new CalibrationResult(targetLeq, refSpl - targetLeq, spread, DominantBand(t, bands, out _), dur, warnings,
            u, CalibrationAccuracy.FromUncertainty(u), notes);
    }

    /// <summary>
    /// Estimate from published specs, no measurement. The interface's maximum input level is taken
    /// as the RMS level of a full-scale sine at minimum gain; <paramref name="gainAboveMinDb"/> is how
    /// far the preamp is turned up from minimum.
    /// </summary>
    public static CalibrationResult FromSpecs(double micSensitivityMvPerPa, double maxInputDbuAtMinGain, double gainAboveMinDb)
    {
        if (micSensitivityMvPerPa <= 0) throw new ArgumentOutOfRangeException(nameof(micSensitivityMvPerPa), "Sensitivity must be positive.");
        var sensDbv = 20 * Math.Log10(micSensitivityMvPerPa / 1000);      // dBV at 1 Pa (94 dB SPL)
        var fullScaleDbv = maxInputDbuAtMinGain - 2.2185 - gainAboveMinDb; // dBu → dBV
        var splAtFullScaleSine = 94 + fullScaleDbv - sensDbv;
        // Our dBFS reads a full-scale sine as -3.01 dB, so offset = SPL - (-3.01).
        var offset = splAtFullScaleSine + 10 * Math.Log10(2);
        var notes = new List<string>
        {
            "Estimate from spec sheets only — very rough (±6 dB or worse). Mic sensitivity varies between units and analog gain knobs are imprecise.",
            "Use this only until you can calibrate with a meter or calibrator.",
        };
        var warnings = new List<string>();
        if (gainAboveMinDb is < 0 or > 80) warnings.Add("Gain above minimum looks out of range (expected 0–80 dB).");
        if (micSensitivityMvPerPa is < 1 or > 100) warnings.Add("Mic sensitivity looks unusual (measurement mics are typically 5–50 mV/Pa).");
        return new CalibrationResult(null, offset, 0, null, 0, warnings,
            CalibrationAccuracy.SpecEstimate, CalibrationAccuracy.FromUncertainty(CalibrationAccuracy.SpecEstimate), notes);
    }

    private static Func<MeterBlock, double> Selector(Weighting w) => w switch
    {
        Weighting.A => b => b.MsA,
        Weighting.C => b => b.MsC,
        _ => b => b.MsZ,
    };

    private static void CommonChecks(IReadOnlyList<MeterBlock> blocks, double dur, double stability, List<string> warnings, double stabilityLimit = 0.3)
    {
        if (blocks.Any(b => b.Clipped)) warnings.Add("Input clipped during capture — reduce preamp gain and recalibrate.");
        if (dur < 2.9) warnings.Add($"Capture was only {dur:0.0} s; at least 3 s is recommended.");
        if (stability > stabilityLimit) warnings.Add($"Level varied by {stability:0.00} dB (std dev) during capture; reading may be unreliable.");
        var leq = blocks.Count == 0 ? Db.FloorDb : Db.FromMeanSquare(blocks.Average(b => b.MsZ));
        if (leq < -80) warnings.Add("Signal is very low — check phantom power and preamp gain.");
    }

    private static (double LeqDb, double Duration, double StdDevDb) Summarise(IReadOnlyList<MeterBlock> blocks, Func<MeterBlock, double> ms)
    {
        if (blocks.Count == 0) return (Db.FloorDb, 0, 0);
        double energy = 0, dur = 0;
        foreach (var b in blocks) { energy += ms(b) * b.DurationSeconds; dur += b.DurationSeconds; }
        var levels = blocks.Select(b => Db.FromMeanSquare(ms(b))).ToList();
        return (Db.FromMeanSquare(energy / dur), dur, StdDev(levels));
    }

    private static double StdDev(IReadOnlyList<double> v)
    {
        var mean = v.Average();
        return Math.Sqrt(v.Select(l => (l - mean) * (l - mean)).Average());
    }

    private static double? DominantBand(IReadOnlyList<MeterBlock> blocks, IReadOnlyList<OctaveBand> bands, out double fraction)
    {
        fraction = 0;
        if (blocks.Count == 0 || bands.Count == 0) return null;
        var sums = new double[bands.Count];
        foreach (var b in blocks)
            for (var i = 0; i < sums.Length && i < b.BandMs.Length; i++) sums[i] += b.BandMs[i];
        var total = sums.Sum();
        if (total <= 0) return null;
        var max = 0;
        for (var i = 1; i < sums.Length; i++) if (sums[i] > sums[max]) max = i;
        fraction = sums[max] / total;
        return bands[max].NominalHz;
    }
}
