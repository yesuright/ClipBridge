using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace ClipBridge.Core.Process;

public enum InstanceCommand
{
    None,
    Activate,
    Status,
    Exit,
}

/// <summary>
/// Windows single-instance guard (Milestone 3): a per-user named mutex plus a small
/// named-pipe channel. The second instance detects the running one, signals it
/// (activate → tray balloon; status → primary answers with a JSON snapshot;
/// exit → primary shuts down cleanly) and exits — no second tray icon, no second relay,
/// no second clipboard listener, no second bind on 8787.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private Thread? _pipeThread;
    private volatile bool _running;
    private bool _ownsMutex;

    public const string MutexPrefix = "ClipBridge.SingleInstance.";
    public const string PipePrefix = "ClipBridge.SingleInstance.";

    /// <summary>Raised on the primary instance when a second instance signals activation.</summary>
    public event Action? Activated;

    /// <summary>Raised on the primary instance when a second instance requests a clean exit.</summary>
    public event Action? ExitRequested;

    /// <summary>
    /// Host hook: given an incoming command line, returns an optional response string
    /// (used for "status" → JSON snapshot). Must be cheap and thread-safe.
    /// </summary>
    public Func<string, string?>? CommandHandler { get; set; }

    public bool IsPrimary { get; private set; }

    public SingleInstanceGuard(string instanceId)
    {
        var scope = ScopeSuffix(instanceId);
        _mutex = new Mutex(false, MutexPrefix + scope);
        _pipeName = PipePrefix + scope;
    }

    private static string ScopeSuffix(string instanceId)
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (!string.IsNullOrEmpty(sid))
            {
                return instanceId + "." + sid;
            }
        }
        catch
        {
            // non-Windows (test runners) — fall through to a per-user-ish name
        }
        return instanceId + "." + Environment.UserName;
    }

    /// <summary>
    /// Try to become the primary instance. Returns true when this instance owns the lock
    /// (and must run). When another instance owns it, this instance signals it and returns false.
    /// </summary>
    public bool TryAcquire()
    {
        try
        {
            var acquired = _mutex.WaitOne(0, false);
            if (!acquired)
            {
                IsPrimary = false;
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // previous instance died without releasing the mutex — we own it now
        }
        IsPrimary = true;
        _ownsMutex = true;
        _running = true;
        _pipeThread = new Thread(PipeServerLoop) { IsBackground = true, Name = "clipbridge-single-instance" };
        _pipeThread.Start();
        return true;
    }

    /// <summary>Signal the primary instance (second-instance path). Best effort, never throws.</summary>
    public void Signal(string command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(1000);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            writer.WriteLine(command);
            // status replies with one line; everything else gets no reply
            var line = new StreamReader(pipe).ReadLine();
            _ = line;
        }
        catch
        {
            // primary vanished in a race — the second instance should still exit quietly
        }
    }

    /// <summary>Ask the primary for a status snapshot; null when not reachable.</summary>
    public string? RequestStatus()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(1500);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            writer.WriteLine("status");
            return new StreamReader(pipe).ReadLine();
        }
        catch
        {
            return null;
        }
    }

    private void PipeServerLoop()
    {
        while (_running)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                pipe.WaitForConnection();
                using var reader = new StreamReader(pipe, leaveOpen: true);
                var line = reader.ReadLine();
                if (line is null)
                {
                    continue;
                }
                switch (ParseCommand(line))
                {
                    case InstanceCommand.Activate:
                        Activated?.Invoke();
                        break;
                    case InstanceCommand.Status:
                        var reply = CommandHandler?.Invoke(line);
                        if (reply is not null)
                        {
                            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                            writer.WriteLine(reply);
                        }
                        break;
                    case InstanceCommand.Exit:
                        ExitRequested?.Invoke();
                        break;
                }
            }
            catch
            {
                // transient pipe errors — keep serving
            }
        }
    }

    public static InstanceCommand ParseCommand(string? line) => line switch
    {
        "activate" => InstanceCommand.Activate,
        "status" => InstanceCommand.Status,
        "exit" => InstanceCommand.Exit,
        _ => InstanceCommand.None,
    };

    public void Dispose()
    {
        _running = false;
        try
        {
            if (_ownsMutex)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch (Exception ex) when (ex is ApplicationException or ObjectDisposedException)
        {
            // released by the owning thread already, or double-disposed — safe to ignore
        }
        try
        {
            _mutex.Dispose();
        }
        catch
        {
            // concurrent disposal — finalizer-safe
        }
    }
}
