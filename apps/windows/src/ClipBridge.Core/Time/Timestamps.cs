using System.Globalization;

namespace ClipBridge.Core.Time;

/// <summary>UTC ISO 8601 helpers shared by client and tests.</summary>
public static class Timestamps
{
    /// <summary>Fixed-width UTC with milliseconds and a Z suffix: 2025-06-01T12:00:00.123Z.</summary>
    public static string UtcNowIso()
        => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
