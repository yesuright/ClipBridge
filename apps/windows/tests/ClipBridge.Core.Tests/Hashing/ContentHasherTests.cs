using ClipBridge.Core.Hashing;
using Xunit;

namespace ClipBridge.Core.Tests;

public class ContentHasherTests
{
    [Fact]
    public void EmptyString_MatchesKnownVector()
    {
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ContentHasher.Sha256Hex(""));
    }

    [Fact]
    public void Ascii_MatchesKnownVector()
    {
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ContentHasher.Sha256Hex("abc"));
    }

    [Fact]
    public void ChineseUtf8_MatchesKnownVector()
    {
        // Verified against the relay's Python hashlib.sha256(content.encode('utf-8'))
        Assert.Equal(
            "670d9743542cae3ea7ebe36af56bd53648b0a1126162e78d81a32934a711302e",
            ContentHasher.Sha256Hex("你好"));
    }

    [Fact]
    public void EmojiAndSymbols_ProduceStableHash()
    {
        var content = "Emoji: 🐱🚀 符号：`~!@#$%^&*()_+-={}[]`";
        var hash = ContentHasher.Sha256Hex(content);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, ContentHasher.Sha256Hex(content));
        Assert.NotEqual(ContentHasher.Sha256Hex(content + " "), hash);
    }

    [Fact]
    public void HashPrefix_IsShortAndLengthBounded()
    {
        var hash = ContentHasher.Sha256Hex("hello");
        Assert.Equal(8, ContentHasher.HashPrefix(hash).Length);
        Assert.Equal(hash[..8], ContentHasher.HashPrefix(hash));
        Assert.Equal(hash, ContentHasher.HashPrefix(hash, 64));
        Assert.Equal(hash, ContentHasher.HashPrefix(hash, 128));
    }
}
