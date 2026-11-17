using System.Globalization;
using Rta.Core.Dsp;

namespace Rta.Core.Calibration;

public enum CalibrationMethod
{
    Calibrator,
    MeterMatch,
    Manual,
    /// <summary>Matched to a phone sound-meter app. Always unreliable.</summary>
    PhoneApp,
    /// <summary>Copied from another calibrated mic placed side by side.</summary>
    Transfer,
    /// <summary>Estimated from mic sensitivity and interface specs; no measurement.</summary>
    SpecEstimate,
}

public sealed record CorrectionPoint(double FrequencyHz, double Db);

/// <summary>
/// A free-standing calibration. Assigned to an input by the user; the mic/channel fields are
/// optional metadata used for suggestions and mismatch warnings — they never block use.
/// Calibrated SPL = dBFS(mean square) + OffsetDb.
/// </summary>
public sealed record CalibrationProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public double OffsetDb { get; init; }
    public CalibrationMethod Method { get; init; } = CalibrationMethod.Manual;
    public double? ReferenceDb { get; init; }
    public Weighting? ReferenceWeighting { get; init; }

    /// <summary>How much to trust this profile, and its typical uncertainty (±dB).</summary>
    public CalibrationQuality Quality { get; init; } = CalibrationQuality.Unknown;
    public double? UncertaintyDb { get; init; }
    /// <summary>What the reference was, e.g. "Class 2 meter", "iPhone · NIOSH SLM", "Calibrator 94 dB @ 1 kHz".</summary>
    public string? ReferenceDescription { get; init; }
    /// <summary>For <see cref="CalibrationMethod.Transfer"/>: the profile it was copied from.</summary>
    public string? SourceProfileId { get; init; }
    public string? SourceProfileName { get; init; }

    // Optional bindings / metadata
    public string? MicName { get; init; }
    public string? MicSerial { get; init; }
    public string? DeviceName { get; init; }
    public int? ChannelIndex { get; init; }
    public string? GainNote { get; init; }
    public double? SampleRate { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Optional microphone response deviation (dB) by frequency. The app subtracts it, i.e. a
    /// mic reading +2 dB high at 8 kHz has a point (8000, +2). Applied to RTA bands.
    /// </summary>
    public IReadOnlyList<CorrectionPoint>? Correction { get; init; }
}

/// <summary>Context of the live input a profile is assigned to, for mismatch checks.</summary>
public sealed record InputContext(string? DeviceName, int ChannelIndex, string? MicLabel, double SampleRate);

public static class CalibrationChecks
{
    public static IReadOnlyList<string> Warnings(CalibrationProfile? profile, InputContext input)
    {
        var w = new List<string>();
        if (profile is null) { w.Add("Uncalibrated: readings are relative (dBFS), not dB SPL."); return w; }

        if (!string.IsNullOrWhiteSpace(profile.MicName) && !string.IsNullOrWhiteSpace(input.MicLabel) &&
            !string.Equals(profile.MicName.Trim(), input.MicLabel.Trim(), StringComparison.OrdinalIgnoreCase))
            w.Add($"Profile was made for mic '{profile.MicName}' but this input is labelled '{input.MicLabel}'.");

        if (profile.ChannelIndex is int ch && ch != input.ChannelIndex)
            w.Add($"Profile was made on input {ch + 1}; it is assigned to input {input.ChannelIndex + 1}. Preamp gain may differ.");

        if (!string.IsNullOrWhiteSpace(profile.DeviceName) && !string.IsNullOrWhiteSpace(input.DeviceName) &&
            !string.Equals(profile.DeviceName, input.DeviceName, StringComparison.OrdinalIgnoreCase))
            w.Add($"Profile was made on '{profile.DeviceName}', not '{input.DeviceName}'.");

        if (CalibrationAccuracy.IsUnreliable(profile.Quality))
            w.Add($"{CalibrationAccuracy.Label(profile.Quality)} calibration ({MethodName(profile.Method)}{(profile.UncertaintyDb is double u ? $", about ±{u:0.#} dB" : "")}). Treat readings as approximate.");

        if ((DateTimeOffset.UtcNow - profile.CreatedUtc).TotalDays > 365)
            w.Add("Calibration is more than a year old; consider recalibrating.");

        return w;
    }

    public static string MethodName(CalibrationMethod m) => m switch
    {
        CalibrationMethod.Calibrator => "acoustic calibrator",
        CalibrationMethod.MeterMatch => "SPL meter match",
        CalibrationMethod.PhoneApp => "phone app",
        CalibrationMethod.Transfer => "copied from another mic",
        CalibrationMethod.SpecEstimate => "spec-sheet estimate",
        _ => "manual offset",
    };

    /// <summary>Scores how well a profile matches an input (higher = better; 0 = no metadata match).</summary>
    public static int MatchScore(CalibrationProfile p, InputContext input)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(p.MicName) && string.Equals(p.MicName.Trim(), input.MicLabel?.Trim(), StringComparison.OrdinalIgnoreCase)) score += 4;
        if (p.ChannelIndex == input.ChannelIndex) score += 2;
        if (!string.IsNullOrWhiteSpace(p.DeviceName) && string.Equals(p.DeviceName, input.DeviceName, StringComparison.OrdinalIgnoreCase)) score += 1;
        return score;
    }
}

public static class CorrectionCurve
{
    /// <summary>
    /// Parses a frequency-response text file (REW/.frd/miniDSP style): any line whose first two
    /// numeric tokens are frequency and dB. Headers, comments and extra columns are ignored.
    /// </summary>
    public static List<CorrectionPoint> Parse(string text)
    {
        var points = new List<CorrectionPoint>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '*' or '#' or '"' or ';') continue;
            var tokens = line.Split([' ', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;
            if (!double.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ||
                !double.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var db)) continue;
            if (f <= 0) continue;
            points.Add(new CorrectionPoint(f, db));
        }
        points.Sort((a, b) => a.FrequencyHz.CompareTo(b.FrequencyHz));
        return points;
    }

    /// <summary>Linear interpolation on a log-frequency axis; clamps outside the curve's range.</summary>
    public static double InterpolateDb(IReadOnlyList<CorrectionPoint>? curve, double f)
    {
        if (curve is null || curve.Count == 0) return 0;
        if (f <= curve[0].FrequencyHz) return curve[0].Db;
        if (f >= curve[^1].FrequencyHz) return curve[^1].Db;
        var lo = 0; var hi = curve.Count - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (curve[mid].FrequencyHz <= f) lo = mid; else hi = mid;
        }
        var a = curve[lo]; var b = curve[hi];
        var t = (Math.Log(f) - Math.Log(a.FrequencyHz)) / (Math.Log(b.FrequencyHz) - Math.Log(a.FrequencyHz));
        return a.Db + t * (b.Db - a.Db);
    }
}
