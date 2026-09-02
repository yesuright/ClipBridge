using System.Collections.Concurrent;
using ClipBridge.Core.Logging;
using ClipBridge.Core.Status;

namespace ClipBridge.Core.Process;

/// <summary>
/// Owns the relay child process lifecycle (Milestone 3):
/// - start with health verification (bounded, no tight loops)
/// - crash detection → bounded restart with exponential backoff (no restart storm)
/// - graceful stop via the authenticated admin endpoint, hard-kill fallback, then
///   the Job Object (kill-on-close) guarantees the child dies with this app — no orphans.
/// - port-in-use is a clear Failed state, never a silent port change.
/// </summary>
public sealed class RelayManager : IDisposable
{
    private readonly object _lock = new();
    private readonly RelayLaunchSpec _spec;
    private readonly IRelayProcessFactory _processFactory;
    private readonly IRelayAdminClient _admin;
    private readonly IClientLogger _log;
    private readonly TimeSpan[] _backoff;
    private readonly int _maxRestarts;
    private IRelayProcess? _process;
    private JobObject? _job;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private bool _stopping;
    private bool _disposed;
    private int _restartAttempts;
    private DateTime _firstFailureUtc = DateTime.MinValue;
    private DateTime _runningSinceUtc = DateTime.MinValue;

