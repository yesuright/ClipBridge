using ClipBridge.Core.Config;
using Xunit;

namespace ClipBridge.Core.Tests;

public class ClientConfigTests : IDisposable
{
    private static readonly string[] EnvVars =
    {
        "CLIPBRIDGE_RELAY_URL", "CLIPBRIDGE_DEVICE_ID", "CLIPBRIDGE_DEVICE_TOKEN",
        "CLIPBRIDGE_POLL_INTERVAL_MS", "CLIPBRIDGE_HTTP_TIMEOUT_SECONDS",
        "CLIPBRIDGE_SUPPRESS_WINDOW_SECONDS", "CLIPBRIDGE_UPLOAD_MEMORY_SECONDS",
    };

    public ClientConfigTests()
    {
        foreach (var name in EnvVars)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public void Dispose()
    {
        foreach (var name in EnvVars)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void Defaults_AreSane()
    {
        var config = ClientConfig.Load("nonexistent-config.json");
        Assert.Equal("http://127.0.0.1:8787", config.RelayUrl);
        Assert.Equal("windows-main", config.DeviceId);
        Assert.False(config.HasValidToken);
        Assert.Equal(2000, config.PollIntervalMs);
        Assert.Equal(30, config.SuppressWindowSeconds);
    }

    [Fact]
    public void RequiresToken_ToBeUsable()
    {
        var config = ClientConfig.Load("nonexistent-config.json");
        Assert.False(config.HasValidToken);

        var withToken = config with { DeviceToken = "abc" };
        Assert.True(withToken.HasValidToken);
    }

    [Fact]
    public void EnvVars_OverrideDefaults()
    {
        Environment.SetEnvironmentVariable("CLIPBRIDGE_RELAY_URL", "http://192.168.1.5:9000");
        Environment.SetEnvironmentVariable("CLIPBRIDGE_DEVICE_ID", "iphone");
        Environment.SetEnvironmentVariable("CLIPBRIDGE_DEVICE_TOKEN", "env-token-123");
        Environment.SetEnvironmentVariable("CLIPBRIDGE_POLL_INTERVAL_MS", "5000");

        var config = ClientConfig.Load("nonexistent-config.json");

        Assert.Equal("http://192.168.1.5:9000", config.RelayUrl);
        Assert.Equal("iphone", config.DeviceId);
        Assert.Equal("env-token-123", config.DeviceToken);
        Assert.Equal(5000, config.PollIntervalMs);
    }

    [Fact]
    public void PollInterval_IsClamped()
    {
        Environment.SetEnvironmentVariable("CLIPBRIDGE_POLL_INTERVAL_MS", "100");
        Assert.Equal(500, ClientConfig.Load("nonexistent-config.json").PollIntervalMs);

        Environment.SetEnvironmentVariable("CLIPBRIDGE_POLL_INTERVAL_MS", "99999");
        Assert.Equal(60000, ClientConfig.Load("nonexistent-config.json").PollIntervalMs);

        Environment.SetEnvironmentVariable("CLIPBRIDGE_POLL_INTERVAL_MS", "banana");
        Assert.Equal(2000, ClientConfig.Load("nonexistent-config.json").PollIntervalMs);
    }

    [Fact]
    public void JsonFile_IsLoaded()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clipbridge-config-test");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path,
            "{\"RelayUrl\":\"http://example.test:9999\",\"DeviceId\":\"ipad\"," +
            "\"DeviceToken\":\"file-token\",\"PollIntervalMs\":2718}");

        var config = ClientConfig.Load(path);

        Assert.Equal("http://example.test:9999", config.RelayUrl);
        Assert.Equal("ipad", config.DeviceId);
        Assert.Equal("file-token", config.DeviceToken);
        Assert.Equal(2718, config.PollIntervalMs);
    }

    [Fact]
    public void MalformedJson_DoesNotThrow()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clipbridge-config-test");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, "{ not valid json !!!");

        var config = ClientConfig.Load(path);
        Assert.Equal("http://127.0.0.1:8787", config.RelayUrl); // defaults win
    }

    [Fact]
    public void ApiBaseUri_AppendsApiV1Once()
    {
        Assert.Equal("http://127.0.0.1:8787/api/v1",
            new ClientConfig().ApiBaseUri.ToString().TrimEnd('/'));

        var withSlash = new ClientConfig { RelayUrl = "http://127.0.0.1:8787/" };
        Assert.Equal("http://127.0.0.1:8787/api/v1",
            withSlash.ApiBaseUri.ToString().TrimEnd('/'));

        var alreadyV1 = new ClientConfig { RelayUrl = "http://127.0.0.1:8787/api/v1" };
        Assert.Equal("http://127.0.0.1:8787/api/v1",
            alreadyV1.ApiBaseUri.ToString().TrimEnd('/'));
    }

    [Fact]
    public void LanAddress_ProducesValidBaseUri()
    {
        // LAN mode: relay bound to 0.0.0.0, client still points at the PC's LAN IP.
        var lan = new ClientConfig { RelayUrl = "http://192.168.1.123:8787" };
        Assert.Equal("http://192.168.1.123:8787/api/v1/", lan.ApiBaseUri.ToString());

        var lanEnv = Environment.GetEnvironmentVariable("CLIPBRIDGE_RELAY_URL");
        try
        {
            Environment.SetEnvironmentVariable("CLIPBRIDGE_RELAY_URL", "http://10.0.0.42:9000");
            var loaded = ClientConfig.Load("nonexistent-config.json");
            Assert.Equal("http://10.0.0.42:9000/api/v1/", loaded.ApiBaseUri.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLIPBRIDGE_RELAY_URL", lanEnv);
        }
    }
}
