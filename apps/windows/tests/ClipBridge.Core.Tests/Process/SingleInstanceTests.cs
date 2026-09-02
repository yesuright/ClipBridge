using ClipBridge.Core.Process;
using Xunit;

namespace ClipBridge.Core.Tests.Process;

/// <summary>
/// In-process model of the cross-process single-instance protocol. A real second
/// instance is a different process; in the test process the mutex is owned by a
/// dedicated thread so the "second" guard is a different thread — same semantics.
/// </summary>
public class SingleInstanceTests
{
    private static string UniqueId() => "test-" + Guid.NewGuid().ToString("N");

    private static void AcquireOnDedicatedThread(SingleInstanceGuard guard)
    {
        var acquired = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            guard.TryAcquire();
            acquired.Set();
            Thread.Sleep(Timeout.Infinite);
        })
        {
            IsBackground = true,
            Name = "guard-owner-" + Guid.NewGuid().ToString("N"),
        };
        thread.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(10)), "first guard must acquire the lock");
    }

    [Fact]
    public void SecondGuard_IsNotPrimary_WhenFirstOwnsLock()
    {
        var id = UniqueId();
        using (var first = new SingleInstanceGuard(id))
        {
            AcquireOnDedicatedThread(first);
            EnsurePrimary(first);

            using var second = new SingleInstanceGuard(id);
            Assert.False(second.TryAcquire());
            Assert.False(second.IsPrimary);
        }
    }

    private static void EnsurePrimary(SingleInstanceGuard guard)
        => Assert.True(guard.IsPrimary);

    [Fact]
    public void ReleasedGuard_AllowsNextAcquire()
    {
        var id = UniqueId();
        using (var first = new SingleInstanceGuard(id))
        {
            // acquire + release entirely on the owner thread (mutex is thread-affine)
            var released = new ManualResetEventSlim(false);
            var t = new Thread(() =>
            {
                first.TryAcquire();
                first.Dispose();
                released.Set();
            })
            { IsBackground = true };
            t.Start();
            Assert.True(released.Wait(TimeSpan.FromSeconds(10)));
        }
        using var second = new SingleInstanceGuard(id);
        Assert.True(second.TryAcquire());
    }

    [Fact]
    public void ActivationSignal_ReachesPrimary()
    {
        var id = UniqueId();
        using var first = new SingleInstanceGuard(id);
        AcquireOnDedicatedThread(first);

        var activated = new ManualResetEventSlim(false);
        first.Activated += () => activated.Set();

        using var second = new SingleInstanceGuard(id);
        Assert.False(second.TryAcquire());
        second.Signal("activate");

        Assert.True(activated.Wait(TimeSpan.FromSeconds(5)), "primary must receive the activate signal");
    }

    [Fact]
    public void StatusRequest_IsAnsweredByPrimary()
    {
        var id = UniqueId();
        using var first = new SingleInstanceGuard(id);
        AcquireOnDedicatedThread(first);
        first.CommandHandler = line => line == "status" ? "{\"ok\":true}" : null;

        using var second = new SingleInstanceGuard(id);
        Assert.False(second.TryAcquire());
        var reply = second.RequestStatus();

        Assert.Equal("{\"ok\":true}", reply);
    }

    [Fact]
    public void RepeatedRequests_AreAllAnswered()
    {
        var id = UniqueId();
        using var first = new SingleInstanceGuard(id);
        AcquireOnDedicatedThread(first);
        first.CommandHandler = _ => "{\"seq\":true}";

        using var second = new SingleInstanceGuard(id);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("{\"seq\":true}", second.RequestStatus());
        }
    }

    [Fact]
    public void InterleavedCommands_AllWork()
    {
        var id = UniqueId();
        using var first = new SingleInstanceGuard(id);
        AcquireOnDedicatedThread(first);
        var activated = 0;
        first.Activated += () => Interlocked.Increment(ref activated);
        first.CommandHandler = _ => "{\"ok\":true}";

        using var second = new SingleInstanceGuard(id);
        Assert.False(second.TryAcquire());
        second.Signal("activate");
        Assert.Equal("{\"ok\":true}", second.RequestStatus());
        second.Signal("activate");
        Assert.Equal("{\"ok\":true}", second.RequestStatus());
        Assert.Equal(2, activated);
    }

    [Fact]
    public void ParseCommand_MapsKnownLines()
    {
        Assert.Equal(InstanceCommand.Activate, SingleInstanceGuard.ParseCommand("activate"));
        Assert.Equal(InstanceCommand.Status, SingleInstanceGuard.ParseCommand("status"));
        Assert.Equal(InstanceCommand.Exit, SingleInstanceGuard.ParseCommand("exit"));
        Assert.Equal(InstanceCommand.None, SingleInstanceGuard.ParseCommand("something-else"));
    }
}
