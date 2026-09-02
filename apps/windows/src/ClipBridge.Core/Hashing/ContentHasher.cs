using System.Security.Cryptography;
using System.Text;

namespace ClipBridge.Core.Hashing;

/// <summary>
/// Protocol content hashing: SHA-256 over the UTF-8 bytes of the content,
/// lowercase hex — identical to the relay's computation.
/// </summary>
public static class ContentHasher
{
    public static string Sha256Hex(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Short, safe log representation of a hash (never the full content).</summary>
    public static string HashPrefix(string hash, int length = 8)
        => hash[..Math.Min(length, hash.Length)];
}
