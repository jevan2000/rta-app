using Rta.Core;
using System.Text.Json;
using Rta.App.Node;
using Rta.Core.Calibration;

namespace Rta.App.Hub;

public sealed record Pt(double X, double Y);

public enum ZoneSurveyMode { Replace, Average }
public enum MicRole { Permanent, Roving }

/// <summary>A drawn element of the blueprint. Units are metres; y grows downward (screen space).</summary>
public sealed record PlanShape
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>room | stage | seating | aisle | wall | label | other</summary>
    public string Kind { get; init; } = "room";
    public List<Pt> Points { get; init; } = [];
    public string? Label { get; init; }
}

public sealed record PlanZone
{
    /// <summary>Hub-wide zone number (0 = new; the Hub assigns one).</summary>
    public int Id { get; init; }
    public string Name { get; init; } = "Zone";
    public List<Pt> Points { get; init; } = [];
    public ZoneSurveyMode Mode { get; init; } = ZoneSurveyMode.Replace;
}

public sealed record MicPlacement(string MicKey, double X, double Y, MicRole Role = MicRole.Permanent);

public sealed record Underlay(string ImageId, double X, double Y, double Width, double Height, double Opacity = 0.45);

public sealed record Floor
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; init; } = "Main floor";
    public double Width { get; init; } = 30;
    public double Height { get; init; } = 20;
    public Underlay? Underlay { get; init; }
    public List<PlanShape> Shapes { get; init; } = [];
    public List<PlanZone> Zones { get; init; } = [];
    public List<MicPlacement> Mics { get; init; } = [];
}

public sealed record Plan
{
    public List<Floor> Floors { get; init; } = [new Floor()];

    public IEnumerable<PlanZone> AllZones => Floors.SelectMany(f => f.Zones);
    public IEnumerable<(Floor Floor, MicPlacement Mic)> AllMics => Floors.SelectMany(f => f.Mics.Select(m => (f, m)));
    public PlanZone? Zone(int id) => AllZones.FirstOrDefault(z => z.Id == id);
    public MicPlacement? Mic(string key) => AllMics.Select(x => x.Mic).FirstOrDefault(m => m.MicKey == key);
}

/// <summary>Stores the blueprint (plan.json) and underlay images in the data folder.</summary>
public sealed class PlanStore
{
    public const long MaxImageBytes = 15 * 1024 * 1024;
    private readonly string _file;
    private readonly string _imageDir;
    private readonly string _historyDir;
    private readonly object _lock = new();
    public const int HistoryKept = 30;
    private Plan _plan;

    public event Action? Changed;

    public PlanStore(NodePaths paths)
    {
        _file = Path.Combine(paths.DataDir, "plan.json");
        _imageDir = Path.Combine(paths.DataDir, "plan-images");
        _historyDir = Path.Combine(paths.DataDir, "plan-history");
        Directory.CreateDirectory(_imageDir);
        _plan = SafeFile.ReadJson<Plan>(_file, CalibrationStore.Json) ?? new();
    }

    public Plan Current { get { lock (_lock) return _plan; } }

    public Plan Save(Plan plan)
    {
        Validate(plan);
        lock (_lock)
        {
            // Give new zones hub-wide ids; keep existing ones stable.
            var next = Math.Max(_plan.AllZones.Select(z => z.Id).DefaultIfEmpty(0).Max(), plan.AllZones.Select(z => z.Id).DefaultIfEmpty(0).Max()) + 1;
            var seen = new HashSet<int>();
            plan = plan with
            {
                Floors = plan.Floors.Select(f => f with
                {
                    Zones = f.Zones.Select(z => z.Id > 0 && seen.Add(z.Id) ? z : z with { Id = next++ }).ToList(),
                }).ToList(),
            };
            KeepVersion();
            SafeFile.WriteJson(_file, plan, CalibrationStore.Json);
            _plan = plan;
        }
        Changed?.Invoke();
        return plan;
    }

