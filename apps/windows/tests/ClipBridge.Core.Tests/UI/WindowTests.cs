using ClipBridge.Core.Api;
using ClipBridge.Core.Models;
using ClipBridge.Win.UI;
using Xunit;
using Appearance = ClipBridge.Win.UI.Appearance;

namespace ClipBridge.Core.Tests.UI;

[Collection("Native UI")]
public class WindowTests
{
    private static void Sta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(15000), "UI test did not complete");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static UiActions Actions => new(() => { }, () => Task.CompletedTask, _ => true, () => { }, () => { }, () => { }, () => { }, () => { });

    [Theory] [InlineData(Keys.Control | Keys.C)] [InlineData(Keys.Control | Keys.Insert)]
    public void RichEditKeyboardCopyUsesHostAdapter(Keys key) => Sta(() =>
    {
        using var reader = new HistoryText { Text = "original selection" };
        reader.SelectAll(); string? copied = null;
        reader.SafeCopy = text => { copied = text; return true; };
        var method = typeof(HistoryText).GetMethod("ProcessCmdKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var result = method.Invoke(reader, [Message.Create(reader.Handle, 0x100, IntPtr.Zero, IntPtr.Zero), key]);
        Assert.Equal(true, result); Assert.Equal("original selection", copied);
    });

    [Fact]
    public void ClosingHidesAndReopeningReusesWindowAndClearsPrivateText() => Sta(() =>
    {
        using var appearance = new Appearance(new AppearanceStore(Path.Combine(Path.GetTempPath(), "nonexistent-cb-ui-test.json")));
        using var window = new MainWindow(PresentationTests.Healthy, appearance, new HistoryStub(), Actions, "windows-main", SystemIcons.Application);
        window.OpenAndFocus(); Assert.True(window.Visible);
        var handle = window.Handle;
        window.SelectPage(1);
        var list = Descendants(window).OfType<HistoryList>().Single(); Assert.Single(list.Items.Cast<object>());
        list.SelectedIndex = 0;
        var detail = Descendants(window).OfType<HistoryText>().Single(); Assert.Contains("private", detail.Text);
        window.Close(); Assert.False(window.IsDisposed); Assert.False(window.Visible); Assert.Empty(detail.Text); Assert.Empty(list.Items);
        window.OpenAndFocus(); Assert.True(window.Visible); Assert.Equal(handle, window.Handle); Assert.Equal(0, window.SelectedPage);
    });

    [Fact]
    public void HomeNeverContainsClipboardTextAndHistoryDoesNotAutoSelect() => Sta(() =>
    {
        using var appearance = new Appearance(new AppearanceStore(Path.Combine(Path.GetTempPath(), "nonexistent-cb-ui-test.json")));
        using var window = new MainWindow(PresentationTests.Healthy, appearance, new HistoryStub(), Actions, "windows-main", SystemIcons.Application);
        window.OpenAndFocus(); Assert.DoesNotContain(Descendants(window), c => c.Text.Contains("private"));
        window.SelectPage(1); Assert.Equal(-1, Descendants(window).OfType<HistoryList>().Single().SelectedIndex);
        Assert.Empty(Descendants(window).OfType<HistoryText>().Single().Text);
    });

    [Theory] [InlineData(false)] [InlineData(true)]
    public void EmptyAndErrorHistoryHaveUsefulStates(bool fail) => Sta(() =>
    {
        using var appearance = new Appearance(new AppearanceStore(Path.Combine(Path.GetTempPath(), "nonexistent-cb-ui-test.json")));
        using var window = new MainWindow(PresentationTests.Healthy, appearance, new HistoryStub { Empty = !fail, Fail = fail }, Actions, "windows-main", SystemIcons.Application);
        window.OpenAndFocus(); window.SelectPage(1);
        var controls = Descendants(window).ToList();
        Assert.Contains(controls, c => c.Text.Contains(fail ? "History is unavailable" : "history starts"));
        Assert.DoesNotContain(controls, c => c.Text.Contains("internal failure"));
    });

    [Theory] [InlineData(96)] [InlineData(120)] [InlineData(144)] [InlineData(192)]
    public void CommonDpiTextAndTargetMetricsFit(int dpi) => Sta(() =>
    {
        // Measure the real fonts at each target DPI without changing the user's monitor settings.
        using var bitmap = new Bitmap(1600, 1200); bitmap.SetResolution(dpi, dpi);
        using var graphics = Graphics.FromImage(bitmap);
        using var font = new Font("Segoe UI Semibold", 12);
        var measured = graphics.MeasureString("http://192.168.255.255:8787", font);
        Assert.True(measured.Width <= 342 * dpi / 96f, $"LAN URL clipped at {dpi} DPI");
        using var buttonFont = new Font("Segoe UI", 9.5f);
        Assert.True(graphics.MeasureString("Resume sync", buttonFont).Width <= 92 * dpi / 96f);
        Assert.True(graphics.MeasureString("Relay restarting…", buttonFont).Width <= 180 * dpi / 96f);
        Assert.True(graphics.MeasureString("Settings", buttonFont).Width <= 68 * dpi / 96f);
        Assert.True(graphics.MeasureString("Copy address", buttonFont).Width <= 92 * dpi / 96f);
        Assert.True(graphics.MeasureString("Windows → Relay · 2.00 MB", buttonFont).Width <= 320 * dpi / 96f);
        Assert.True(graphics.MeasureString("Sep 30 · 23:59", buttonFont).Width <= 146 * dpi / 96f);
    });

    [Fact]
    public void MinimumWindowKeepsEveryPageInsideBounds() => Sta(() =>
    {
        using var appearance = new Appearance(new AppearanceStore(Path.Combine(Path.GetTempPath(), "nonexistent-cb-ui-test.json")));
        using var window = new MainWindow(PresentationTests.Healthy, appearance, new HistoryStub(), Actions, "windows-main", SystemIcons.Application);
        window.OpenAndFocus(); window.Size = window.MinimumSize;
        for (int i = 0; i < 3; i++)
        {
            window.SelectPage(i); window.PerformLayout();
            foreach (var control in Descendants(window).Where(c => c.Visible))
            {
                var parent = control.Parent!;
                Assert.True(control.Left >= 0 && control.Top >= 0 && control.Right <= parent.ClientSize.Width + 1 && control.Bottom <= parent.ClientSize.Height + 1,
                    $"{control.GetType().Name} {control.Text}: {control.Bounds} outside {parent.ClientSize}");
            }
        }
    });

    internal static IEnumerable<Control> Descendants(Control control)
    {
        foreach (Control child in control.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private sealed class HistoryStub : IRelayClient
    {
        public bool Empty, Fail;
        public Task<IReadOnlyList<ClipRecord>> GetRecentAsync(int limit, CancellationToken ct)
        {
            if (Fail) throw new IOException("internal failure, private content");
            return Task.FromResult<IReadOnlyList<ClipRecord>>(Empty ? [] : [PresentationTests.Clip("private\r\ntext\t中文😀")]);
        }
        public Task<ClipRecord?> GetLatestAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ClipRecord> PostClipAsync(ClipRecord clip, CancellationToken ct) => throw new NotSupportedException();
    }
}

// Native windows share a desktop and should not contend with lifecycle timing tests.
[CollectionDefinition("Native UI", DisableParallelization = true)]
public class NativeUiCollection { }
