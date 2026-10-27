using ClipBridge.Core.Config;
using Xunit;

namespace ClipBridge.Core.Tests.Config;

public class TokenGeneratorTests
{
    [Fact]
    public void NewToken_IsUrlSafe_AndLongEnough()
    {
        var token = TokenGenerator.NewToken();
        Assert.True(token.Length >= 43);
        Assert.Matches("^[A-Za-z0-9_-]+$", token); // safe in JSON, headers and a Shortcut field
        Assert.DoesNotContain("=", token);
    }

    [Fact]
    public void NewToken_DoesNotRepeat()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => TokenGenerator.NewToken()).ToHashSet();
        Assert.Equal(200, tokens.Count);
    }

    [Fact]
    public void NewToken_RejectsWeakEntropy()
        => Assert.Throws<ArgumentOutOfRangeException>(() => TokenGenerator.NewToken(8));

    [Fact]
    public void NewDeviceId_SatisfiesTheRelayContract()
    {
        foreach (var machine in new[] { "DESKTOP-ABC123", "我的电脑", "", "a b.c/d" })
        {
            var id = TokenGenerator.NewDeviceId(machine);
            Assert.Matches("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", id);
        }
    }

    [Fact]
    public void NewDeviceId_IsUniquePerInstall()
    {
        var a = TokenGenerator.NewDeviceId("PC");
        var b = TokenGenerator.NewDeviceId("PC");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Fingerprint_IsShortAndDoesNotLeakTheToken()
    {
        var token = TokenGenerator.NewToken();
        var fingerprint = TokenGenerator.Fingerprint(token);
        Assert.Equal(8, fingerprint.Length);
        Assert.DoesNotContain(token, fingerprint);
        Assert.Equal("none", TokenGenerator.Fingerprint(""));
    }
}
