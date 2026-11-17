namespace Rta.Core.Calibration;

/// <summary>How much to trust a calibration. Derived from the method and reference used.</summary>
public enum CalibrationQuality
{
    /// <summary>Manual entry or legacy profile; accuracy not known.</summary>
    Unknown,
    /// <summary>About ±0.7 dB or better (acoustic calibrator, Class 1 meter).</summary>
    High,
    /// <summary>About ±1.6 dB or better (Class 2 meter).</summary>
    Medium,
    /// <summary>Unreliable: about ±3.5 dB (phone app, consumer meter).</summary>
    Low,
    /// <summary>Very rough: worse than ±3.5 dB (spec-sheet estimate, Android phone).</summary>
    VeryLow,
}

public enum MeterClass { Class1, Class2, Consumer }

public enum CalibratorClass { Class1, Class2, Unknown }

public enum PhoneKind { IPhone, Android }

public static class CalibrationAccuracy
{
    /// <summary>Typical uncertainty (±dB) of each reference.</summary>
    public static double Calibrator(CalibratorClass c) => c switch
    {
        CalibratorClass.Class1 => 0.4,
        CalibratorClass.Class2 => 0.6,
        _ => 0.8,
    };

    public static double Meter(MeterClass c) => c switch
    {
        MeterClass.Class1 => 1.0,
        MeterClass.Class2 => 1.5,
        _ => 3.0,
    };

    public static double Phone(PhoneKind p) => p == PhoneKind.IPhone ? 3.0 : 6.0;

    public const double SpecEstimate = 6.0;

    /// <summary>Extra uncertainty added when copying a calibration between two mics side by side.</summary>
    public const double TransferStep = 0.5;

    public static CalibrationQuality FromUncertainty(double? uncertaintyDb) => uncertaintyDb switch
    {
        null => CalibrationQuality.Unknown,
        <= 0.7 => CalibrationQuality.High,
        <= 1.6 => CalibrationQuality.Medium,
        <= 3.5 => CalibrationQuality.Low,
        _ => CalibrationQuality.VeryLow,
    };

    public static bool IsUnreliable(CalibrationQuality q) => q is CalibrationQuality.Low or CalibrationQuality.VeryLow;

    public static string Label(CalibrationQuality q) => q switch
    {
        CalibrationQuality.High => "Reference grade",
        CalibrationQuality.Medium => "Good",
        CalibrationQuality.Low => "Unreliable",
        CalibrationQuality.VeryLow => "Very rough",
        _ => "Accuracy unknown",
    };
}