    public const int DefaultMaxRestarts = 3;
    public static readonly TimeSpan[] DefaultBackoff = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) };

    /// <summary>True when the child was assigned to a kill-on-close job object.</summary>
    public bool IsChildInJob => _job is not null && _jobAssigned;

    private bool _jobAssigned;

    public RelayManager(
        RelayLaunchSpec spec,
        IRelayProcessFactory processFactory,
        IRelayAdminClient admin,
        IClientLogger log,
        TimeSpan[]? backoff = null,
        int maxRestarts = DefaultMaxRestarts,
        TimeSpan? healthTimeout = null,
        TimeSpan? healthPollInterval = null,
        TimeSpan? successStability = null,
        Func<int, bool>? portProbe = null)
    {
        _spec = spec;
        _processFactory = processFactory;
        _admin = admin;
        _log = log;
        _backoff = backoff ?? DefaultBackoff;
        _maxRestarts = maxRestarts;
        _healthTimeout = healthTimeout ?? TimeSpan.FromSeconds(20);
        _healthPollInterval = healthPollInterval ?? TimeSpan.FromMilliseconds(500);
        _successStability = successStability ?? TimeSpan.FromSeconds(60);
        _portProbe = portProbe ?? PortProbe.IsLoopbackPortOpen;
    }

    private readonly TimeSpan _healthTimeout;
    private readonly TimeSpan _healthPollInterval;
    private readonly TimeSpan _successStability;
    private readonly Func<int, bool> _portProbe;

    /// <summary>
    /// A crash resets the restart budget when the relay had been Running stably for at
    /// least this long (default 60 s). Rapid crash→restart loops therefore hit the bound;
    /// a relay that worked for hours and then crashes gets a fresh budget.
    /// </summary>
    public TimeSpan SuccessStability => _successStability;

    public event Action<RelayState, string?>? StateChanged;

    public RelayState State { get; private set; } = RelayState.Stopped;

    public string? LastFailureReason { get; private set; }

    public int? RelayPid => _process is { HasExited: false } ? _process.Id : null;

    private bool _enabled;

    /// <summary>Start (or restart) the managed relay. Never throws; failures go to Failed.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_disposed || _stopping)
            {
                return;
            }
            // Restarting is a valid entry point (crash-restart path), Starting/Running are not.
            if (State is RelayState.Starting or RelayState.Running)
            {
                return;
            }
            _enabled = true;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => StartWorkerAsync(_cts.Token));
        }
    }

    /// <summary>User-initiated restart: resets the restart budget and starts again.</summary>
    public void Restart()
    {
        lock (_lock)
        {
            _restartAttempts = 0;
            _firstFailureUtc = DateTime.MinValue;
        }
        Stop(graceful: true);
        lock (_lock)
        {
            _stopping = false; // Stop() sets it; a fresh start sequence must clear it
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
        }
        Start();
    }

    /// <summary>Stop the relay gracefully (admin endpoint → wait → kill).</summary>
    public void Stop(bool graceful = true)
    {
        lock (_lock)
        {
            if (_disposed || _stopping)
            {
                return;
            }
            _stopping = true;
            _cts?.Cancel();
        }

        // Graceful path: ask the relay to stop itself (uvicorn drain), wait briefly,
        // then hard-kill. The Job Object disposal in Dispose() is the final guarantee.
        if (graceful)
        {
            var process = _process;
            if (process is { HasExited: false })
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    _ = _admin.RequestShutdownAsync(cts.Token).GetAwaiter().GetResult();
                }
                catch
                {
                    // relay may be mid-crash — proceed to kill
                }
                if (!process.WaitForExit(5000))
                {
                    TryKill(process);
                }
            }
        }
        else
        {
            if (_process is { HasExited: false } p)
            {
                TryKill(p);
            }
        }

        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
            // worker races are non-fatal; the Job Object is the final guarantee
        }
        if (State != RelayState.Failed)
        {
            SetState(RelayState.Stopped, null);
        }
    }

    private async Task StartWorkerAsync(CancellationToken ct)
    {
        try
        {
            SetState(RelayState.Starting, null);
            _log.Info($"relay_starting exe={Path.GetFileName(_spec.ExecutablePath)} port={_spec.Port}");

            // --- port conflict check (no silent port change: the iPhone Shortcut pins 8787)
            if (_portProbe(_spec.Port))
            {
                if (await _admin.IsHealthyAsync(ct).ConfigureAwait(false))
                {
                    // A healthy relay is already answering — treat as external relay (dev/startup race).
                    SetState(RelayState.Running, "external relay already healthy");
                    _log.Info("relay_external_healthy port busy, using already-running relay");
                    return;
                }
                Fail("port_in_use", $"TCP {_spec.Port} is already in use by another program");
                return;
            }

            IRelayProcess process;
            try
            {
                process = _processFactory.Start(_spec);
            }
            catch (Exception ex)
            {
                Fail("spawn_failed", ex.Message);
                return;
            }
            lock (_lock)
            {
                _process = process;
            }
            process.Exited += OnProcessExited;

            // Kill-on-close: parent death (crash or kill) takes the relay with it.
            try
            {
                _job = new JobObject();
                process.AssignJob(_job);
                _jobAssigned = true;
            }
            catch (Exception ex)
            {
                _log.Warn($"relay_job_object_unavailable error={ex.Message} (orphan protection degraded to graceful-stop-only)");
                _job?.Dispose();
                _job = null;
                _jobAssigned = false;
            }

            // --- health verification: bounded wait (default 20 s), 500 ms steps
            var healthy = false;
            var deadline = DateTime.UtcNow + _healthTimeout;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                if (process.HasExited)
                {
                    Fail("process_exited", $"relay exited during startup (exit code {process.ExitCode})");
                    return;
                }
                healthy = await _admin.IsHealthyAsync(ct).ConfigureAwait(false);
                if (healthy)
                {
                    break;
                }
                if (ct.IsCancellationRequested)
                {
                    break;
                }
                await Task.Delay(_healthPollInterval, ct).ConfigureAwait(false);
            }

            if (!healthy)
            {
                if (ct.IsCancellationRequested)
                {
                    return; // stopping — nothing to do
                }
                Fail("startup_timeout", "relay did not become healthy within the startup timeout");
                TryKill(process);
                return;
            }

            lock (_lock)
            {
                _runningSinceUtc = DateTime.UtcNow; // stability window starts on success
            }
            SetState(RelayState.Running, null);
            _log.Info($"relay_running pid={process.Id} port={_spec.Port}");
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        catch (Exception ex)
        {
            Fail("unexpected", ex.Message);
        }
    }

    private void OnProcessExited()
    {
        IRelayProcess? process;
        lock (_lock)
        {
            process = _process;
        }
        if (process is null || _stopping || _disposed)
        {
            return;
        }
        _log.Warn($"relay_exited pid={process.Id} code={process.ExitCode}");

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var wasRunningStably = _runningSinceUtc != DateTime.MinValue
                && (now - _runningSinceUtc) > _successStability;
            var windowExpired = _firstFailureUtc != DateTime.MinValue
                && (now - _firstFailureUtc) > TimeSpan.FromMinutes(5);
            if (_firstFailureUtc == DateTime.MinValue || wasRunningStably || windowExpired)
            {
                _firstFailureUtc = now;
                _restartAttempts = 0; // fresh budget (first crash, or a long-stable run)
            }
            _restartAttempts++;
            if (_restartAttempts > _maxRestarts)
            {
                LastFailureReason = "restart_limit";
                SetState(RelayState.Failed, "restart_limit");
                _log.Error($"relay_failed reason=restart_limit attempts={_restartAttempts}");
                return;
            }
            var delay = _backoff[Math.Clamp(_restartAttempts - 1, 0, _backoff.Length - 1)];
            SetState(RelayState.Restarting, $"attempt {_restartAttempts}/{_maxRestarts}");
            _log.Warn($"relay_restarting attempt={_restartAttempts}/{_maxRestarts} delay_ms={(int)delay.TotalMilliseconds}");
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                    lock (_lock)
                    {
                        if (_stopping || _disposed)
                        {
                            return;
                        }
                    }
                    Start();
                }
                catch (OperationCanceledException)
                {
                    // shutting down
                }
            });
        }
    }

    private void Fail(string reason, string detail)
    {
        LastFailureReason = reason;
        _log.Error($"relay_failed reason={reason} detail={detail}");
        SetState(RelayState.Failed, reason);
    }

    private void SetState(RelayState state, string? reason)
    {
        State = state;
        StateChanged?.Invoke(state, reason);
    }

    private void TryKill(IRelayProcess process)
    {
        try
        {
            process.Kill();
        }
        catch
        {
            // already dead — fine
        }
    }

    public void Dispose()
    {
        // Stop first (before flagging _disposed): Stop ignores the child otherwise.
        Stop(graceful: true);
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _cts?.Dispose();
            _cts = null;
            _job?.Dispose(); // kill-on-close: last-resort orphan guard
            _job = null;
            _process?.Dispose();
            _process = null;
        }
    }
}
