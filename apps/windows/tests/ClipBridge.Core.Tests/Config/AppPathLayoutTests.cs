using ClipBridge.Core.Config;
using Xunit;

namespace ClipBridge.Core.Tests.Config;

/// <summary>
/// Path resolution must be independent of the Windows account name, the install folder
/// and the current working directory, while still honouring explicit overrides.
/// </summary>
public class AppPathLayoutTests
{
    private static Func<Environment.SpecialFolder, string> Folders(string roaming, string local) => folder => folder switch
    {
        Environment.SpecialFolder.ApplicationData => roaming,
        Environment.SpecialFolder.LocalApplicationData => local,
        Environment.SpecialFolder.UserProfile => @"C:\Users\someone",
        _ => "",
    };

    [Fact]
    public void Default_UsesPerUserRoamingAndLocalFolders()
    {
        var layout = AppPathLayout.FromEnvironment(
            name => null, Folders(@"C:\Users\alice\AppData\Roaming", @"C:\Users\alice\AppData\Local"));

        Assert.Equal(@"C:\Users\alice\AppData\Roaming\ClipBridge", layout.ConfigDir);
        Assert.Equal(@"C:\Users\alice\AppData\Local\ClipBridge", layout.DataDir);
        Assert.Equal("default", layout.Source);
        Assert.EndsWith("config.json", layout.ConfigFile);
        Assert.EndsWith("clipbridge.db", layout.DatabaseFile);
    }

    [Fact]
    public void DifferentUser_ProducesDifferentPaths_WithNoHardcodedName()
    {
        var alice = AppPathLayout.FromEnvironment(
            name => null, Folders(@"C:\Users\alice\AppData\Roaming", @"C:\Users\alice\AppData\Local"));
        var bob = AppPathLayout.FromEnvironment(
            name => null, Folders(@"C:\Users\bob\AppData\Roaming", @"C:\Users\bob\AppData\Local"));

        Assert.NotEqual(alice.ConfigDir, bob.ConfigDir);
        Assert.DoesNotContain("alice", bob.ConfigDir, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("devuser", alice.ConfigDir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Home_Override_SplitsConfigAndDataUnderOneRoot()
    {
        var layout = AppPathLayout.FromEnvironment(
            name => name == AppPathLayout.HomeEnvVar ? @"D:\Profiles\测试 用户\ClipBridge Home" : null,
            Folders(@"C:\Users\a\AppData\Roaming", @"C:\Users\a\AppData\Local"));

        Assert.Equal(@"D:\Profiles\测试 用户\ClipBridge Home\config", layout.ConfigDir);
        Assert.Equal(@"D:\Profiles\测试 用户\ClipBridge Home\data", layout.DataDir);
        Assert.Equal(AppPathLayout.HomeEnvVar, layout.Source);
    }

    [Fact]
    public void ExplicitDirectories_OverrideIndependently()
    {
        var layout = AppPathLayout.FromEnvironment(
            name => name switch
            {
                AppPathLayout.ConfigDirEnvVar => @"E:\cfg",
                AppPathLayout.DataDirEnvVar => @"E:\state",
                _ => null,
            },
            Folders(@"C:\roaming", @"C:\local"));

        Assert.Equal(@"E:\cfg", layout.ConfigDir);
        Assert.Equal(@"E:\state", layout.DataDir);
        Assert.Equal($"{AppPathLayout.ConfigDirEnvVar}/{AppPathLayout.DataDirEnvVar}", layout.Source);
    }

    [Theory]
    [InlineData(@"relative\path")]
    [InlineData(@".\here")]
    [InlineData("")]
    [InlineData("   ")]
    public void InvalidOverrides_FallBackToStandardFolders(string value)
    {
        var layout = AppPathLayout.FromEnvironment(
            name => name == AppPathLayout.HomeEnvVar ? value : null,
            Folders(@"C:\roaming", @"C:\local"));

        Assert.Equal(@"C:\roaming\ClipBridge", layout.ConfigDir);
        Assert.Equal(@"C:\local\ClipBridge", layout.DataDir);
        Assert.True(Path.IsPathFullyQualified(layout.ConfigDir));
    }

    [Fact]
    public void EmptySpecialFolders_StillProduceAbsolutePaths()
    {
        // Locked-down or redirected profiles can return "" for ApplicationData.
        var layout = AppPathLayout.FromEnvironment(name => null, _ => "");

        Assert.True(Path.IsPathFullyQualified(layout.ConfigDir), layout.ConfigDir);
        Assert.True(Path.IsPathFullyQualified(layout.DataDir), layout.DataDir);
    }

    [Fact]
    public void UnicodeAndSpaces_RoundTripThroughPathCombine()
    {
        var layout = AppPathLayout.ForHome(@"C:\Users\名字 带空格\ClipBridge");
        Assert.Contains("名字 带空格", layout.ConfigFile);
        Assert.Equal(Path.Combine(layout.ConfigDir, "config.json"), layout.ConfigFile);
    }

    [Fact]
    public void ConfigVersion_DefaultsToCurrentSchema()
    {
        Assert.Equal(ConfigStore.CurrentSchemaVersion, new ClientConfig().ConfigVersion);
    }
}
