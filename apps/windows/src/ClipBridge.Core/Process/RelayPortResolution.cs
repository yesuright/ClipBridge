namespace ClipBridge.Core.Process;

/// <summary>Outcome of "which local port should the relay use?".</summary>
public sealed record PortResolution(int Port, bool Changed, string? Reason, bool Resolved)
{
    public bool FallbackApplied => Changed && Resolved;
}

/// <summary>
/// Decides the local relay port when the configured one is already taken.
///
/// Policy (portability requirement: a stranger's PC is not our test machine):
/// - Free port → use it, change nothing.
/// - Port taken by an already-running ClipBridge relay → keep it (external/earlier instance).
/// - Port taken by anything else → move to the next free port rather than sitting in a
///   permanent "Failed" state. The caller persists the new port and tells the user, because
///   the iPhone Shortcut pins the URL.
/// - No free port nearby → report unresolved; the caller shows a clear error.
///
/// The decision itself is pure and injectable; only <see cref="FindFreePort"/> touches the OS.
/// </summary>
public static class RelayPortResolution
{
    public const int DefaultScanAttempts = 20;

    public static PortResolution Resolve(
        int desiredPort,
        Func<int, bool> isPortFree,
        Func<int, bool> isClipBridgeRelayHealthy,
        int scanAttempts = DefaultScanAttempts)
    {
        if (desiredPort is < 1 or > 65535)
        {
            return new PortResolution(desiredPort, false, $"port {desiredPort} is not a valid TCP port", false);
        }
        if (isPortFree(desiredPort))
        {
            return new PortResolution(desiredPort, false, null, true);
        }
        if (isClipBridgeRelayHealthy(desiredPort))
        {
            return new PortResolution(desiredPort, false, "an existing ClipBridge relay is already serving", true);
        }
        for (var offset = 1; offset <= scanAttempts; offset++)
        {
            var candidate = desiredPort + offset;
            if (candidate > 65535)
            {
                break;
            }
            if (isPortFree(candidate))
            {
                return new PortResolution(
                    candidate, true,
                    $"port {desiredPort} is used by another program; moved to {candidate}", true);
            }
        }
        return new PortResolution(
            desiredPort, false,
            $"port {desiredPort} is used by another program and no free port was found in the next {scanAttempts} ports",
            false);
    }

    /// <summary>OS-backed scan used by the caller in the default (non-test) path.</summary>
    public static int? FindFreePort(int from, int scanAttempts = DefaultScanAttempts)
    {
        for (var offset = 1; offset <= scanAttempts; offset++)
        {
            var candidate = from + offset;
            if (candidate > 65535)
            {
                return null;
            }
            if (PortProbe.IsBindable(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}