    private static void Validate(Plan p)
    {
        if (p.Floors.Count is < 1 or > 10) throw new ArgumentException("A plan needs 1 to 10 floors.");
        foreach (var f in p.Floors)
        {
            if (string.IsNullOrWhiteSpace(f.Name)) throw new ArgumentException("Every floor needs a name.");
            if (f.Width is < 1 or > 1000 || f.Height is < 1 or > 1000) throw new ArgumentException($"Floor '{f.Name}': size must be 1–1000 m.");
            if (f.Shapes.Count > 1000 || f.Zones.Count > 200 || f.Mics.Count > 200) throw new ArgumentException($"Floor '{f.Name}' has too many items.");
            foreach (var pts in f.Shapes.Select(s => s.Points).Concat(f.Zones.Select(z => z.Points)))
            {
                if (pts.Count > 500) throw new ArgumentException("A shape has too many points.");
                if (pts.Any(q => !double.IsFinite(q.X) || !double.IsFinite(q.Y))) throw new ArgumentException("Invalid coordinates.");
            }
            foreach (var z in f.Zones)
                if (z.Points.Count < 3) throw new ArgumentException($"Zone '{z.Name}' needs at least 3 corners.");
            if (f.Mics.GroupBy(m => m.MicKey).Any(g => g.Count() > 1)) throw new ArgumentException($"A mic is placed twice on '{f.Name}'.");
        }
        var allMics = p.Floors.SelectMany(f => f.Mics).Select(m => m.MicKey).ToList();
        if (allMics.Count != allMics.Distinct().Count()) throw new ArgumentException("A mic is placed on more than one floor.");
    }

    // ---------- earlier versions ----------

    /// <summary>Copies the plan as it is now into plan-history (before it's replaced), keeping the last 30.</summary>
    private void KeepVersion()
    {
        if (!File.Exists(_file)) return;
        Directory.CreateDirectory(_historyDir);
        File.Copy(_file, Path.Combine(_historyDir, $"plan-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json"), overwrite: true);
        foreach (var old in new DirectoryInfo(_historyDir).EnumerateFiles("plan-*.json").OrderByDescending(f => f.Name).Skip(HistoryKept))
            try { old.Delete(); } catch (IOException) { }
    }

    public sealed record PlanVersion(string Name, long Saved, int Floors, int Zones, int Mics, string Summary);

    /// <summary>Earlier saved versions, newest first.</summary>
    public IReadOnlyList<PlanVersion> Versions()
    {
        if (!Directory.Exists(_historyDir)) return [];
        var list = new List<PlanVersion>();
        foreach (var f in new DirectoryInfo(_historyDir).EnumerateFiles("plan-*.json").OrderByDescending(f => f.Name))
        {
            var p = ReadVersion(f.Name);
            if (p is null) continue;
            list.Add(new PlanVersion(f.Name, new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeSeconds(), p.Floors.Count, p.AllZones.Count(), p.AllMics.Count(),
                string.Join(", ", p.Floors.Select(x => $"{x.Name}: {x.Shapes.Count} shapes, {x.Zones.Count} zones"))));
        }
        return list;
    }

    public Plan? ReadVersion(string name)
    {
        if (name != Path.GetFileName(name) || !name.StartsWith("plan-") || !name.EndsWith(".json")) return null;
        var path = Path.Combine(_historyDir, name);
        try { return File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize<Plan>(File.ReadAllText(path), CalibrationStore.Json) : null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public byte[]? ReadImage(string id) => GetImage(id) is { } img ? File.ReadAllBytes(img.Path) : null;

    /// <summary>Saves an underlay image after checking it really is PNG, JPEG or WebP.</summary>
    public string SaveImage(byte[] data)
    {
        if (data.Length == 0 || data.Length > MaxImageBytes) throw new ArgumentException("Image must be under 15 MB.");
        var ext = Sniff(data) ?? throw new ArgumentException("Only PNG, JPEG or WebP images are supported (export a PDF plan as PNG first).");
        var id = Guid.NewGuid().ToString("N") + ext;
        File.WriteAllBytes(Path.Combine(_imageDir, id), data);
        return id;
    }

    public (string Path, string ContentType)? GetImage(string id)
    {
        if (id.Length > 64 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '.') || id.Count(c => c == '.') != 1) return null;
        var path = Path.Combine(_imageDir, id);
        if (!File.Exists(path)) return null;
        var type = Path.GetExtension(id) switch { ".png" => "image/png", ".jpg" => "image/jpeg", ".webp" => "image/webp", _ => null };
        return type is null ? null : (path, type);
    }

    private static string? Sniff(byte[] d)
    {
        if (d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return ".png";
        if (d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return ".jpg";
        if (d.Length > 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') return ".webp";
        return null;
    }
}
