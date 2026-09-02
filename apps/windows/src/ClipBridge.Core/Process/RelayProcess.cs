using System.Diagnostics;
using ProcessX = System.Diagnostics.Process;

namespace ClipBridge.Core.Process;

/// <summary>Spec for launching the relay child process.</summary>
public sealed record RelayLaunchSpec(
    string ExecutablePath,
    int Port,
    string Token,
    string DatabasePath,
    string LogsDirectory);

/// <summary>Abstraction over the relay child process (testable without real processes).</summary>
public interface IRelayProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
    event Action? Exited;
    void Kill();
    bool WaitForExit(int milliseconds);

    /// <summary>Assign to a kill-on-close Job Object (no-op in test doubles).</summary>
    void AssignJob(JobObject job);
}

public interface IRelayProcessFactory
{
    IRelayProcess Start(RelayLaunchSpec spec);
}

/// <summary>
/// Real implementation: System.Diagnostics.Process with no console window,
/// stdout/stderr redirected into the log directory (never a window, never the terminal).
/// </summary>
public sealed class ProcessRelayProcess : IRelayProcess
{
    private readonly ProcessX _process;

    public ProcessRelayProcess(RelayLaunchSpec spec, Action<string>? onOutputLine = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(spec.ExecutablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,     // no console/PowerShell window in daily mode
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // The relay (legacy .env layout) reads env vars; the managed child is configured
        // entirely through its environment so it never needs a config file layout near the exe.
        startInfo.Environment["CLIPBRIDGE_BIND_HOST"] = "0.0.0.0"; // LAN mode: iPhone/iPad reach it
        startInfo.Environment["CLIPBRIDGE_PORT"] = spec.Port.ToString();
        startInfo.Environment["CLIPBRIDGE_TOKEN"] = spec.Token;
        startInfo.Environment["CLIPBRIDGE_DB_PATH"] = spec.DatabasePath;
        startInfo.Environment["CLIPBRIDGE_LOG_DIR"] = spec.LogsDirectory;
        startInfo.Environment["CLIPBRIDGE_LOG_LEVEL"] = "INFO";
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";

        _process = new ProcessX { StartInfo = startInfo };
        _process.EnableRaisingEvents = true;
        _process.OutputDataReceived += (_, e) => { if (e.Data is { } line) onOutputLine?.Invoke(line); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) onOutputLine?.Invoke(line); };
        if (!_process.Start())
        {
            throw new InvalidOperationException("relay process failed to start");
        }
        _process.Exited += (_, _) => Exited?.Invoke();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public int Id => _process.Id;
    public bool HasExited => _process.HasExited;
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    public event Action? Exited;

    public void AssignJob(JobObject job) => job.Assign(_process);

    public void Kill()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }
    }

    public bool WaitForExit(int milliseconds) => _process.WaitForExit(milliseconds);

    public void Dispose() => _process.Dispose();
}

public sealed class DefaultRelayProcessFactory : IRelayProcessFactory
{
    private readonly Action<string>? _onOutputLine;

    public DefaultRelayProcessFactory(Action<string>? onOutputLine = null) => _onOutputLine = onOutputLine;

    public IRelayProcess Start(RelayLaunchSpec spec) => new ProcessRelayProcess(spec, _onOutputLine);
}
