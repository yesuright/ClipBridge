using System.Runtime.InteropServices;
using System.Text.Json;
using ClipBridge.Core.Config;
using Microsoft.Win32;

namespace ClipBridge.Win.UI;

public enum ThemeChoice { System, Light, Dark }

public sealed class AppearanceStore(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(AppPaths.ConfigDir, "appearance.json");
    public ThemeChoice Load()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            return Enum.TryParse<ThemeChoice>(doc.RootElement.GetProperty("Theme").GetString(), out var v)
                && Enum.IsDefined(v) ? v : ThemeChoice.System;
        }
        catch { return ThemeChoice.System; }
    }
    public void Save(ThemeChoice choice)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { Theme = choice.ToString() }));
        File.Move(temporary, _path, true);
    }
}

public sealed record Palette(Color Background, Color Rail, Color Surface, Color Text,
    Color Muted, Color Border, Color Hover, Color Selected, Color Accent, Color AccentText, Color Good, bool Dark)
{
    private static Color C(string value) => ColorTranslator.FromHtml(value);
    public static Palette Create(bool dark, Color? accent = null)
    {
        var a = accent ?? (dark ? C("#82c6e1") : C("#196e8c"));
        var ink = (a.R * .299 + a.G * .587 + a.B * .114) > 155 ? C("#14212a") : Color.White;
        return dark
            ? new(C("#202428"), C("#1b1f23"), C("#292e33"), C("#edf0f3"), C("#a9b2bb"), C("#3b4249"), C("#353d44"), C("#344651"), a, ink, C("#87c8a5"), true)
            : new(C("#f6f7f9"), C("#edf0f3"), C("#fdfdfd"), C("#202b36"), C("#606e7b"), C("#dde2e7"), C("#e8edf2"), C("#ddebf1"), a, ink, C("#25794f"), false);
    }
}

public sealed class Appearance : IDisposable
{
    private readonly AppearanceStore _store;
    public ThemeChoice Choice { get; private set; }
    public Palette Colors { get; private set; }
    public event Action? Changed;
    public Appearance(AppearanceStore? store = null)
    {
        _store = store ?? new();
        Choice = _store.Load();
        Colors = Resolve();
        SystemEvents.UserPreferenceChanged += OnSystemChange;
    }
    public bool Set(ThemeChoice choice)
    {
        try { _store.Save(choice); }
        catch { return false; }
        Choice = choice;
        Colors = Resolve();
        Changed?.Invoke();
        return true;
    }
    private void OnSystemChange(object sender, UserPreferenceChangedEventArgs e)
    {
        Colors = Resolve();
        Changed?.Invoke();
    }
    private Palette Resolve()
    {
        bool dark = Choice == ThemeChoice.Dark;
        Color? accent = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (Choice == ThemeChoice.System) dark = key?.GetValue("AppsUseLightTheme") is int v && v == 0;
            using var dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (dwm?.GetValue("ColorizationColor") is int color)
            {
                var a = Color.FromArgb(color);
                accent = Color.FromArgb(a.R, a.G, a.B);
            }
        }
        catch { /* usable palette even with registry restrictions */ }
        if (SystemInformation.HighContrast)
            return new(SystemColors.Window, SystemColors.Control, SystemColors.Window, SystemColors.WindowText,
                SystemColors.WindowText, SystemColors.WindowText, SystemColors.Control, SystemColors.Highlight,
                SystemColors.Highlight, SystemColors.HighlightText, SystemColors.WindowText, dark);
        return Palette.Create(dark, accent);
    }
    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnSystemChange;
}

internal static class WindowChrome
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hwnd, string subApp, string? subId);
    public static void Apply(Form window, bool dark)
    {
        if (!window.IsHandleCreated) return;
        int value = dark ? 1 : 0;
        DwmSetWindowAttribute(window.Handle, 20, ref value, sizeof(int));
        value = 2;
        DwmSetWindowAttribute(window.Handle, 33, ref value, sizeof(int));
    }
    public static void Scrollbar(Control control, bool dark)
    {
        if (control.IsHandleCreated) SetWindowTheme(control.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
    }
}
