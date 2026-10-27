using ClipBridge.Core.Config;
using Xunit;

namespace ClipBridge.Core.Tests.Config;

/// <summary>
/// First-run behaviour is the core portability contract: a machine with no ClipBridge
/// history, no developer .env and no hand-edited JSON must end up with a working config.
/// </summary>
[Collection("ClipBridge environment")]
public class ConfigBootstrapTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "clipbridge bootstrap 空 格 " + Guid.NewGuid().ToString("N"));

    public ConfigBootstrapTests() => Directory.CreateDirectory(_root);

    private AppPathLayout Layout => AppPathLayout.ForHome(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void FreshMachine_IsProvisionedAutomatically()
    {
        var result = ConfigBootstrap.Ensure(Layout);

        Assert.Equal(ConfigBootstrapOutcome.Provisioned, result.Outcome);
        Assert.True(result.IsUsable);
        Assert.True(File.Exists(result.ConfigPath));
        Assert.True(result.Config.DeviceToken.Length >= 32);
        Assert.Matches("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", result.Config.DeviceId);
        Assert.StartsWith("clipbridge-", result.Config.LanHostname);
        Assert.Equal(8787, result.Config.RelayPort);
    }

    [Fact]
    public void ProvisionedConfig_SurvivesARestart_WithTheSameToken()
    {
        var first = ConfigBootstrap.Ensure(Layout);
        var second = ConfigBootstrap.Ensure(Layout);

        Assert.Equal(ConfigBootstrapOutcome.Existing, second.Outcome);
        Assert.Equal(first.Config.DeviceToken, second.Config.DeviceToken);
        Assert.Equal(first.Config.DeviceId, second.Config.DeviceId);
        Assert.False(second.WasWritten);
    }

    [Fact]
    public void ExistingValidConfig_IsNeverRewritten()
    {
        Directory.CreateDirectory(Layout.ConfigDir);
        var json = """
            {"DeviceToken":"my-existing-token","DeviceId":"windows-main",
             "RelayPort":8787,"RelayUrl":"http://127.0.0.1:8787"}
            """;
        File.WriteAllText(Layout.ConfigFile, json);

        var result = ConfigBootstrap.Ensure(Layout);

        Assert.Equal(ConfigBootstrapOutcome.Existing, result.Outcome);
        Assert.Equal("my-existing-token", result.Config.DeviceToken);
        Assert.Equal(json, File.ReadAllText(Layout.ConfigFile)); // byte-for-byte untouched
    }

    [Fact]
    public void CorruptConfig_IsBackedUpThenReplaced()
    {
        Directory.CreateDirectory(Layout.ConfigDir);
        File.WriteAllText(Layout.ConfigFile, "{ broken json with an old token: abc123");

        var result = ConfigBootstrap.Ensure(Layout);

        Assert.Equal(ConfigBootstrapOutcome.Recovered, result.Outcome);
        Assert.NotNull(result.BackupPath);
        Assert.Contains("abc123", File.ReadAllText(result.BackupPath!));
        Assert.NotEqual("{ broken json with an old token: abc123", File.ReadAllText(Layout.ConfigFile));
        Assert.True(ConfigBootstrap.Ensure(Layout).Config.HasValidToken);
    }

    [Fact]
    public void TokenlessConfig_KeepsItsOtherSettings()
    {
        Directory.CreateDirectory(Layout.ConfigDir);
        File.WriteAllText(Layout.ConfigFile,
            """{"DeviceToken":"","DeviceId":"windows-office","RelayPort":9123,"RelayUrl":"http://127.0.0.1:9123"}""");

        var result = ConfigBootstrap.Ensure(Layout);

        Assert.Equal(ConfigBootstrapOutcome.Provisioned, result.Outcome);
        Assert.True(result.Config.HasValidToken);
        Assert.Equal(9123, result.Config.RelayPort);      // user's port choice is respected
        Assert.Equal("windows-office", result.Config.DeviceId); // and their device name
    }

    [Fact]
    public void UnwritableConfigLocation_IsReportedNotSwallowed()
    {
        Directory.CreateDirectory(Layout.ConfigDir);
        Directory.CreateDirectory(Layout.ConfigFile); // a directory where the file must go

        var result = ConfigBootstrap.Ensure(Layout);

        Assert.Equal(ConfigBootstrapOutcome.Unusable, result.Outcome);
        Assert.False(result.IsUsable);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Contains("Could not write", result.Error);
    }

    [Fact]
    public void LegacyEnv_InsideACheckout_IsImportedForDeveloperBuilds()
    {
        var appDir = MakeFakeCheckout();

        Assert.Equal(
            Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..", "services", "relay", ".env")),
            ConfigMigration.FindLegacyRelayEnv(appDir));
        Assert.True(ConfigMigration.TryReadLegacyToken(out var token, out _, appDir), "token should be readable");
        Assert.Equal("dev-token-from-repo", token);
    }

    [Fact]
    public void LegacyEnv_IsNotImportedByAPackagedArtifact()
    {
        // artifacts\ClipBridge sits inside a checkout but is a packaged build, not a dev run.
        var repo = Path.Combine(_root, "checkout2");
        var packaged = Path.Combine(repo, "artifacts", "ClipBridge");
        MakeFakeCheckout(repo);
        Directory.CreateDirectory(packaged);

        Assert.Null(ConfigMigration.FindLegacyRelayEnv(packaged));
    }

    /// <summary>Creates &lt;root&gt;\apps\windows\src\ClipBridge.Win + services\relay\.env.</summary>
    private string MakeFakeCheckout(string? rootOverride = null)
    {
        var repo = rootOverride ?? Path.Combine(_root, "checkout");
        var envDir = Path.Combine(repo, "services", "relay");
        Directory.CreateDirectory(envDir);
        File.WriteAllText(Path.Combine(envDir, ".env"), "CLIPBRIDGE_TOKEN=dev-token-from-repo\n");
        var appDir = Path.Combine(repo, "apps", "windows", "src", "ClipBridge.Win");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "ClipBridge.Win.csproj"), "<Project/>");
        return appDir;
    }

    [Fact]
    public void LegacyEnv_OutsideACheckout_IsNeverImported()
    {
        // A folder that merely contains services/relay/.env is not a ClipBridge checkout.
        var notARepo = Path.Combine(_root, "random", "services", "relay");
        Directory.CreateDirectory(notARepo);
        File.WriteAllText(Path.Combine(notARepo, ".env"), "CLIPBRIDGE_TOKEN=someone-elses-token\n");

        Assert.Null(ConfigMigration.FindLegacyRelayEnv(Path.Combine(_root, "random")));
    }

    [Fact]
    public void EnvironmentOverrides_StillWork_AndAreNeverPersisted()
    {
        var previous = Environment.GetEnvironmentVariable("CLIPBRIDGE_DEVICE_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("CLIPBRIDGE_DEVICE_TOKEN", "token-from-environment-only");
            var result = ConfigBootstrap.Ensure(Layout);

            Assert.True(result.IsUsable);
            Assert.Equal("token-from-environment-only", result.Config.DeviceToken);
            // An override is a runtime input, not something to write into the user's config.
            Assert.False(File.Exists(result.ConfigPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLIPBRIDGE_DEVICE_TOKEN", previous);
        }
    }

    [Fact]
    public void Startup_DoesNotDependOnTheCurrentWorkingDirectory()
    {
        var cwd = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = Path.GetTempPath();
            var result = ConfigBootstrap.Ensure(Layout);
            Assert.Equal(ConfigBootstrapOutcome.Provisioned, result.Outcome);
            Assert.StartsWith(Path.GetFullPath(_root), result.ConfigPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.CurrentDirectory = cwd;
        }
    }
}
