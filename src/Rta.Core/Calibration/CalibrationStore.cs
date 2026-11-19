using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rta.Core.Calibration;

/// <summary>Stores calibration profiles as one JSON file each, so they can be copied between machines.</summary>
public sealed class CalibrationStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _dir;
    private readonly object _lock = new();
    private IReadOnlyList<CalibrationProfile>? _cache;

    public CalibrationStore(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(_dir);
    }

    public IReadOnlyList<CalibrationProfile> List()
    {
        lock (_lock)
        {
            if (_cache is not null) return _cache;
            var list = new List<CalibrationProfile>();
            var files = Directory.EnumerateFiles(_dir, "*.json").Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Concat(Directory.EnumerateFiles(_dir, "*.json.bak").Select(b => b[..^4])) // a profile caught mid-save
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
                if (SafeFile.ReadJson<CalibrationProfile>(f, Json) is { } p) list.Add(p);
            return _cache = list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public CalibrationProfile? Get(string id) => IsValidId(id) ? List().FirstOrDefault(p => p.Id == id) : null;

    public CalibrationProfile Save(CalibrationProfile profile)
    {
        if (!IsValidId(profile.Id)) profile = profile with { Id = Guid.NewGuid().ToString("N") };
        lock (_lock)
        {
            SafeFile.WriteAllText(PathFor(profile.Id), JsonSerializer.Serialize(profile, Json));
            _cache = null;
        }
        return profile;
    }

    /// <summary>Imports a profile exported from this or another machine.</summary>
    public CalibrationProfile Import(string json)
    {
        var p = JsonSerializer.Deserialize<CalibrationProfile>(json, Json)
                ?? throw new InvalidDataException("Not a calibration profile.");
        return Save(p);
    }

    public string Export(string id)
    {
        var p = Get(id) ?? throw new KeyNotFoundException(id);
        return JsonSerializer.Serialize(p, Json);
    }

    public bool Delete(string id)
    {
        if (!IsValidId(id)) return false;
        lock (_lock)
        {
            var path = PathFor(id);
            if (!File.Exists(path)) return false;
            SafeFile.Delete(path);
            _cache = null;
            return true;
        }
    }

    private string PathFor(string id) => Path.Combine(_dir, id + ".json");

    private static bool IsValidId(string id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
