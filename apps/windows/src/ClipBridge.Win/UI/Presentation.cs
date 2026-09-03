using System.Globalization;
using System.Text;
using ClipBridge.Core.Models;
using ClipBridge.Core.Status;

namespace ClipBridge.Win.UI;

public sealed record StatusPresentation(string Title, string Explanation, bool Connected)
{
    public static StatusPresentation From(AppStatusSnapshot s)
    {
        if (s.App == AppState.Starting || s.Relay == RelayState.Starting)
            return new("Starting…", "Starting ClipBridge…", false);
        if (s.Relay == RelayState.Restarting)
            return new("Relay restarting…", "Clipboard sync will resume automatically.", false);
        if (s.App != AppState.Running || s.Relay is RelayState.Failed or RelayState.Stopped || s.Sync == SyncState.Offline)
            return new("Offline", "Unable to reach the relay. Check your connection.", false);
        if (s.Sync == SyncState.Paused)
            return new("Sync paused", "Clipboard sync is paused.", false);
        if (s.Network is NetworkState.NoLan or NetworkState.Unknown || s.LanUrl is null)
            return new("No local network", "No local network connection.", false);
        return new("Connected", "Ready to move text between your devices.", true);
    }
}

public static class HistoryPresentation
{
    public static string Source(string device, string localDevice) =>
        device == localDevice ? "Windows" :
        device.Equals("iphone", StringComparison.OrdinalIgnoreCase) ? "iPhone" : device;

    // A stored record proves arrival at the relay, not delivery to the other device.
    public static string Route(ClipRecord clip, string localDevice) =>
        $"{Source(clip.DeviceId, localDevice)} → Relay";

    public static string Preview(string text)
    {
        // Bound work even for a 2 MiB entry. Never retain a second full-text preview.
        var b = new StringBuilder(120);
        foreach (var rune in text.EnumerateRunes())
        {
            if (b.Length >= 110) { b.Append('…'); break; }
            if (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune))
            {
                if (b.Length > 0 && b[^1] != ' ') b.Append(' ');
            }
            else b.Append(rune.ToString());
        }
        return b.Length == 0 ? "Whitespace only" : b.ToString().Trim();
    }

    public static string Size(long bytes) => bytes < 1024 ? $"{bytes:N0} bytes" :
        bytes < 1024 * 1024 ? $"{bytes / 1024d:N1} KB" : $"{bytes / (1024d * 1024):N2} MB";

    public static string Time(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var time) ? time.ToLocalTime().ToString("MMM d · HH:mm") : "Time unavailable";

    public static string RecentTime(DateTime at, DateTime now)
    {
        var elapsed = now - at;
        return elapsed.TotalMinutes < 1 ? "Just now" : elapsed.TotalHours < 1
            ? $"{(int)elapsed.TotalMinutes} min ago" : at.ToLocalTime().ToString("HH:mm");
    }
}
