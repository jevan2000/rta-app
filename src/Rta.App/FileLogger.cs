using System.Collections.Concurrent;
using System.Text;

namespace Rta.App;

/// <summary>
/// Minimal daily-rolling file log (rta-YYYYMMDD.log in the data folder's logs directory), so an
/// unattended service or sign-in copy leaves a trail. Keeps 14 days; Information and above.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly BlockingCollection<string> _queue = new(10_000);
    private readonly Thread _writer;

    public FileLoggerProvider(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        _writer = new Thread(Write) { IsBackground = true, Name = "File log" };
        _writer.Start();
        Prune();
    }

    public ILogger CreateLogger(string category) => new FileLogger(this, category);

    internal void Enqueue(string line) => _queue.TryAdd(line);

    private void Write()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                var file = Path.Combine(_dir, $"rta-{DateTime.Now:yyyyMMdd}.log");
                var sb = new StringBuilder(line).AppendLine();
                while (_queue.TryTake(out var more)) sb.AppendLine(more);
                File.AppendAllText(file, sb.ToString());
            }
            catch { /* disk full or locked: drop rather than crash */ }
        }
    }

    private void Prune()
    {
        try
        {
            foreach (var f in new DirectoryInfo(_dir).EnumerateFiles("rta-*.log"))
                if (f.LastWriteTime < DateTime.Now.AddDays(-14)) f.Delete();
        }
        catch { /* ignore */ }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger(FileLoggerProvider p, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information
            && !(category.StartsWith("Microsoft.AspNetCore") && level < LogLevel.Warning)
            && !(category.StartsWith("System.Net.Http") && level < LogLevel.Warning);

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
        {
            if (!IsEnabled(level)) return;
            var lvl = level switch { LogLevel.Warning => "WARN", LogLevel.Error => "ERROR", LogLevel.Critical => "CRIT", _ => "INFO" };
            p.Enqueue($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {lvl} {category}: {fmt(state, ex)}{(ex is null ? "" : Environment.NewLine + ex)}");
        }
    }
}
