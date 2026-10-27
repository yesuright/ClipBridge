using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ClipBridge.Core.Startup;

/// <summary>
/// User-level "Start with Windows" registration. HKCU Run is the chosen mechanism:
/// no admin rights, per-user, reversible, trivially detectable, installer-friendly.
/// Default = OFF; it is only ever enabled explicitly by the user (tray menu / CLI).
/// </summary>
public interface IStartupRegistration
{
    bool IsEnabled();
    string? GetCommand();
    void Enable(string commandLine);
    void Disable();
}

/// <summary>Real implementation over HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartupRegistration : IStartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "ClipBridge";

    /// <summary>
    /// Tests inject a different base path (a fake hive root) or the registry API is faked.
    /// </summary>
    private readonly string _keyPath;

    public WindowsStartupRegistration(string? keyPath = null) => _keyPath = keyPath ?? RunKeyPath;

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: false);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public string? GetCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch
        {
            return null;
        }
    }

    public void Enable(string commandLine)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
        key.SetValue(ValueName, commandLine, RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
        key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>
/// Pure enable/disable/repair logic on top of an <see cref="IStartupRegistration"/> store.
/// The command line is built by the host (tray) — the logic here is testable with a fake.
/// </summary>
public static class StartupManager
{
    public static bool IsEnabled(IStartupRegistration registration) => registration.IsEnabled();

    public static void SetEnabled(IStartupRegistration registration, bool enabled, string commandLine)
    {
        if (enabled)
        {
            registration.Enable(commandLine);
        }
        else
        {
            registration.Disable();
        }
    }

    public static void Toggle(IStartupRegistration registration, string commandLine)
    {
        if (registration.IsEnabled())
        {
            registration.Disable();
        }
        else
        {
            registration.Enable(commandLine);
        }
    }

    /// <summary>
    /// Quote the exe path and append a flag so the app starts into the tray only.
    /// </summary>
    public static string BuildCommandLine(string exePath) =>
        $"\"{exePath}\" --tray-autostart";

    /// <summary>Extracts the executable path from a HKCU Run command line.</summary>
    public static string? ExtractExecutable(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }
        var text = commandLine.Trim();
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }
        var space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }

    /// <summary>
    /// True when the registered command no longer launches this installation:
    /// the path is missing (folder moved/renamed/deleted) or points at a stale sibling
    /// binary in the current folder (in-place upgrade). A different, still-existing
    /// installation elsewhere is left alone — that is a deliberate second copy.
    /// </summary>
    public static bool IsStale(string? commandLine, string currentExePath)
    {
        var registered = ExtractExecutable(commandLine);
        if (registered is null)
        {
            return true;
        }
        try
        {
            if (!File.Exists(registered))
            {
                return true;
            }
        }
        catch
        {
            return true;
        }

        var registeredDir = Path.GetDirectoryName(Path.GetFullPath(registered));
        var currentDir = Path.GetDirectoryName(Path.GetFullPath(currentExePath));
        return registeredDir is not null
               && currentDir is not null
               && string.Equals(
                   registeredDir.TrimEnd(Path.DirectorySeparatorChar),
                   currentDir.TrimEnd(Path.DirectorySeparatorChar),
                   StringComparison.OrdinalIgnoreCase)
               && !string.Equals(
                   Path.GetFullPath(registered),
                   Path.GetFullPath(currentExePath),
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Repairs an autostart entry that points at a path that no longer exists.
    /// Returns true when the registry value was rewritten.
    /// </summary>
    public static bool RepairIfStale(IStartupRegistration registration, string currentExePath)
    {
        if (!registration.IsEnabled())
        {
            return false;
        }
        if (!IsStale(registration.GetCommand(), currentExePath))
        {
            return false;
        }
        registration.Enable(BuildCommandLine(currentExePath));
        return true;
    }
}
