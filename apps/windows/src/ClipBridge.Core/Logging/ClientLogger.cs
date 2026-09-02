namespace ClipBridge.Core.Logging;

/// <summary>
/// Minimal logger contract. Implementations MUST NOT log clipboard content or tokens.
/// The built-in <see cref="FileClientLogger"/> enforces the same redaction discipline
/// as the relay: only clip ids, sizes, hash prefixes, statuses, timestamps.
/// </summary>
public interface IClientLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
    void Debug(string message);
}

public sealed class NullClientLogger : IClientLogger
{
    public static readonly NullClientLogger Instance = new();
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? exception = null) { }
    public void Debug(string message) { }
}

/// <summary>
/// Simple rotating file logger with a hard rule: never write clipboard content or tokens.
/// Fields allowed: clip_id, device_id, size, hash prefix, event names, status.
/// </summary>
public sealed class FileClientLogger : IClientLogger, IDisposable
{
    private readonly object _lock = new();
    private readonly StreamWriter _writer;

    public FileClientLogger(string logDirectory, string fileName = "client.log", Func<DateTime>? clock = null)
    {
        Directory.CreateDirectory(logDirectory);
        var path = Path.Combine(logDirectory, fileName);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    private readonly Func<DateTime> _clock;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Debug(string message) => Write("DEBUG", message);

    public void Error(string message, Exception? exception = null)
    {
        Write("ERROR", exception is null ? message : $"{message} ({exception.GetType().Name})");
    }

    private void Write(string level, string message)
    {
        // Sanitize defensively: replace control characters so multi-line content
        // can never smuggle itself into a single log line.
        var safe = message.Replace("\r", " ").Replace("\n", " ");
        var line = $"{_clock():yyyy-MM-ddTHH:mm:ss.fffZ} {level} {safe}";
        lock (_lock)
        {
            _writer.WriteLine(line);
        }
    }

    public void Dispose() => _writer.Dispose();
}
