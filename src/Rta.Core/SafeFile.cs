using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Rta.Core;

/// <summary>
/// Settings files that survive power cuts. Writes go to a temporary file that is flushed to disk and
/// then swapped in, keeping the previous version as <c>&lt;name&gt;.bak</c>. A file that can't be read
/// is set aside as <c>&lt;name&gt;.corrupt-&lt;time&gt;</c> and the <c>.bak</c> is used instead, so a bad
/// file never silently turns into default settings that then overwrite it.
/// </summary>
public static class SafeFile
{
    /// <summary>Recoveries since start-up, for the log and the backup page.</summary>
    public static ConcurrentQueue<string> Notes { get; } = new();

    public static void WriteAllText(string path, string text)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(false).GetBytes(text);
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
        if (File.Exists(path))
        {
            try { File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true); return; }
            catch (IOException) { /* e.g. file system without Replace support: fall through */ }
            catch (UnauthorizedAccessException) { }
            File.Copy(path, path + ".bak", overwrite: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static void WriteJson<T>(string path, T value, JsonSerializerOptions options) =>
        WriteAllText(path, JsonSerializer.Serialize(value, options));

    /// <summary>
    /// Reads a JSON file. Returns null if neither it nor its backup exists. If the file is unreadable it
    /// is set aside and its <c>.bak</c> is restored in its place; null only if that fails too.
    /// </summary>
    public static T? ReadJson<T>(string path, JsonSerializerOptions options) where T : class
    {
        if (!File.Exists(path))
        {
            // A crash between the two steps of a write can leave only the backup.
            if (TryRead<T>(path + ".bak", options, out var orphan) && orphan is not null)
            {
                File.Copy(path + ".bak", path, overwrite: true);
                Notes.Enqueue($"{Path.GetFileName(path)} was missing; restored it from its backup copy.");
                return orphan;
            }
            return null;
        }
        if (TryRead<T>(path, options, out var value) && value is not null) return value;

        var aside = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        try { File.Move(path, aside, overwrite: true); } catch (IOException) { aside = "(could not move it)"; }
        if (TryRead<T>(path + ".bak", options, out var backup) && backup is not null)
        {
            File.Copy(path + ".bak", path, overwrite: true);
            Notes.Enqueue($"{Path.GetFileName(path)} was damaged; restored the previous version. The damaged file was kept as {Path.GetFileName(aside)}.");
            return backup;
        }
        Notes.Enqueue($"{Path.GetFileName(path)} was damaged and had no usable backup, so defaults are in use. The damaged file was kept as {Path.GetFileName(aside)}.");
        return null;
    }

    private static bool TryRead<T>(string path, JsonSerializerOptions options, out T? value) where T : class
    {
        value = null;
        if (!File.Exists(path)) return false;
        try
        {
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return false;
            value = JsonSerializer.Deserialize<T>(text, options);
            return value is not null;
        }
        catch (JsonException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>Deletes a file written by <see cref="WriteAllText"/> and its backup.</summary>
    public static void Delete(string path)
    {
        File.Delete(path);
        File.Delete(path + ".bak");
    }
}
