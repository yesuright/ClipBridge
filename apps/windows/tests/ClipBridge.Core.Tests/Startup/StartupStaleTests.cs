using ClipBridge.Core.Process;
using ClipBridge.Core.Startup;
using Xunit;

namespace ClipBridge.Core.Tests.Startup;

/// <summary>
/// "Start with Windows" must keep working after the install folder moves — a very common
/// real-world case (unzip to Downloads, then move to Program Files / D:).
/// </summary>
public class StartupStaleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clipbridge-startup-" + Guid.NewGuid().ToString("N"));
    private readonly string _exe;

    public StartupStaleTests()
    {
        Directory.CreateDirectory(_dir);
        _exe = Path.Combine(_dir, "ClipBridge.exe");
        File.WriteAllText(_exe, "stub");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void ExtractExecutable_HandlesQuotedAndUnquotedPaths()
    {
        Assert.Equal(@"C:\Program Files\ClipBridge\ClipBridge.exe",
            StartupManager.ExtractExecutable("\"C:\\Program Files\\ClipBridge\\ClipBridge.exe\" --tray-autostart"));
        Assert.Equal(@"C:\Tools\ClipBridge.exe",
            StartupManager.ExtractExecutable(@"C:\Tools\ClipBridge.exe --tray-autostart"));
        Assert.Null(StartupManager.ExtractExecutable(""));
        Assert.Null(StartupManager.ExtractExecutable(null));
    }

    [Fact]
    public void IsStale_FalseForTheCurrentExe()
    {
        var command = StartupManager.BuildCommandLine(_exe);
        Assert.False(StartupManager.IsStale(command, _exe));
    }

    [Fact]
    public void IsStale_TrueWhenTheFolderWasMoved()
    {
        var gone = Path.Combine(_dir, "old", "ClipBridge.exe");
        Assert.True(StartupManager.IsStale(StartupManager.BuildCommandLine(gone), _exe));
    }

    [Fact]
    public void IsStale_TrueWhenTheEntryIsEmpty()
        => Assert.True(StartupManager.IsStale(null, _exe));

    [Fact]
    public void IsStale_DoesNotHijackAnotherLiveInstallation()
    {
        var otherDir = Path.Combine(_dir, "other");
        Directory.CreateDirectory(otherDir);
        var otherExe = Path.Combine(otherDir, "ClipBridge.exe");
        File.WriteAllText(otherExe, "stub");

        // Both exist: this is a deliberate second copy, not a broken shortcut.
        Assert.False(StartupManager.IsStale(StartupManager.BuildCommandLine(otherExe), _exe));
    }

    [Fact]
    public void RepairIfStale_RewritesToTheCurrentPath_OnlyWhenStale()
    {
        var store = new FakeStartup();
        Assert.False(StartupManager.RepairIfStale(store, _exe)); // disabled → untouched

        store.Enable(StartupManager.BuildCommandLine(Path.Combine(_dir, "gone", "ClipBridge.exe")));
        Assert.True(StartupManager.RepairIfStale(store, _exe));
        Assert.Contains(_exe, store.GetCommand());

        Assert.False(StartupManager.RepairIfStale(store, _exe)); // already correct
    }

    private sealed class FakeStartup : IStartupRegistration
    {
        private string? _command;
        public bool IsEnabled() => _command is not null;
        public string? GetCommand() => _command;
        public void Enable(string commandLine) => _command = commandLine;
        public void Disable() => _command = null;
    }
}

public class RelayHealthPayloadTests
{
    [Theory]
    [InlineData("""{"status":"ok","service":"clipbridge-relay","version":"0.4.0"}""")]
    [InlineData("""{"service":"ClipBridge-Relay"}""")]
    [InlineData("""{"status":"ok"}""")] // older relay: still recognised
    public void RecognisesAClipBridgeRelay(string body)
        => Assert.True(HttpRelayAdminClient.LooksLikeClipBridge(body));

    [Theory]
    [InlineData("")]
    [InlineData("OK")]
    [InlineData("<html>hello</html>")]
    [InlineData("""{"status":"degraded"}""")]
    [InlineData("""{"service":"something-else"}""")]
    [InlineData("""["status","ok"]""")]
    [InlineData("null")]
    public void RejectsAnythingElse(string body)
        => Assert.False(HttpRelayAdminClient.LooksLikeClipBridge(body));

    [Fact]
    public void AGeneric200OnOurPortIsNotMistakenForOurRelay()
    {
        // Port 8787 is popular; a printer/dev server answering 200 must be a conflict,
        // not "our relay is already running".
        Assert.False(HttpRelayAdminClient.LooksLikeClipBridge("{\"ok\":true}"));
    }
}
