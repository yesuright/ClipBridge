using System.Text.Json;
using ClipBridge.Core.Config;
using Xunit;

namespace ClipBridge.Core.Tests.Config;

public class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "clipbridge store 配置 " + Guid.NewGuid().ToString("N"));

    public ConfigStoreTests() => Directory.CreateDirectory(_dir);

    private string PathOf(string name = "config.json") => Path.Combine(_dir, name);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Read_MissingFile_IsMissing_NotAnError()
    {
        var result = ConfigStore.Read(PathOf());
        Assert.Equal(ConfigFileState.Missing, result.State);
        Assert.False(result.Config.HasValidToken);
        Assert.Null(result.Note);
    }

    [Fact]
    public void Read_LegacyConfigWithoutVersion_LoadsAndKeepsToken()
    {
        File.WriteAllText(PathOf(), """
            {"RelayUrl":"http://127.0.0.1:8787","DeviceId":"windows-main",
             "DeviceToken":"legacy-token-123","PollIntervalMs":2000}
            """);

        var result = ConfigStore.Read(PathOf());

        Assert.Equal(ConfigFileState.Loaded, result.State);
        Assert.Equal("legacy-token-123", result.Config.DeviceToken);
        Assert.Equal("windows-main", result.Config.DeviceId);
        Assert.True(result.Config.ManageRelay);
        Assert.Null(result.Note); // a valid legacy file is not "normalized" into a warning
    }

    [Fact]
    public void Read_Malformed_BacksUpOriginalBytes_AndKeepsThemRecoverable()
    {
        var original = "{ this is not json — token: keep-me";
        File.WriteAllText(PathOf(), original);

        var result = ConfigStore.Read(PathOf());

        Assert.Equal(ConfigFileState.Malformed, result.State);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        Assert.Equal(original, File.ReadAllText(result.BackupPath!));
        // Reading must never delete or rewrite the broken file by itself.
        Assert.Equal(original, File.ReadAllText(PathOf()));
    }

    [Fact]
    public void Read_Malformed_DoesNotPileUpDuplicateBackups()
    {
        File.WriteAllText(PathOf(), "{ broken ");
        var first = ConfigStore.Read(PathOf());
        var second = ConfigStore.Read(PathOf());

        Assert.Equal(first.BackupPath, second.BackupPath);
        Assert.Single(Directory.GetFiles(_dir, "config.corrupt-*.json"));
    }

    [Fact]
    public void Write_IsAtomic_RoundTripsUnicodeAndSpaces()
    {
        var config = new ClientConfig
        {
            DeviceToken = "tok-üñî-测试",
            DeviceId = "windows-测试",
            RelayPort = 9123,
            RelayUrl = "http://127.0.0.1:9123",
        };

        ConfigStore.Write(config, PathOf());
        var read = ConfigStore.Read(PathOf());

        Assert.Equal(ConfigFileState.Loaded, read.State);
        Assert.Equal("tok-üñî-测试", read.Config.DeviceToken);
        Assert.Equal(ConfigStore.CurrentSchemaVersion, read.Config.ConfigVersion);
        // No temp file may be left behind after an atomic write.
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Write_CreatesMissingDirectory()
    {
        var nested = Path.Combine(_dir, "new folder", "config.json");
        ConfigStore.Write(new ClientConfig { DeviceToken = "t" }, nested);
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Write_ReportsAUsableError_WhenTargetIsADirectory()
    {
        var blocked = Path.Combine(_dir, "config.json");
        Directory.CreateDirectory(blocked);

        var error = Record.Exception(() => ConfigStore.Write(new ClientConfig { DeviceToken = "t" }, blocked));

        var writeError = Assert.IsType<ConfigWriteException>(error);
        Assert.Contains("Could not write", writeError.Message);
        Assert.False(ConfigStore.TryWrite(new ClientConfig { DeviceToken = "t" }, blocked, out var message));
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Fact]
    public void Normalize_RepairsCommonHandEditMistakes()
    {
        var config = new ClientConfig
        {
            RelayUrl = "127.0.0.1:8787",       // no scheme
            RelayPort = 9000,                  // does not match the URL port
            DeviceId = "my pc!",               // rejected by the relay contract
            LanIpOverride = "not-an-ip",
        };

        var normalized = config.Normalize(out var notes);

        Assert.Equal("http://127.0.0.1:9000", normalized.RelayUrl);
        Assert.Equal("mypc", normalized.DeviceId);
        Assert.Equal("", normalized.LanIpOverride);
        Assert.NotEmpty(notes);
    }

    [Fact]
    public void Normalize_AddsTheMissingScheme_ButKeepsALanAddress()
    {
        var config = new ClientConfig { RelayUrl = "192.168.1.14:8787", RelayPort = 8787 };
        var normalized = config.Normalize(out _);
        Assert.Equal("http://192.168.1.14:8787", normalized.RelayUrl);
    }

    [Fact]
    public void Normalize_KeepsRemoteRelayUrl_WhenRelayIsExternal()
    {
        var config = new ClientConfig
        {
            ManageRelay = false,
            RelayUrl = "http://192.168.1.50:9100",
            RelayPort = 8787,
        };
        var normalized = config.Normalize(out var notes);
        Assert.Equal("http://192.168.1.50:9100", normalized.RelayUrl);
        Assert.Empty(notes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url at all")]
    [InlineData("ftp://192.168.1.2:21")]
    [InlineData("http://")]
    public void BuildApiBaseUri_NeverThrows_AndReportsTheFallback(string url)
    {
        var config = new ClientConfig { RelayUrl = url };
        var uri = config.BuildApiBaseUri(out var note);
        Assert.Equal("http", uri.Scheme);
        Assert.NotNull(note);
        Assert.Null(Record.Exception(() => config.ApiBaseUri));
    }

    [Fact]
    public void JsonShape_IsStableForExistingTools()
    {
        var json = JsonSerializer.Serialize(new ClientConfig { DeviceToken = "x" });
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("RelayUrl", out _));
        Assert.True(doc.RootElement.TryGetProperty("DeviceToken", out _));
        Assert.True(doc.RootElement.TryGetProperty("RelayPort", out _));
        Assert.True(doc.RootElement.TryGetProperty("ConfigVersion", out _));
    }
}
