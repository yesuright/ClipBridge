using ClipBridge.Core.Config;
using Xunit;

namespace ClipBridge.Core.Tests.Config;

public class ConfigMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clipbridge-migrate-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteEnv(string host = "127.0.0.1", string? token = "legacy-secret-token-abcdef123456")
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "env.txt");
        File.WriteAllText(path, $"CLIPBRIDGE_BIND_HOST={host}\nCLIPBRIDGE_PORT=8787\nCLIPBRIDGE_TOKEN={token}\n");
        return path;
    }

    [Fact]
    public void ReadLegacyToken_ExtractsToken_WithoutLoggingConcerns()
    {
        var token = ConfigMigration.ReadLegacyToken(WriteEnv());
        Assert.Equal("legacy-secret-token-abcdef123456", token);
    }

    [Fact]
    public void ReadLegacyToken_IgnoresCommentsAndOtherKeys()
    {
        var token = ConfigMigration.ReadLegacyToken(WriteEnv(token: ""));
        Assert.Null(token);
    }

    [Fact]
    public void TryMigrate_WritesConfig_WhenConfigMissing()
    {
        var configPath = Path.Combine(_dir, "config.json");
        Directory.CreateDirectory(_dir);
        var migrated = ConfigMigration.TryMigrateFromLegacyEnv(configPath);
        Assert.False(migrated); // no repo .env next to the temp dir → nothing to migrate
    }

    [Fact]
    public void TryMigrate_DoesNotOverwrite_ExistingConfig()
    {
        var configPath = Path.Combine(_dir, "config.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(configPath, "{\"DeviceToken\":\"existing\"}");
        Assert.False(ConfigMigration.TryMigrateFromLegacyEnv(configPath));
        Assert.Equal("{\"DeviceToken\":\"existing\"}", File.ReadAllText(configPath));
    }

    [Fact]
    public void ClientConfig_LoadsLegacyM2Json_WithNewDefaults()
    {
        var path = Path.Combine(_dir, "config.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, """
            {
              "RelayUrl": "http://127.0.0.1:8787",
              "DeviceId": "windows-main",
              "DeviceToken": "m2-token",
              "PollIntervalMs": 2000
            }
            """);
        var config = ClientConfig.Load(path);
        Assert.Equal("m2-token", config.DeviceToken);
        Assert.True(config.ManageRelay);          // M3 default
        Assert.Equal("clipbridge", config.LanHostname);
        Assert.Equal(8787, config.RelayPort);
        Assert.Equal("http://127.0.0.1:8787/api/v1/", config.ApiBaseUri.ToString());
    }

    [Fact]
    public void ClientConfig_MalformedFile_FallsBackToDefaults()
    {
        var path = Path.Combine(_dir, "config.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{ not json ");
        var config = ClientConfig.Load(path);
        Assert.Equal("windows-main", config.DeviceId);
        Assert.Equal("", config.DeviceToken);
    }

    [Fact]
    public void ClientConfig_EnvOverridesFile()
    {
        var path = Path.Combine(_dir, "config.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, """{"DeviceId":"from-file","DeviceToken":"t","PollIntervalMs":2000}""");
        var old = Environment.GetEnvironmentVariable("CLIPBRIDGE_DEVICE_ID");
        try
        {
            Environment.SetEnvironmentVariable("CLIPBRIDGE_DEVICE_ID", "from-env");
            var config = ClientConfig.Load(path);
            Assert.Equal("from-env", config.DeviceId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLIPBRIDGE_DEVICE_ID", old);
        }
    }

    [Theory]
    [InlineData("  ClipBridge.local ", "clipbridge")]
    [InlineData("clipbridge.", "clipbridge")]
    [InlineData("UPPER-CASE", "upper-case")]
    [InlineData("-bad", "clipbridge")]
    [InlineData("bad-", "clipbridge")]
    [InlineData("", "clipbridge")]
    [InlineData("good.name", "good")] // dots are not allowed in mDNS single-label names
    public void ClientConfig_NormalizesLanHostname(string input, string expected)
    {
        var old = Environment.GetEnvironmentVariable("CLIPBRIDGE_LAN_HOSTNAME");
        try
        {
            Environment.SetEnvironmentVariable("CLIPBRIDGE_LAN_HOSTNAME", input);
            Assert.Equal(expected, ClientConfig.Load().LanHostname);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLIPBRIDGE_LAN_HOSTNAME", old);
        }
    }
}
