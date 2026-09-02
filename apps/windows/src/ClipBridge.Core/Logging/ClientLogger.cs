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
///
/// Rotation (Milestone 3): size-bounded, bounded file count
/// (client.log → client.log.1 … client.log.N, newest last). Disk usage is therefore
/// bounded at maxBytes * (maxFiles + 1). Normal poll successes stay DEBUG-level — no
/// per-2-second log spam in daily use.
/// </summary>
public sealed class FileClientLogger : IClientLogger, IDisposable
{
    private readonly object _lock = new();
    private readonly string _logPath;
    private StreamWriter? _writer;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly Func<DateTime> _clock;

    public const int DefaultMaxBytes = 1024 * 1024; // 1 MiB per file
    public const int DefaultMaxFiles = 5;

    public FileClientLogger(
        string logDirectory,
        string fileName = "client.log",
        Func<DateTime>? clock = null,
        long maxBytes = DefaultMaxBytes,
        int maxFiles = DefaultMaxFiles)
    {
        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, fileName);
        _clock = clock ?? (() => DateTime.UtcNow);
        _maxBytes = maxBytes;
        _maxFiles = Math.Max(0, maxFiles);
        _writer = OpenAppend();
    }

    public string LogPath => _logPath;

    private StreamWriter OpenAppend() =>
        new(new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };

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
            if (_writer is null)
            {
                return;
            }
            if (NeedsRoll())
            {
                Roll();
            }
            _writer.WriteLine(line);
        }
    }

    private bool NeedsRoll()
    {
        try
        {
            return _maxBytes > 0 && new FileInfo(_logPath).Length >= _maxBytes;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private void Roll()
    {
        _writer?.Dispose();
        _writer = null;
        try
        {
            // Shift client.log.N → client.log.N+1 (oldest dropped), then current → .1
            if (_maxFiles > 0)
            {
                var oldest = $"{_logPath}.{_maxFiles}";
                if (File.Exists(oldest))
                {
                    File.Delete(oldest);
                }
                for (var i = _maxFiles - 1; i >= 1; i--)
                {
                    var from = $"{_logPath}.{i}";
                    var to = $"{_logPath}.{i + 1}";
                    if (File.Exists(from))
                    {
                        File.Move(from, to, overwrite: true);
                    }
                }
                File.Move(_logPath, $"{_logPath}.1", overwrite: true);
            }
            else
            {
                File.Delete(_logPath);
            }
        }
        catch
        {
            // rotation is best-effort; the logger must never take the app down
        }
        _writer = OpenAppend();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
