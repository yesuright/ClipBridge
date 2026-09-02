using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ClipBridge.Core.Startup;

/// <summary>
/// User-level "Start with Windows" registration (Milestone 3). HKCU Run is the chosen
/// mechanism: no admin rights, per-user, reversible, trivially detectable, installer-friendly.
/// Default = OFF; it is only ever enabled explicitly by the user (tray menu / CLI).
/// </summary>
public interface IStartupRegistration
{
    bool IsEnabled();
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
/// Pure enable/disable/toggle logic on top of an <see cref="IStartupRegistration"/> store.
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
}
