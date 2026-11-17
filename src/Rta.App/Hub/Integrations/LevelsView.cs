using System.Text;

namespace Rta.App.Hub.Integrations;

/// <summary>One mic or zone as exposed to other systems: a stable short name and its current values.</summary>
public sealed record LevelSource(
    string Key, string Slug, string Kind, string Label, bool Online, bool Calibrated,
    IReadOnlyDictionary<string, double?> Values, string? AlertLevel);

public sealed record LevelsSnapshot(
    long T, string Hub, string? Service, long? ServiceStart,
    IReadOnlyList<LevelSource> Mics, IReadOnlyList<LevelSource> Zones,
    IReadOnlyList<ActiveAlertView> Alerts);

/// <summary>
/// Flattens the Hub's live state for integrations. Metric names are lowercase and stable:
/// lcs, las, lceq1m, laeq1m, lceq15m, laeq15m, lceqservice, laeqservice, lcpeak1m, cminusa.
/// </summary>
public static class LevelsView
{
    public static readonly (string Name, AlertMetric? Metric)[] Metrics =
    [
        ("lcs", AlertMetric.LCS), ("las", AlertMetric.LAS),
        ("lceq1m", AlertMetric.LCeq1m), ("laeq1m", AlertMetric.LAeq1m),
        ("lceq15m", AlertMetric.LCeq15m), ("laeq15m", AlertMetric.LAeq15m),
        ("lceqservice", AlertMetric.LCeqService), ("laeqservice", AlertMetric.LAeqService),
        ("lcpeak1m", AlertMetric.LCpeakMax1m), ("cminusa", null),
    ];

    public static string MetricName(string metric) => metric.ToLowerInvariant() switch
    {
        "lcpeakmax1m" => "lcpeak1m",
        var m => m,
    };

    public static LevelsSnapshot Build(HubLive live)
    {
        var alerts = live.Alerts;
        string? AlertFor(string key) =>
            alerts.Where(a => a.MicKey == key).Select(a => a.Level).OrderByDescending(l => l == "alarm").FirstOrDefault();

        var micSlugs = UniqueSlugs(live.Mics.Where(m => !m.Hidden).Select(m => (m.Key, m.Label, m.NodeName)));
        var mics = live.Mics.Where(m => !m.Hidden).Select(m => Source(m, "mic", micSlugs[m.Key], AlertFor(m.Key))).ToList();

        var zoneViews = live.Zones.Where(z => z.Surveyed).Select(HubState.ZoneAsMic).ToList();
        var zoneSlugs = UniqueSlugs(zoneViews.Select(z => (z.Key, z.Label, "zone")));
        var zones = zoneViews.Select(z => Source(z, "zone", zoneSlugs[z.Key], AlertFor(z.Key))).ToList();

        return new LevelsSnapshot(live.T, live.HubName, live.Service?.Name, live.Service?.StartTs, mics, zones, alerts);
    }

    private static LevelSource Source(MicLiveView m, string kind, string slug, string? alert)
    {
        var values = new Dictionary<string, double?>();
        foreach (var (name, metric) in Metrics)
        {
            double? v = metric is AlertMetric am ? HubState.MetricValue(m, am)
                : m.Live is { Coverage1m: >= 5 } l ? l.CMinusA : null;
            values[name] = v is double d ? Math.Round(d, 1) : null;
        }
        return new LevelSource(m.Key, slug, kind, m.Label, m.Online, m.Calibrated, values, alert);
    }

    public static string Slug(string s)
    {
        var sb = new StringBuilder();
        // Strip accents first so "Ünder" becomes "under", not "nder".
        foreach (var c in s.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        }
        var slug = string.Join('_', sb.ToString().Split('_', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "x" : slug;
    }

    /// <summary>Slugs from labels; duplicates get the node name, then a number.</summary>
    private static Dictionary<string, string> UniqueSlugs(IEnumerable<(string Key, string Label, string Node)> items)
    {
        var list = items.ToList();
        var counts = list.GroupBy(x => Slug(x.Label)).ToDictionary(g => g.Key, g => g.Count());
        var result = new Dictionary<string, string>();
        var used = new HashSet<string>();
        foreach (var (key, label, node) in list)
        {
            var slug = Slug(label);
            if (counts[slug] > 1) slug = $"{Slug(node)}_{slug}";
            var s = slug;
            for (var i = 2; !used.Add(s); i++) s = $"{slug}_{i}";
            result[key] = s;
        }
        return result;
    }

    public static LevelSource? Find(LevelsSnapshot s, string slugOrKey) =>
        s.Mics.Concat(s.Zones).FirstOrDefault(x => x.Slug == slugOrKey || x.Key == slugOrKey);

    public static string Format(double? v) => v is double d ? d.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "";
}
