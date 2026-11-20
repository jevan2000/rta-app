using System.Text.RegularExpressions;

namespace Rta.Audio;

/// <summary>
/// Recognises the same audio interface after Windows renumbers it. A WASAPI device gets a new id when
/// it's plugged into another USB port, and often a "2- " in its name ("Microphone (2- UMC404HD 192k)"),
/// so devices are matched on their name with that numbering removed.
/// </summary>
public static partial class DeviceMatch
{
    /// <summary>"Microphone (2- UMC404HD 192k)" → "microphone (umc404hd 192k)".</summary>
    public static string Model(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = InstanceInParens().Replace(name, "(");
        s = LeadingInstance().Replace(s, "");
        return Spaces().Replace(s.ToLowerInvariant(), " ").Trim();
    }

    public static bool SameModel(string? a, string? b) => Model(a) is { Length: > 0 } m && m == Model(b);

    [GeneratedRegex(@"\(\s*\d+\s*-\s*")] private static partial Regex InstanceInParens();
    [GeneratedRegex(@"^\s*\d+\s*-\s*")] private static partial Regex LeadingInstance();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
}
