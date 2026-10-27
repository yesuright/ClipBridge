using System.Collections.Concurrent;
using ClipBridge.Core.Logging;
using ClipBridge.Core.Status;
using ClipBridge.Core.Time;

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
    private RelayLaunchSpec _spec;
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
    private int _recoveryRounds;
    private int _recoverySeq;
    private bool _recoveryPending;
    private DateTime _startedAtUtc = DateTime.MinValue;
    private DateTime _lastExitAtUtc = DateTime.MinValue;
    private int? _lastExitCode;

    public const int DefaultMaxRestarts = 3;
    public static readonly TimeSpan[] DefaultBackoff = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) };

    /// <summary>
    /// Cool-down before a fresh restart budget after the bounded budget is exhausted
    /// (escalating, final value is the permanent cap). The manager never gives up on a
    /// managed relay: a transient failure self-heals without the user restarting the app,
    /// while a permanently broken relay retries at most once per cap — never a storm.
    /// </summary>
    public static readonly TimeSpan[] DefaultRecoveryCooldown =
    {
        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(300),
    };

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
        Func<int, bool>? portProbe = null,
        TimeSpan[]? recoveryCooldown = null,
        Func<int, int?>? portFallback = null,
        Action<int>? portChanged = null)
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
        _recoveryCooldown = recoveryCooldown ?? DefaultRecoveryCooldown;
        _portFallback = portFallback;
        _portChanged = portChanged;
        if (_recoveryCooldown.Length == 0)
        {
            _recoveryCooldown = DefaultRecoveryCooldown;
        }
    }

    private readonly Func<int, int?>? _portFallback;
    private readonly Action<int>? _portChanged;

    private readonly TimeSpan _healthTimeout;
    private readonly TimeSpan _healthPollInterval;
    private readonly TimeSpan _successStability;
    private readonly Func<int, bool> _portProbe;
    private readonly TimeSpan[] _recoveryCooldown;

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

    // ------------------------------------------------------- diagnostics (Layer 2)
    // Precise, queryable lifecycle facts so the NEXT unexpected exit can be explained
    // from the log alone (pid / start / exit / exit code / attempt / budget).

    /// <summary>Restarts consumed inside the current failure window (0 = fresh budget).</summary>
    public int RestartAttempts { get { lock (_lock) { return _restartAttempts; } } }

    /// <summary>Failed cycles since the relay last ran: drives the recovery cool-down.</summary>
    public int RecoveryRounds { get { lock (_lock) { return _recoveryRounds; } } }

    /// <summary>UTC start time of the current (or most recent) relay child.</summary>
    public DateTime? LastStartAtUtc { get { lock (_lock) { return _startedAtUtc == DateTime.MinValue ? null : _startedAtUtc; } } }

    /// <summary>UTC time the most recent relay child was observed to exit.</summary>
    public DateTime? LastExitAtUtc { get { lock (_lock) { return _lastExitAtUtc == DateTime.MinValue ? null : _lastExitAtUtc; } } }

    /// <summary>Exit code of the most recent relay child (null = never observed).</summary>
    public int? LastExitCode { get { lock (_lock) { return _lastExitCode; } } }

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
            _recoveryRounds = 0;
            _recoveryPending = false;
            _recoverySeq++; // supersede any pending recovery supervisor
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

            // --- port conflict check
            // Policy: an already-healthy ClipBridge relay is reused (external/dev case).
            // Anything else on the port is a conflict. Instead of a permanent Failed state
            // the manager may ask the host for the next free port once per start attempt;
            // the host persists it and tells the user, because the Shortcut pins the URL.
            var port = _spec.Port;
            var conflicted = _portProbe(port);
            if (conflicted && await _admin.IsHealthyAsync(ct).ConfigureAwait(false))
            {
                // A ClipBridge relay is already answering — treat as external relay (dev/startup race).
                SetState(RelayState.Running, "external relay already healthy");
                _log.Info($"relay_external_healthy port={port} busy, using already-running relay");
                return;
            }
            if (conflicted && _portFallback is not null)
            {
                var fallback = _portFallback(port);
                if (fallback is int candidate
                    && candidate != port
                    && candidate is >= 1 and <= 65535
                    && !_portProbe(candidate))
                {
                    _spec = _spec with { Port = candidate };
                    conflicted = false;
                    _log.Warn($"relay_port_fallback old_port={port} new_port={candidate} reason=port_in_use");
                    port = candidate;
                    try
                    {
                        _portChanged?.Invoke(candidate);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"relay_port_fallback_callback_failed error={ex.GetType().Name}");
                    }
                }
            }
            if (conflicted)
            {
                Fail("port_in_use", $"TCP {port} is already in use by another program");
                return;
            }

            _log.Info($"relay_starting exe={Path.GetFileName(_spec.ExecutablePath)} port={port}");

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
                _startedAtUtc = DateTime.UtcNow;
            }
            process.Exited += OnProcessExited;
            _log.Info($"relay_spawned pid={process.Id} attempt={_restartAttempts + 1} started_at={Timestamps.UtcNowIso()}");

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
            var startedAt = DateTime.UtcNow;
            var deadline = startedAt + _healthTimeout;
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
                // Record whether the child ever bound the port: this single field tells the
                // next investigation apart "relay never listened" from "relay listened but
                // the health probe could not reach it".
                var listening = SafePortProbe(_spec.Port);
                Fail("startup_timeout",
                    $"relay did not become healthy within the startup timeout " +
                    $"(pid={process.Id} waited_ms={(int)(DateTime.UtcNow - startedAt).TotalMilliseconds} " +
                    $"listening={listening} exited={process.HasExited})");
                TryKill(process);
                return;
            }

            lock (_lock)
            {
                _runningSinceUtc = DateTime.UtcNow; // stability window starts on success
                // A healthy run clears the recovery escalation and supersedes any pending
                // supervisor: a future crash gets a full, fresh budget.
                _recoveryRounds = 0;
                _recoveryPending = false;
                _recoverySeq++;
            }
            LastFailureReason = null; // the relay is serving again: no stale failure state
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
        var exitCode = process.ExitCode;
        var now = DateTime.UtcNow;
        TimeSpan uptime;
        lock (_lock)
        {
            _lastExitCode = exitCode;
            _lastExitAtUtc = now;
            uptime = _startedAtUtc == DateTime.MinValue ? TimeSpan.Zero : now - _startedAtUtc;
        }
        _log.Warn(
            $"relay_exited pid={process.Id} code={exitCode} " +
            $"uptime_ms={(int)uptime.TotalMilliseconds} exited_at={Timestamps.UtcNowIso()}");

        lock (_lock)
        {
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
                _log.Error($"relay_failed reason=restart_limit attempts={_restartAttempts} last_exit_code={exitCode}");
                // The bounded budget protects against a restart STORM, it must not be a
                // dead end: arm the escalating self-recovery supervisor so a transient
                // failure still heals without the user restarting the app.
                ArmRecoveryLocked();
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

    /// <summary>
    /// Arm one recovery supervisor after the manager settled into <see cref="RelayState.Failed"/>.
    /// After the cool-down it clears the restart budget and starts a fresh attempt sequence.
    /// Every arm supersedes the previous one (single pending supervisor), the cool-down
    /// escalates and is capped, and a cancelled/stopped/disposed manager never restarts.
    /// The caller must hold <see cref="_lock"/>.
    /// </summary>
    private void ArmRecoveryLocked()
    {
        // One supervisor per failure episode: several failure paths can report the same
        // stuck episode (Fail(...) plus the Exited handler), and only the first should
        // consume a cool-down step.
        if (_stopping || _disposed || !_enabled || _recoveryPending)
        {
            return;
        }
        var seq = ++_recoverySeq;
        _recoveryPending = true;
        var cooldown = _recoveryCooldown[Math.Clamp(_recoveryRounds, 0, _recoveryCooldown.Length - 1)];
        _recoveryRounds++;
        _log.Error(
            $"relay_restart_exhausted attempts={_restartAttempts} last_exit_code={_lastExitCode?.ToString() ?? "none"} " +
            $"last_error={LastFailureReason ?? "unknown"} recovery_round={_recoveryRounds} " +
            $"next_retry_in_ms={(int)cooldown.TotalMilliseconds} state=Failed");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(cooldown).ConfigureAwait(false);
                lock (_lock)
                {
                    // Cancelled/stopped/superseded: never restart.
                    if (_stopping || _disposed || !_enabled || seq != _recoverySeq)
                    {
                        return;
                    }
                    _recoveryPending = false;
                    if (State != RelayState.Failed)
                    {
                        return; // recovered through another path in the meantime
                    }
                    _restartAttempts = 0; // fresh bounded budget for the recovery round
                    _firstFailureUtc = DateTime.MinValue;
                    _log.Warn(
                        $"relay_recovery_attempt round={_recoveryRounds} after_exhausted=true reason={LastFailureReason ?? "unknown"}");
                }
                Start();
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
        });
    }

    private void Fail(string reason, string detail)
    {
        LastFailureReason = reason;
        _log.Error($"relay_failed reason={reason} detail={detail}");
        SetState(RelayState.Failed, reason);
        lock (_lock)
        {
            // Failure paths that never produce an Exited event (port_in_use, spawn_failed)
            // would otherwise be permanent. When a live child exists its exit drives the
            // normal bounded-restart flow, which arms recovery itself if it exhausts.
            if (_process is not { HasExited: false })
            {
                ArmRecoveryLocked();
            }
        }
    }

    private bool SafePortProbe(int port)
    {
        try
        {
            return _portProbe(port);
        }
        catch
        {
            return false;
        }
    }

    private RelayState _lastNotifiedState = RelayState.Stopped;

    private void SetState(RelayState state, string? reason)
    {
        State = state;
        // StateChanged means "the state changed". A stuck episode reports Failed more than
        // once with different reasons (startup_timeout, then restart_limit, then once per
        // recovery cool-down) and the tray must not balloon once per report.
        if (state == _lastNotifiedState)
        {
            return;
        }
        _lastNotifiedState = state;
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
