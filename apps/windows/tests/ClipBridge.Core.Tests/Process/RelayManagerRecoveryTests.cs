using ClipBridge.Core.Process;
using ClipBridge.Core.Status;
using ClipBridge.Core.Tests.Sync;
using Xunit;

namespace ClipBridge.Core.Tests.Process;

/// <summary>
/// Regression tests for the real 2026-09-10 incident: after a Windows reboot the relay
/// child was force-killed, every post-reboot start failed the startup health check, the
/// bounded restart budget was consumed within ~90 s, and the app then stayed
/// `Relay=Failed / Sync=Offline` FOREVER — even after the transient condition cleared and
/// even though a fresh launch of the very same artifact came up in under a second.
///
/// Root cause: the restart budget could only ever be reset inside the child's Exited
/// handler, and the terminal `Failed(restart_limit)` state has no running child — so no
/// Exited event could ever fire again and the manager could never retry.
/// </summary>
public class RelayManagerRecoveryTests
{
    private static readonly TimeSpan[] FastBackoff =
        { TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5) };

    private static RelayManager CreateManager(
        FakeRelayProcessFactory factory,
        FakeRelayAdmin admin,
        CapturingLogger log,
        TimeSpan? healthTimeout = null,
        TimeSpan? successStability = null,
        TimeSpan[]? recoveryCooldown = null,
        Func<int, bool>? portProbe = null,
        int maxRestarts = RelayManager.DefaultMaxRestarts)
        => new(
            new RelayLaunchSpec("C:\\fake\\ClipBridgeRelay.exe", 8787, "token", "db", "logs"),
            factory, admin, log,
            backoff: FastBackoff,
            maxRestarts: maxRestarts,
            healthTimeout: healthTimeout ?? TimeSpan.FromMilliseconds(60),
            healthPollInterval: TimeSpan.FromMilliseconds(10),
            successStability: successStability ?? TimeSpan.FromHours(1),
            portProbe: portProbe ?? (_ => false),
            recoveryCooldown: recoveryCooldown ?? new[] { TimeSpan.FromMilliseconds(150) });

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "condition not met within timeout");
    }

    private static Task WaitForExhaustedAsync(RelayManager manager)
        => WaitForAsync(() => manager.State == RelayState.Failed
                              && manager.LastFailureReason == "restart_limit");

    // ---------------------------------------------------------------- unexpected exit

    [Fact]
    public async Task UnexpectedExit_IsObservedWithCodeAndTimestamps_ThenRecovers()
    {
        var factory = new FakeRelayProcessFactory();
        var admin = new FakeRelayAdmin();
        var log = new CapturingLogger();
        using var manager = CreateManager(factory, admin, log);
        manager.Start();
        await WaitForAsync(() => manager.State == RelayState.Running);
        Assert.NotNull(manager.LastStartAtUtc);

        factory.Last.Crash(code: 1073807364); // DBG_TERMINATE_PROCESS, as in the incident
        await WaitForAsync(() => manager.State == RelayState.Running && factory.StartCount >= 2);

        Assert.Equal(1073807364, manager.LastExitCode);
        Assert.NotNull(manager.LastExitAtUtc);
        Assert.Contains(log.Lines, l => l.Contains("relay_exited") && l.Contains("code=1073807364"));
        Assert.Contains(log.Lines, l => l.Contains("relay_exited") && l.Contains("uptime_ms="));
        Assert.Contains(log.Lines, l => l.Contains("relay_spawned") && l.Contains("pid="));
    }

    // ---------------------------------------------------------------- the core regression

    [Fact]
    public async Task ExhaustedBudget_IsNotTerminal_ManagerRecoversByItself()
    {
        var factory = new FakeRelayProcessFactory();
        var admin = new FakeRelayAdmin { Healthy = false }; // every start times out
        var log = new CapturingLogger();
        using var manager = CreateManager(factory, admin, log);

        manager.Start();
        await WaitForExhaustedAsync(manager);

        // Bounded: the initial attempt plus at most maxRestarts — never a storm.
        Assert.True(factory.StartCount <= 1 + RelayManager.DefaultMaxRestarts,
            $"start count {factory.StartCount} must stay bounded");
        Assert.Equal(RelayState.Failed, manager.State);

        var startsAtExhaustion = factory.StartCount;

        // The transient condition clears. NOBODY touches the app: no manual Restart(),
        // no tray action, no relaunch. The manager must heal on its own.
        admin.Healthy = true;
        await WaitForAsync(() => manager.State == RelayState.Running);

        Assert.True(factory.StartCount > startsAtExhaustion,
            "a recovery round must actually start a new relay child");
        Assert.Null(manager.LastFailureReason);
        Assert.Equal(RelayState.Running, manager.State);
    }

    [Fact]
    public async Task Exhaustion_IsLoggedWithAccurateDiagnostics()
    {
        var factory = new FakeRelayProcessFactory();
        var admin = new FakeRelayAdmin { Healthy = false };
        var log = new CapturingLogger();
        using var manager = CreateManager(factory, admin, log);

        manager.Start();
        await WaitForExhaustedAsync(manager);

        var line = log.Lines.FirstOrDefault(l => l.Contains("relay_restart_exhausted"));
        Assert.NotNull(line);
        Assert.Contains("last_exit_code=", line);
        Assert.Contains("last_error=restart_limit", line);
        Assert.Contains("recovery_round=", line);
        Assert.Contains("next_retry_in_ms=", line);
        // The next investigation must be able to tell "never listened" from "listened but
        // unreachable" straight from the startup-timeout line.
        Assert.Contains(log.Lines, l => l.Contains("relay_failed reason=startup_timeout")
                                        && l.Contains("listening="));
    }

    [Fact]
    public async Task RecoveryRounds_KeepRetrying_ButNeverStorm()
    {
        var factory = new FakeRelayProcessFactory();
        var admin = new FakeRelayAdmin { Healthy = false }; // stays permanently broken
        var log = new CapturingLogger();
        using var manager = CreateManager(
            factory, admin, log,
            recoveryCooldown: new[] { TimeSpan.FromMilliseconds(200) });

        manager.Start();
        await WaitForExhaustedAsync(manager);
        await WaitForAsync(() => manager.RecoveryRounds >= 3);

        Assert.Equal(RelayState.Failed, manager.State);
        // Each recovery round gets a fresh bounded budget and nothing more.
        Assert.True(factory.StartCount <= (1 + RelayManager.DefaultMaxRestarts) * (manager.RecoveryRounds + 1),
            $"start count {factory.StartCount} must stay bounded across {manager.RecoveryRounds} rounds");
        Assert.Contains(log.Lines, l => l.Contains("relay_recovery_attempt"));
    }

    [Fact]
    public async Task ExhaustedRelay_WaitsTheFullCooldownBeforeRetrying()
    {
        var factory = new FakeRelayProcessFactory();
        var admin = new FakeRelayAdmin { Healthy = false };
        var log = new CapturingLogger();
        var cooldown = TimeSpan.FromMilliseconds(700);
        using var manager = CreateManager(factory, admin, log, recoveryCooldown: new[] { cooldown });

        manager.Start();
        await WaitForExhaustedAsync(manager);
        var startsAtExhaustion = factory.StartCount;

        // Inside the cool-down nothing may start: no per-second restart storm.
        await Task.Delay((int)(cooldown.TotalMilliseconds * 0.6));
        Assert.Equal(startsAtExhaustion, factory.StartCount);

        // After it, exactly one recovery round begins.
        await WaitForAsync(() => factory.StartCount > startsAtExhaustion);
        Assert.True(manager.RecoveryRounds >= 1);
    }

    // ---------------------------------------------------------------- cancellation

    [Fact]
    public async Task StopWhileRecoveryIsPending_NeverRestarts()
    {
        var factory = new FakeRelayProcessFactory();
        var admin = new FakeRelayAdmin { Healthy = false };
        var log = new CapturingLogger();
        using var manager = CreateManager(
            factory, admin, log,
            recoveryCooldown: new[] { TimeSpan.FromMilliseconds(400) });

        manager.Start();
        await WaitForExhaustedAsync(manager);
        var startsAtStop = factory.StartCount;

        manager.Stop(graceful: true);
        // Stop() deliberately keeps a Failed state visible (the tray still shows the last
        // failure), but it must not leave a live relay or a queued restart behind.
        Assert.NotEqual(RelayState.Running, manager.State);

        await Task.Delay(1200); // well past the pending cool-down
        Assert.Equal(startsAtStop, factory.StartCount);
        Assert.NotEqual(RelayState.Running, manager.State);
    }

    [Fact]
    public async Task DisposeWhileRecoveryIsPending_NeverRestarts()
    {
        var factory = new FakeRelayProcessFactory();
        var admin = new FakeRelayAdmin { Healthy = false };
        var log = new CapturingLogger();
        var manager = CreateManager(
            factory, admin, log,
            recoveryCooldown: new[] { TimeSpan.FromMilliseconds(400) });

        manager.Start();
        await WaitForExhaustedAsync(manager);
        var startsAtDispose = factory.StartCount;

        manager.Dispose();
        await Task.Delay(1200);
        Assert.Equal(startsAtDispose, factory.StartCount);
    }

    [Fact]
    public async Task ManualRestart_SupersedesAPendingRecoveryRound()
    {
        var factory = new FakeRelayProcessFactory();
        var admin = new FakeRelayAdmin { Healthy = false };
        var log = new CapturingLogger();
        using var manager = CreateManager(
            factory, admin, log,
            recoveryCooldown: new[] { TimeSpan.FromMilliseconds(800) });

        manager.Start();
        await WaitForExhaustedAsync(manager);

        admin.Healthy = true;
        manager.Restart(); // user action: must not be doubled by the queued supervisor
        await WaitForAsync(() => manager.State == RelayState.Running);

        var startsAfterRecovery = factory.StartCount;
        await Task.Delay(1200);
        Assert.Equal(startsAfterRecovery, factory.StartCount);
        Assert.Equal(RelayState.Running, manager.State);
    }

    // ---------------------------------------------------------------- reset semantics

    [Fact]
    public async Task StableRun_ResetsTheBudget_SoTheNextCrashIsAFreshFirstAttempt()
    {
        var factory = new FakeRelayProcessFactory();
        var admin = new FakeRelayAdmin();
        var log = new CapturingLogger();
        using var manager = CreateManager(
            factory, admin, log,
            successStability: TimeSpan.FromMilliseconds(150));

        manager.Start();
        await WaitForAsync(() => manager.State == RelayState.Running);

        await Task.Delay(400); // run stably past the reset threshold
        factory.Last.Crash();
        await WaitForAsync(() => manager.State == RelayState.Running && manager.RestartAttempts == 1);

        // Crash again immediately: the relay never became stable, so the budget CONTINUES.
        factory.Last.Crash();
        await WaitForAsync(() => manager.State == RelayState.Running && manager.RestartAttempts == 2);

        // Run stably again, then crash: the budget must reset to a full one.
        await Task.Delay(400);
        factory.Last.Crash();
        await WaitForAsync(() => manager.State == RelayState.Running && manager.RestartAttempts == 1);

        Assert.Equal(RelayState.Running, manager.State);
    }

    // ---------------------------------------------------------------- other dead-end path

    [Fact]
    public async Task PortInUseFailure_IsAlsoRecoverable_WhenThePortIsReleased()
    {
        var factory = new FakeRelayProcessFactory();
        var admin = new FakeRelayAdmin { Healthy = false };
        var log = new CapturingLogger();
        var occupied = true; // another program holds 8787
        using var manager = CreateManager(
            factory, admin, log,
            portProbe: _ => occupied,
            recoveryCooldown: new[] { TimeSpan.FromMilliseconds(150) });

        manager.Start();
        await WaitForAsync(() => manager.State == RelayState.Failed);
        Assert.Equal("port_in_use", manager.LastFailureReason);
        Assert.Equal(0, factory.StartCount); // never spawned into a busy port

        // The other program exits; the manager must notice without user action.
        occupied = false;
        admin.Healthy = true;
        await WaitForAsync(() => manager.State == RelayState.Running);
        Assert.True(factory.StartCount >= 1);
    }

    // ---------------------------------------------------------------- relaunch

    [Fact]
    public async Task RelaunchAfterExhaustion_StartsAFreshLifecycle()
    {
        var log = new CapturingLogger();

        // Run 1: exhaust the budget and shut the app down in that state.
        var factory1 = new FakeRelayProcessFactory();
        var admin1 = new FakeRelayAdmin { Healthy = false };
        var manager1 = CreateManager(factory1, admin1, log);
        manager1.Start();
        await WaitForExhaustedAsync(manager1);
        manager1.Dispose();

        // Run 2 (fresh process, as if the user relaunched ClipBridge.exe): the persisted
        // Failed state must NOT carry over — a new lifecycle starts with a full budget.
        var factory2 = new FakeRelayProcessFactory();
        var admin2 = new FakeRelayAdmin();
        var log2 = new CapturingLogger();
        using var manager2 = CreateManager(factory2, admin2, log2);
        manager2.Start();
        await WaitForAsync(() => manager2.State == RelayState.Running);

        Assert.Equal(RelayState.Running, manager2.State);
        Assert.Null(manager2.LastFailureReason);
        Assert.Equal(1, factory2.StartCount);
        Assert.Equal(0, manager2.RecoveryRounds);
    }

    [Fact]
    public async Task Recovery_ReportsRunningSoTheTrayCanReturnToActive()
    {
        var factory = new FakeRelayProcessFactory();
        var admin = new FakeRelayAdmin { Healthy = false };
        var log = new CapturingLogger();
        using var manager = CreateManager(factory, admin, log);

        var states = new List<RelayState>();
        manager.StateChanged += (state, _) => { lock (states) { states.Add(state); } };

        manager.Start();
        await WaitForExhaustedAsync(manager);
        admin.Healthy = true;
        await WaitForAsync(() => manager.State == RelayState.Running);

        lock (states)
        {
            // Failed → (recovery) → Starting → Running, so the host can move Sync back
            // from Offline to Active through the existing RelayState.Running handler.
            Assert.Contains(RelayState.Failed, states);
            Assert.Equal(RelayState.Running, states[^1]);
            Assert.Contains(RelayState.Starting, states);
        }
    }
}
