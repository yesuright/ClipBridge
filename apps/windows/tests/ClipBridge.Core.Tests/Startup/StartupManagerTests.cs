using ClipBridge.Core.Startup;
using Xunit;

namespace ClipBridge.Core.Tests.Startup;

public class StartupManagerTests
{
    private sealed class FakeStartup : IStartupRegistration
    {
        public bool Enabled;
        public string? Command;

        public bool IsEnabled() => Enabled;
        public void Enable(string commandLine) { Enabled = true; Command = commandLine; }
        public void Disable() { Enabled = false; Command = null; }
    }

    [Fact]
    public void SetEnabled_EnableRegistersCommand()
    {
        var store = new FakeStartup();
        StartupManager.SetEnabled(store, true, "\"C:\\app\\ClipBridge.exe\" --tray-autostart");
        Assert.True(store.IsEnabled());
        Assert.Contains("--tray-autostart", store.Command);
    }

    [Fact]
    public void SetEnabled_DisableRemoves()
    {
        var store = new FakeStartup { Enabled = true, Command = "x" };
        StartupManager.SetEnabled(store, false, "x");
        Assert.False(store.IsEnabled());
        Assert.Null(store.Command);
    }

    [Fact]
    public void Toggle_Toggles()
    {
        var store = new FakeStartup { Enabled = true };
        StartupManager.Toggle(store, "cmd");
        Assert.False(store.IsEnabled());
        StartupManager.Toggle(store, "cmd");
        Assert.True(store.IsEnabled());
        Assert.Equal("cmd", store.Command);
    }

    [Fact]
    public void BuildCommandLine_QuotesPath()
    {
        var cmd = StartupManager.BuildCommandLine(@"C:\Program Files\ClipBridge\ClipBridge.exe");
        Assert.Equal("\"C:\\Program Files\\ClipBridge\\ClipBridge.exe\" --tray-autostart", cmd);
    }
}
