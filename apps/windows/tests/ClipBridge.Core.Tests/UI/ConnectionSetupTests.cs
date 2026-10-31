using ClipBridge.Core.Api;
using ClipBridge.Core.Models;
using ClipBridge.Core.Status;
using ClipBridge.Win.UI;
using Xunit;
using Appearance = ClipBridge.Win.UI.Appearance;

namespace ClipBridge.Core.Tests.UI;

/// <summary>
/// Regression tests for the connection-setup entry points. The old UI hid the action
/// inside a truncated gray "Authentication: Configured · Connection setup" label, so
/// users could neither see nor reliably click it. These tests pin the replacement:
/// real buttons on Home and Settings, one shared ShowSetup callback, no token text in
/// the main window, and a read-only dialog that keeps the token masked by default.
/// </summary>
[Collection("Native UI")]
public class ConnectionSetupTests
{
    private static void Sta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(15000), "UI test did not complete");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static MainWindow CreateWindow(AppStatusSnapshot snapshot, Appearance appearance, Action showSetup)
    {
        // Nine positional slots: ToggleSync, CheckNow, Copy, ToggleStartup, RefreshStartup,
        // OpenLogs, OpenData, RestartRelay, ShowSetup. Keep ShowSetup ninth.
        UiActions actions = new(() => { }, () => Task.CompletedTask, _ => true, () => { }, () => { },
            () => { }, () => { }, () => { }, showSetup);
        return new MainWindow(snapshot, appearance, new HistoryStub(), actions, "windows-main", SystemIcons.Application);
    }

    [Fact]
    public void HomeAndSettingsExposeRealConnectionSetupButtons() => Sta(() =>
    {
        using var appearance = new Appearance(new AppearanceStore(Path.Combine(Path.GetTempPath(), "nonexistent-cb-ui-test.json")));
        using var window = CreateWindow(PresentationTests.Healthy, appearance, () => { });
        window.OpenAndFocus();
        var buttons = WindowTests.Descendants(window).OfType<ActionButton>().Where(b => b.Text == "Connection setup").ToList();
        Assert.Equal(2, buttons.Count);
        // One per page, and each lives on its own card (never the same control twice).
        Assert.Equal(2, buttons.Select(b => b.Parent).Distinct().Count());
        window.SelectPage(0);
        var visibleOnHome = buttons.Where(b => b.Visible).ToList();
        Assert.Single(visibleOnHome);
        Assert.IsType<ActionButton>(visibleOnHome[0]);
        window.SelectPage(2);
        var visibleOnSettings = buttons.Where(b => b.Visible).ToList();
        Assert.Single(visibleOnSettings);
        Assert.IsType<ActionButton>(visibleOnSettings[0]);
    });

    [Fact]
    public void BothButtonsTriggerTheSharedShowSetupCallback() => Sta(() =>
    {
        int calls = 0;
        using var appearance = new Appearance(new AppearanceStore(Path.Combine(Path.GetTempPath(), "nonexistent-cb-ui-test.json")));
        using var window = CreateWindow(PresentationTests.Healthy, appearance, () => calls++);
        window.OpenAndFocus();
        var buttons = WindowTests.Descendants(window).OfType<ActionButton>().Where(b => b.Text == "Connection setup").ToList();
        Assert.Equal(2, buttons.Count);
        window.SelectPage(0);
        buttons.Single(b => b.Visible).PerformClick();
        Assert.Equal(1, calls);
        window.SelectPage(2);
        buttons.Single(b => b.Visible).PerformClick();
        Assert.Equal(2, calls);
    });

    [Fact]
    public void SetupEntryStaysAvailableWithoutNetwork() => Sta(() =>
    {
        int calls = 0;
        using var appearance = new Appearance(new AppearanceStore(Path.Combine(Path.GetTempPath(), "nonexistent-cb-ui-test.json")));
        var offline = PresentationTests.Healthy with { LanUrl = null, Network = NetworkState.NoLan };
        using var window = CreateWindow(offline, appearance, () => calls++);
        window.OpenAndFocus();
        var buttons = WindowTests.Descendants(window).OfType<ActionButton>().Where(b => b.Text == "Connection setup").ToList();
        Assert.Equal(2, buttons.Count);
        Assert.All(buttons, b => Assert.True(b.Enabled, "setup entry must work without a LAN address"));
        window.SelectPage(0);
        buttons.Single(b => b.Visible).PerformClick();
        window.SelectPage(2);
        buttons.Single(b => b.Visible).PerformClick();
        Assert.Equal(2, calls);
        // The status line stays plain: no hand cursor pretending to be a link.
        var auth = WindowTests.Descendants(window).Single(c => c is Label && c.Text == "Authentication: Configured");
        Assert.NotEqual(System.Windows.Forms.Cursors.Hand, auth.Cursor);
    });

    [Fact]
    public void MainWindowNeverHostsMaskedTokenText() => Sta(() =>
    {
        using var appearance = new Appearance(new AppearanceStore(Path.Combine(Path.GetTempPath(), "nonexistent-cb-ui-test.json")));
        using var window = CreateWindow(PresentationTests.Healthy, appearance, () => { });
        window.OpenAndFocus();
        for (int page = 0; page < 3; page++)
        {
            window.SelectPage(page);
            // Masked (password-style) text belongs to the setup dialog only; the main
            // window must never carry the device token on any page.
            Assert.DoesNotContain(WindowTests.Descendants(window), c => c is System.Windows.Forms.TextBox box && box.UseSystemPasswordChar);
        }
    });

    [Fact]
    public void SetupDialogMasksTokenByDefaultAndNeverRewritesConfig() => Sta(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-setup-dialog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");
        File.WriteAllText(configPath, "{ \"DeviceToken\": \"dialog-test-token\" }");
        var before = File.ReadAllBytes(configPath);
        try
        {
            using var appearance = new Appearance(new AppearanceStore(Path.Combine(Path.GetTempPath(), "nonexistent-cb-ui-test.json")));
            using var dialog = new ConnectionSetupDialog(appearance.Colors, "http://192.168.1.14:8787",
                "dialog-test-token", "windows-main", configPath, _ => true);
            // .NET 10 PerformClick() is a silent no-op on controls without live handles,
            // so materialize the dialog the way a user would see it before clicking.
            dialog.Show();
            Application.DoEvents();
            var boxes = WindowTests.Descendants(dialog).OfType<System.Windows.Forms.TextBox>().ToList();
            var token = boxes.Single(b => b.Text == "dialog-test-token");
            Assert.True(token.ReadOnly);
            Assert.True(token.UseSystemPasswordChar, "token must be masked when the dialog opens");
            var show = WindowTests.Descendants(dialog).OfType<ActionButton>().Single(b => b.Text == "Show");
            show.PerformClick();
            Assert.False(token.UseSystemPasswordChar);
            show.PerformClick();
            Assert.True(token.UseSystemPasswordChar);
            Assert.DoesNotContain(WindowTests.Descendants(dialog), c => c.Text is "Save" or "Apply");
            Assert.Equal(before, File.ReadAllBytes(configPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    });

    [Fact]
    public void TouchedCardsKeepControlsInsideAndClearOfEachOtherAtMinimumSize() => Sta(() =>
    {
        using var appearance = new Appearance(new AppearanceStore(Path.Combine(Path.GetTempPath(), "nonexistent-cb-ui-test.json")));
        using var window = CreateWindow(PresentationTests.Healthy, appearance, () => { });
        window.OpenAndFocus();
        window.Size = window.MinimumSize;
        window.PerformLayout();
        window.SelectPage(0);
        var addressCard = WindowTests.Descendants(window).Single(c => c is Surface && c.AccessibleName == "iPhone connection");
        AssertNoVisibleOverlap(addressCard);
        window.SelectPage(2);
        var dataCard = WindowTests.Descendants(window).Single(c => c is Surface && WindowTests.Descendants(c).Any(b => b.Text == "Open logs"));
        AssertNoVisibleOverlap(dataCard);
        // Every control stays inside its card even at the smallest window size.
        foreach (var card in new[] { addressCard, dataCard })
            foreach (var child in card.Controls.Cast<Control>().Where(c => c.Visible))
                Assert.True(child.Left >= 0 && child.Top >= 0
                    && child.Right <= card.ClientSize.Width + 1 && child.Bottom <= card.ClientSize.Height + 1,
                    $"{child.Text} escaped its card: {child.Bounds} vs {card.ClientSize}");
    });

    private static void AssertNoVisibleOverlap(Control card)
    {
        var visible = card.Controls.Cast<Control>().Where(c => c.Visible).ToList();
        for (int i = 0; i < visible.Count; i++)
            for (int j = i + 1; j < visible.Count; j++)
                Assert.False(visible[i].Bounds.IntersectsWith(visible[j].Bounds),
                    $"{visible[i].Text} overlaps {visible[j].Text}");
    }

    private sealed class HistoryStub : IRelayClient
    {
        public Task<IReadOnlyList<ClipRecord>> GetRecentAsync(int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<ClipRecord>>([]);
        public Task<ClipRecord?> GetLatestAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ClipRecord> PostClipAsync(ClipRecord clip, CancellationToken ct) => throw new NotSupportedException();
    }
}
