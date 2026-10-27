using ClipBridge.Core.Logging;
using ClipBridge.Core.Process;
using ClipBridge.Core.Status;
using Xunit;

namespace ClipBridge.Core.Tests.Process;

/// <summary>
/// A port collision on a stranger's PC must not become a permanent failure state.
/// The manager asks the host for one alternative port per start attempt.
/// </summary>
public class RelayManagerPortFallbackTests
{
    private static RelayManager Create(
        FakeRelayProcessFactory factory,
        FakeRelayAdmin admin,
        Func<int, bool> probe,
        Func<int, int?>? fallback,
        Action<int>? changed = null,
        int port = 8787)
    {
        var spec = new RelayLaunchSpec("C:\\fake\\ClipBridgeRelay.exe", port, "token", "db", "logs");
        return new RelayManager(
            spec, factory, admin, NullClientLogger.Instance,
            backoff: new[] { TimeSpan.FromMilliseconds(10) },
            healthTimeout: TimeSpan.FromSeconds(2),
            healthPollInterval: TimeSpan.FromMilliseconds(20),
            successStability: TimeSpan.FromHours(1),
            portProbe: probe,
            portFallback: fallback,
            portChanged: changed);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.True(condition(), "condition not met within timeout");
    }

    [Fact]
    public async Task PortBusy_UsesTheFallbackPortAndStarts()
    {
        var admin = new FakeRelayAdmin { Healthy = false };
        // The relay only becomes healthy once it has actually been spawned.
        var factory = new FakeRelayProcessFactory(_ =>
        {
            admin.Healthy = true;
            return new FakeRelayProcess();
        });
        var changed = new List<int>();

        using var manager = Create(
            factory, admin,
            probe: port => port == 8787,
            fallback: _ => 8788,
            changed: changed.Add);

        manager.Start();
        await WaitForAsync(() => manager.State == RelayState.Running);

        Assert.Equal(new[] { 8788 }, changed);
        Assert.Equal(1, factory.StartCount);
        Assert.Null(manager.LastFailureReason);
    }

    [Fact]
    public async Task PortBusy_AndNoFallbackAvailable_FailsClearly()
    {
        var factory = new FakeRelayProcessFactory();
        var admin = new FakeRelayAdmin { Healthy = false };

        using var manager = Create(factory, admin, probe: port => port == 8787, fallback: _ => null);

        manager.Start();
        await WaitForAsync(() => manager.State == RelayState.Failed);

        Assert.Equal("port_in_use", manager.LastFailureReason);
        Assert.Equal(0, factory.StartCount);
    }

    [Fact]
    public async Task FallbackPortAlsoBusy_DoesNotStartOnACollision()
    {
        var factory = new FakeRelayProcessFactory();
        var admin = new FakeRelayAdmin { Healthy = false };

        using var manager = Create(
            factory, admin,
            probe: port => port is 8787 or 8788,
            fallback: _ => 8788);

        manager.Start();
        await WaitForAsync(() => manager.State == RelayState.Failed);

        Assert.Equal("port_in_use", manager.LastFailureReason);
        Assert.Equal(0, factory.StartCount);
    }

    [Fact]
    public async Task FreePort_DoesNotAskForAFallback()
    {
        var factory = new FakeRelayProcessFactory();
        var admin = new FakeRelayAdmin();
        var asked = 0;

        using var manager = Create(
            factory, admin,
            probe: _ => false,
            fallback: _ => { asked++; return 8788; });

        manager.Start();
        await WaitForAsync(() => manager.State == RelayState.Running);

        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task PortHeldByAHealthyClipBridgeRelay_IsReusedWithoutFallback()
    {
        var factory = new FakeRelayProcessFactory();
        var admin = new FakeRelayAdmin { Healthy = true };
        var asked = 0;

        using var manager = Create(
            factory, admin,
            probe: port => port == 8787,
            fallback: _ => { asked++; return 8788; });

        manager.Start();
        await WaitForAsync(() => manager.State == RelayState.Running);

        Assert.Equal(0, asked);
        Assert.Equal(0, factory.StartCount); // external relay, nothing spawned
    }
}
