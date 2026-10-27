using System.Security.Cryptography;
using System.Text;

namespace ClipBridge.Core.Config;

/// <summary>
/// Generates the shared bearer token for a fresh install.
///
/// Why the app generates it: a new user has no relay .env and no way to invent a token,
/// so "open this JSON file and type a token" was the single biggest reason a stranger
/// could not get ClipBridge working. The token must be unguessable (it is the only auth
/// on a LAN-reachable HTTP port), so it is 256 bits from the OS CSPRNG, URL-safe encoded:
/// 43 ASCII characters, no quotes/backslashes to mangle in JSON or a Shortcut field.
/// </summary>
public static class TokenGenerator
{
    public const int EntropyBytes = 32;

    public static string NewToken(int entropyBytes = EntropyBytes)
    {
        if (entropyBytes < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(entropyBytes), "At least 128 bits are required.");
        }
        var bytes = RandomNumberGenerator.GetBytes(entropyBytes);
        return Base64Url(bytes);
    }

    public static string Base64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// A readable, unique device id for a fresh install: "windows-&lt;machine&gt;-&lt;suffix&gt;".
    /// Must satisfy the relay contract ^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$.
    /// </summary>
    public static string NewDeviceId(string? machineName = null, int suffixLength = 4)
    {
        var machine = machineName ?? Environment.MachineName;
        var builder = new StringBuilder();
        foreach (var c in machine.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(c);
            }
        }
        var host = builder.Length == 0 ? "pc" : builder.ToString();
        if (host.Length > 24)
        {
            host = host[..24];
        }
        var suffix = Base64Url(RandomNumberGenerator.GetBytes(8))
            .ToLowerInvariant()
            .Where(char.IsAsciiLetterOrDigit)
            .Take(Math.Max(0, suffixLength))
            .ToArray();
        return suffix.Length == 0 ? $"windows-{host}" : $"windows-{host}-{new string(suffix)}";
    }

    /// <summary>Short, non-secret fingerprint for logs (never the token itself).</summary>
    public static string Fingerprint(string? token)
        => string.IsNullOrEmpty(token)
            ? "none"
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..8].ToLowerInvariant();
}
