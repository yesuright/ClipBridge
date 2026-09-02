using System.Net;
using System.Net.Sockets;
using ClipBridge.Core.Logging;
using ClipBridge.Core.Process;
using ClipBridge.Core.Status;
using Xunit;

namespace ClipBridge.Core.Tests.Process;

/// <summary>Fake child process with controllable lifecycle.</summary>
public sealed class FakeRelayProcess : IRelayProcess
{
    private static int _nextId = 1000;
    public int Id { get; } = Interlocked.Increment(ref _nextId);
    public bool HasExited { get; private set; }
    public int? ExitCode { get; private set; } = null;
    public bool Killed { get; private set; }
    public event Action? Exited;
    public void AssignJob(JobObject job) { } // fakes never assign real job objects
    public bool WaitForExit(int milliseconds) => false; // fakes stay "alive" until killed

    public void Crash(int code = 1)
    {
        if (HasExited) return;
        HasExited = true;
        ExitCode = code;
        Exited?.Invoke();
    }

    public void Kill()
    {
        if (HasExited) return;
        Killed = true;
        Crash(137);
    }

    public void Dispose() => Crash();
}

public sealed class FakeRelayProcessFactory : IRelayProcessFactory
{
    public readonly List<FakeRelayProcess> Started = new();
    private readonly Func<int, FakeRelayProcess>? _factory;

    public FakeRelayProcessFactory(Func<int, FakeRelayProcess>? factory = null) => _factory = factory;

    public FakeRelayProcess Last => Started.Last();
    public int StartCount => Started.Count;

    public IRelayProcess Start(RelayLaunchSpec spec)
    {
        var process = _factory is null ? new FakeRelayProcess() : _factory(StartCount);
        Started.Add(process);
        return process;
    }
}

public sealed class FakeRelayAdmin : IRelayAdminClient
{
    public bool Healthy = true;
    public bool ShutdownAccepted = true;
    public int HealthCalls;
    public int ShutdownCalls;

    public Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        HealthCalls++;
        return Task.FromResult(Healthy);
    }

    public Task<bool> RequestShutdownAsync(CancellationToken ct)
    {
        ShutdownCalls++;
        return Task.FromResult(ShutdownAccepted);
    }
}

public class RelayManagerTests
{
    private static RelayManager CreateManager(
        FakeRelayProcessFactory factory,
        FakeRelayAdmin admin,
        int port = 8787,
        TimeSpan[]? backoff = null,
        TimeSpan? healthTimeout = null,
        TimeSpan? successStability = null,
        Func<int, bool>? portProbe = null)
    {
        var spec = new RelayLaunchSpec("C:\\fake\\ClipBridgeRelay.exe", port, "token", "db", "logs");
        return new RelayManager(
            spec, factory, admin, NullClientLogger.Instance,
            backoff: backoff ?? new[] { TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10) },
            healthTimeout: healthTimeout ?? TimeSpan.FromSeconds(5),
            healthPollInterval: TimeSpan.FromMilliseconds(25),
            successStability: successStability ?? TimeSpan.FromHours(1),
            // Hermetic by default: unit tests must never depend on what port 8787 is doing
            // on the machine right now. The port-conflict test injects the real probe.
            portProbe: portProbe ?? (_ => false));
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.True(condition(), "condition not met within timeout");
    }

    [Fact]
    public async Task Start_ReachesRunning_WhenHealthy()
    {
        var factory = new FakeRelayProcessFactory();
        var admin = new FakeRelayAdmin();
        using var manager = CreateManager(factory, admin);

        manager.Start();
        await WaitForAsync(() => manager.State == RelayState.Running);

        Assert.Equal(1, factory.StartCount);
        Assert.Single(factory.Started);
        Assert.Equal(RelayState.Running, manager.State);
    }

    [Fact]
    public async Task Crash_TriggersRestart_AndRecovers()
    {
        var factory = new FakeRelayProcessFactory();
        var admin = new FakeRelayAdmin();
        using var manager = CreateManager(factory, admin);
        manager.Start();
        await WaitForAsync(() => manager.State == RelayState.Running);

        factory.Last.Crash(); // simulate relay death
        await WaitForAsync(() => manager.State == RelayState.Running && factory.StartCount >= 2);

        Assert.Equal(2, factory.StartCount);
        Assert.NotSame(factory.Started[0], factory.Started[1]);
        Assert.Equal(RelayState.Running, manager.State);
    }

    [Fact]
    public async Task RepeatedCrash_IsBounded_AndEndsFailed()
    {
        var factory = new FakeRelayProcessFactory();
        var admin = new FakeRelayAdmin();
        using var manager = CreateManager(factory, admin, backoff: new[] { TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5) });
        manager.Start();
        await WaitForAsync(() => manager.State == RelayState.Running);

        // Crash 4 times: the 4th exceeds the budget of 3 restarts → Failed.
        for (var i = 0; i < 100; i++)
        {
            if (manager.State == RelayState.Failed)
            {
                break;
            }
            factory.Last.Crash();
            await Task.Delay(20);
        }

        Assert.Equal(RelayState.Failed, manager.State);
        Assert.True(factory.StartCount <= 1 + 3, $"start count {factory.StartCount} should be bounded");
        Assert.Equal("restart_limit", manager.LastFailureReason);
    }

    [Fact]
    public async Task PortAlreadyInUse_BecomesFailedWithoutStarting()
    {
        // Occupy a real loopback port so the TCP probe sees it.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var factory = new FakeRelayProcessFactory();
            var admin = new FakeRelayAdmin { Healthy = false }; // nothing HTTP is listening there
            var realProbe = new Func<int, bool>(PortProbe.IsLoopbackPortOpen); // the REAL probe
            using var manager = CreateManager(factory, admin, port: port, portProbe: realProbe);
            manager.Start();
            await WaitForAsync(() => manager.State == RelayState.Failed);
            Assert.Equal("port_in_use", manager.LastFailureReason);
            Assert.Equal(0, factory.StartCount); // never spawned into a busy port
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Stop_GracefullyRequestsShutdown_ThenKills()
    {
        var factory = new FakeRelayProcessFactory();
        var admin = new FakeRelayAdmin();
        using var manager = CreateManager(factory, admin);
        manager.Start();
        await WaitForAsync(() => manager.State == RelayState.Running);

        manager.Stop(graceful: true);
        // Fake never exits on its own; manager must have killed it after the wait.
        Assert.Equal(RelayState.Stopped, manager.State);
        Assert.True(admin.ShutdownCalls >= 1, "graceful shutdown endpoint must be attempted");
        Assert.True(factory.Last.Killed, "relay must be killed when graceful stop does not finish");
    }

    [Fact]
    public async Task Start_WhenRelayFailsStartup_FailsCleanly()
    {
        var factory = new FakeRelayProcessFactory(_ => new FakeRelayProcess { /* stays alive but unhealthy */ });
        var admin = new FakeRelayAdmin { Healthy = false };
        using var manager = CreateManager(factory, admin, healthTimeout: TimeSpan.FromMilliseconds(300));
        manager.Start();
        await WaitForAsync(() => manager.State == RelayState.Failed, 8000);
        Assert.Equal("startup_timeout", manager.LastFailureReason);
        Assert.True(factory.Last.Killed, "unhealthy relay must be killed, not left running");
    }
}
