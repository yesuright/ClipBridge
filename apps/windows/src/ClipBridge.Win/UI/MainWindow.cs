using ClipBridge.Core.Api;
using ClipBridge.Core.Models;
using ClipBridge.Core.Status;

namespace ClipBridge.Win.UI;

public sealed record UiActions(Action ToggleSync, Func<Task> CheckNow, Func<string, bool> Copy,
    Action ToggleStartup, Action RefreshStartup, Action OpenLogs, Action OpenData, Action RestartRelay,
    Action? ShowSetup = null);

/// <summary>Presentation only. The tray ApplicationContext owns this window, never the reverse.</summary>
public sealed class MainWindow : Form
{
    private readonly Appearance _appearance;
    private readonly IRelayClient _historyClient;
    private readonly UiActions _actions;
    private readonly string _localDevice;
    private AppStatusSnapshot _snapshot;
    private readonly Panel _rail = new(), _body = new();
    private readonly Panel[] _pages = [new(), new(), new()];
    private readonly ActionButton[] _nav = new ActionButton[3];
    private readonly Label _brand, _footnote, _heading, _subtitle, _status;
    private readonly Surface _bridgeSurface = new(), _addressSurface = new(), _historySurface = new(), _detailSurface = new();
    private readonly BridgeView _bridge = new();
    private readonly Label _syncTitle, _syncHelp, _address, _addressHelp, _recentTitle, _emptyRecent;
    private readonly Label[] _recentRoute = new Label[2], _recentMeta = new Label[2];
    private readonly ActionButton _pause, _copyAddress, _homeSetup, _historyRefresh, _copyHistory;
    private readonly HistoryList _history = new();
    private readonly HistoryText _detail = new();
    private readonly HistoryScrollBar _detailScroll;
    private readonly Label _historyState, _detailTitle, _detailMeta, _historyNotice;
    private readonly Label _historyEmpty, _detailPlaceholder;
    private readonly Label _startupHelp, _settingsSync, _settingsAddress, _auth, _settingsNotice;
    private readonly ActionButton _startup, _settingsPause, _check, _settingsCopy, _settingsSetup, _retryRelay;
    private readonly ActionButton[] _themes = new ActionButton[3];
    private readonly List<Surface> _settingsSections = [];
    private readonly List<AppStatusSnapshot> _recent = [];
    private readonly ContextMenuStrip _textMenu = new();
    private readonly ToolTip _tooltip = new();
    private CancellationTokenSource? _historyCancellation;
    private bool _loadingHistory, _reloadHistory, _disposing, _ready;
    private int _page;
    private int _historyGeneration;
    private readonly List<(Control Control, float X, float Y, float Width, float Height)> _settingLayout = [];
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool CloseToTray { get; set; } = true;
    public int SelectedPage => _page;

    public MainWindow(AppStatusSnapshot snapshot, Appearance appearance, IRelayClient historyClient,
        UiActions actions, string localDevice, Icon icon)
    {
        _snapshot = snapshot; _appearance = appearance; _historyClient = historyClient;
        _actions = actions; _localDevice = localDevice;
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5f); Text = "ClipBridge"; Icon = icon;
        ClientSize = new Size(704, 604); MinimumSize = new Size(720, 620);
        StartPosition = FormStartPosition.CenterScreen; DoubleBuffered = true;
        Controls.AddRange([_rail, _body]);
        _brand = Label(_rail, "ClipBridge", 12, true);
        _footnote = Label(_rail, "Windows ↔ iPhone\nText, within reach.", 8.5f, false, true);
        for (int i = 0; i < 3; i++)
        {
            int page = i;
            _nav[i] = Button(_rail, new[] { "Home", "History", "Settings" }[i], () => SelectPage(page), new[] { "home", "history", "settings" }[i]);
            _nav[i].Quiet = true;
            _nav[i].AccessibleDescription = $"Show {_nav[i].Text} page";
        }
        _heading = Label(_body, "Home", 23, true);
        _subtitle = Label(_body, "Your text. Across devices.", 10, false, true);
        _status = Label(_body, "Starting…", 9.5f, true);
        for (int i = 0; i < _pages.Length; i++) { _pages[i].AccessibleName = new[] { "Home", "History", "Settings" }[i]; _body.Controls.Add(_pages[i]); }
        _bridgeSurface.AccessibleName = "Device bridge"; _addressSurface.AccessibleName = "iPhone connection";
        _historySurface.AccessibleName = "Recent entries"; _detailSurface.AccessibleName = "Selected entry";

        var home = _pages[0];
        home.Controls.Add(_bridgeSurface); _bridgeSurface.Controls.Add(_bridge);
        _syncTitle = Label(_bridgeSurface, "Sync is active", 10, true);
        _syncHelp = Label(_bridgeSurface, "Ready when you copy.", 9, false, true);
        _pause = Button(_bridgeSurface, "Pause sync", actions.ToggleSync, "pause");
        home.Controls.Add(_addressSurface);
        Label(_addressSurface, "iPhone connection", 9, true).SetBounds(18, 14, 190, 22);
        _address = Label(_addressSurface, "No local network connection", 12, true);
        _addressHelp = Label(_addressSurface, "Use this address in your iPhone Shortcuts.", 9, false, true);
        _copyAddress = Button(_addressSurface, "Copy address", CopyAddress, "copy");
        _homeSetup = Button(_addressSurface, "Connection setup", () => _actions.ShowSetup?.Invoke());
        _homeSetup.AccessibleDescription = "Open the address and token used by the iPhone Shortcuts";
        _recentTitle = Label(home, "Recent activity", 10, true);
        _emptyRecent = Label(home, "Nothing transferred this session.\nCopy on Windows, or send from your iPhone.", 9.5f, false, true);
        for (int i = 0; i < 2; i++)
        {
            _recentRoute[i] = Label(home, "", 9.5f);
            _recentMeta[i] = Label(home, "", 9, false, true);
            _recentMeta[i].TextAlign = ContentAlignment.MiddleRight;
        }

        var historyPage = _pages[1];
        _historyRefresh = Button(historyPage, "Refresh", () => _ = LoadHistoryAsync());
        _historyState = Label(historyPage, "Recent 50 entries", 9.5f, false, true);
        historyPage.Controls.Add(_historySurface); _historySurface.Controls.Add(_history);
        _historyEmpty = Label(_historySurface, "Your words will find a home here.\nCopy something to get started.", 10, false, true);
        _historyEmpty.TextAlign = ContentAlignment.MiddleCenter; _historyEmpty.Visible = false;
        _history.LocalDevice = localDevice;
        _history.SelectedIndexChanged += (_, _) => SelectHistory();
        _detailTitle = Label(historyPage, "Select an entry", 10, true);
        _detailMeta = Label(historyPage, "Its original plain text will appear below.", 9, false, true);
        historyPage.Controls.Add(_detailSurface); _detailSurface.Controls.Add(_detail);
        _detailScroll = new HistoryScrollBar(_detail); _detailSurface.Controls.Add(_detailScroll);
        _detailPlaceholder = Label(_detailSurface, "Choose an entry above to read its text.", 9.5f, false, true);
        _detailPlaceholder.TextAlign = ContentAlignment.MiddleCenter;
        _detail.Visible = false;
        _detailPlaceholder.BringToFront();
        _detail.Font = new Font("Consolas", 10);
        _detail.SafeCopy = CopyText;
        _textMenu.Items.Add("Copy selection", null, (_, _) => { if (_detail.SelectionLength > 0) CopyText(_detail.SelectedText); });
        _textMenu.Items.Add("Select all", null, (_, _) => _detail.SelectAll());
        _textMenu.Opening += (_, _) => _textMenu.Items[0].Enabled = _detail.SelectionLength > 0;
        _detail.ContextMenuStrip = _textMenu;
        _copyHistory = Button(historyPage, "Copy text", () => { if (_history.SelectedItem is HistoryRow row) CopyText(row.Clip.Content); }, "copy");
        _copyHistory.Enabled = false;
        _historyNotice = Label(historyPage, "Previews stay here. Nothing appears on Home.", 9, false, true);

        var settings = _pages[2];
        _settingsNotice = Label(settings, "", 9, false, true);
        Surface Section(float y, float height)
        {
            var panel = new Surface(); settings.Controls.Add(panel); _settingsSections.Add(panel);
            _settingLayout.Add((panel, 0, y, -1, height)); return panel;
        }
        void Set(Control control, float x, float y, float w, float h) => _settingLayout.Add((control, x, y, w, h));
        var general = Section(0, 68);
        Set(Label(general, "Start with Windows", 10, true), 18, 12, 285, 24);
        _startupHelp = Label(general, "Run quietly when you sign in.", 9, false, true); Set(_startupHelp, 18, 37, 310, 22);
        _startup = Button(general, "Off", () => RunSetting(actions.ToggleStartup)); Set(_startup, -100, 16, 82, 36);
        _startup.AccessibleName = "Start ClipBridge when I sign in";
        var appearanceSection = Section(78, 70);
        Set(Label(appearanceSection, "Appearance", 10, true), 18, 12, 170, 24);
        Set(Label(appearanceSection, "Make yourself at home.", 9, false, true), 18, 37, 190, 22);
        for (int i = 0; i < 3; i++)
        {
            var choice = (ThemeChoice)i;
            _themes[i] = Button(appearanceSection, choice.ToString(), () =>
            {
                _settingsNotice.Text = _appearance.Set(choice) ? "" : "Could not save appearance. Please try again.";
            });
            Set(_themes[i], -264 + i * 82, 17, 78, 36);
        }
        var sync = Section(158, 68);
        Set(Label(sync, "Clipboard sync", 10, true), 18, 12, 220, 24);
        _settingsSync = Label(sync, "Active", 9, false, true); Set(_settingsSync, 18, 37, 245, 22);
        _settingsPause = Button(sync, "Pause", actions.ToggleSync); Set(_settingsPause, -220, 16, 90, 36);
        _check = Button(sync, "Check now", async () =>
        {
            _check!.Enabled = false;
            try { await actions.CheckNow(); _settingsNotice.Text = "Check completed. Status is shown above."; }
            catch { _settingsNotice.Text = "Could not check right now. Please try again."; }
            finally { if (!IsDisposed) _check.Enabled = _snapshot.Sync != SyncState.Paused; }
        }); Set(_check, -122, 16, 104, 36);
        var network = Section(236, 100);
        Set(Label(network, "iPhone connection", 10, true), 18, 12, 240, 24);
        _settingsAddress = Label(network, "", 10.5f, true); Set(_settingsAddress, 18, 38, -164, 25);
        Set(Label(network, "Use the current address in both Shortcuts.", 9, false, true), 18, 68, -36, 22);
        _settingsCopy = Button(network, "Copy address", CopyAddress); Set(_settingsCopy, -140, 26, 122, 36);
        var data = Section(346, 84);
        Set(Label(data, "Data & diagnostics", 10, true), 18, 12, 132, 24);
        _settingsSetup = Button(data, "Connection setup", () => _actions.ShowSetup?.Invoke());
        _settingsSetup.AccessibleDescription = "Open the address and token used by the iPhone Shortcuts";
        Set(_settingsSetup, -360, 16, 124, 36);
        _auth = Label(data, "Authentication: Configured", 9, false, true); Set(_auth, 18, 56, 300, 22);
        var logs = Button(data, "Open logs", () => RunSetting(actions.OpenLogs)); Set(logs, -228, 16, 96, 36);
        var folder = Button(data, "Data folder", () => RunSetting(actions.OpenData)); Set(folder, -124, 16, 106, 36);
        _retryRelay = Button(settings, "Restart relay", actions.RestartRelay); _retryRelay.Visible = false;

        _tooltip.SetToolTip(_copyAddress, "Copy the current LAN address without syncing it.");
        _tooltip.SetToolTip(_copyHistory, "Copy the original text without uploading another entry.");
        _tooltip.SetToolTip(_status, "Connection to the relay. iPhone runs on demand via Shortcut.");
        _tooltip.OwnerDraw = true;
        _tooltip.Draw += (_, e) =>
        {
            var p = _appearance.Colors;
            using var fill = new SolidBrush(p.Surface); using var border = new Pen(p.Border);
            e.Graphics.FillRectangle(fill, e.Bounds); e.Graphics.DrawRectangle(border, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, Font, Rectangle.Inflate(e.Bounds, -4, -2), p.Text, TextFormatFlags.VerticalCenter);
        };
        _appearance.Changed += OnAppearanceChanged;
        _ready = true;
        SelectPage(0); Present(snapshot); ApplyTheme();
    }

    private static Label Label(Control parent, string text, float size, bool bold = false, bool muted = false)
    {
        var label = new Label { Text = text, Font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size),
            AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Tag = muted ? "muted" : null,
            TextAlign = ContentAlignment.MiddleLeft };
        parent.Controls.Add(label); return label;
    }
    private static ActionButton Button(Control parent, string text, Action action, string? glyph = null)
    {
        var button = new ActionButton { Text = text, AccessibleName = text, Glyph = glyph };
        button.Click += (_, _) => action(); parent.Controls.Add(button); return button;
    }

    public void OpenAndFocus()
    {
        if (_disposing) return;
        if (!Visible) { SelectPage(0); Show(); }
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate(); BringToFront(); _nav[_page].Focus();
        _actions.RefreshStartup();
    }
    public void SelectPage(int page)
    {
        _page = page;
        for (int i = 0; i < 3; i++) { _pages[i].Visible = i == page; _nav[i].Selected = i == page; _nav[i].Invalidate(); }
        _heading.Text = new[] { "Home", "History", "Settings" }[page];
        _subtitle.Text = new[] { "Your text. Across devices.", "A little continuity for your clipboard.", "Simple by design. Yours to adjust." }[page];
        _historyNotice.Text = "Previews stay here. Nothing appears on Home.";
        _settingsNotice.Text = "";
        if (page == 1 && Visible) _ = LoadHistoryAsync();
        if (page != 1) ClearHistory();
        if (page == 2) _actions.RefreshStartup();
    }

    public void Present(AppStatusSnapshot snapshot)
    {
        if (_disposing) return;
        var old = _snapshot; _snapshot = snapshot;
        var state = StatusPresentation.From(snapshot);
        _status.Text = (state.Connected ? "●  " : "○  ") + state.Title;
        _status.AccessibleName = state.Title;
        _bridge.Connected = state.Connected;
        _bridge.Paused = snapshot.Sync == SyncState.Paused; _bridge.Invalidate();
        bool paused = snapshot.Sync == SyncState.Paused;
        _syncTitle.Text = paused ? "Sync is paused" : state.Connected ? "Sync is active" : state.Title;
        _syncHelp.Text = paused ? "Resume when you’re ready." : state.Connected ? "Ready when you copy." : state.Explanation;
        _pause.Text = paused ? "Resume sync" : "Pause sync"; _pause.AccessibleName = _pause.Text;
        _pause.Glyph = paused ? "play" : "pause"; _pause.Primary = paused; _pause.Invalidate();
        _settingsPause.Text = paused ? "Resume" : "Pause"; _settingsPause.AccessibleName = _settingsPause.Text + " sync";
        _settingsSync.Text = paused ? "Paused" : state.Connected ? "Active" : state.Title;
        _check.Enabled = !paused;
        _startup.Text = snapshot.StartupEnabled ? "On" : "Off";
        _startup.Selected = snapshot.StartupEnabled; _startup.AccessibleDescription = _startup.Text; _startup.Invalidate();
        _address.Text = _settingsAddress.Text = snapshot.LanUrl ?? "No local network connection";
        _copyAddress.Enabled = _settingsCopy.Enabled = snapshot.LanUrl is not null;
        _addressHelp.Text = snapshot.LanUrl is null ? "Connect this PC to your local Wi-Fi or Ethernet." : snapshot.LanIpChanged
            ? "Address changed. Update your iPhone Shortcuts." : "Use this address in your iPhone Shortcuts.";
        _retryRelay.Visible = snapshot.Relay == RelayState.Failed;
        if (snapshot.LastSyncAtUtc is not null && !_recent.Any(r => r.LastSyncAtUtc == snapshot.LastSyncAtUtc))
        {
            _recent.Insert(0, snapshot); if (_recent.Count > 2) _recent.RemoveAt(2);
            if (_page == 1 && Visible) _ = LoadHistoryAsync();
        }
        if (old.Relay != snapshot.Relay && snapshot.Relay == RelayState.Running && _page == 1 && Visible) _ = LoadHistoryAsync();
        _emptyRecent.Visible = _recent.Count == 0;
        for (int i = 0; i < 2; i++)
        {
            _recentRoute[i].Visible = _recentMeta[i].Visible = i < _recent.Count;
            if (i >= _recent.Count) continue;
            var r = _recent[i];
            _recentRoute[i].Text = r.LastSyncDirection == "upload" ? "Windows → Relay" : $"{HistoryPresentation.Source(r.LastSyncDeviceId ?? "Device", _localDevice)} → Windows";
            _recentMeta[i].Text = $"{HistoryPresentation.Size(r.LastSyncBytes ?? 0)}  ·  {HistoryPresentation.RecentTime(r.LastSyncAtUtc!.Value, DateTime.UtcNow)}";
        }
        _status.ForeColor = state.Connected ? _appearance.Colors.Good : _appearance.Colors.Muted;
    }

    private void CopyAddress()
    {
        if (_snapshot.LanUrl is not { } url) return;
        var message = _actions.Copy(url) ? "Address copied. Ready to use in your Shortcuts." : "Clipboard is busy. Please try again.";
        _addressHelp.Text = message; _settingsNotice.Text = message;
    }
    private bool CopyText(string text)
    {
        bool copied = _actions.Copy(text);
        _historyNotice.Text = copied ? "Copied to clipboard. No new sync entry." : "Clipboard is busy. Please try again.";
        return copied;
    }
    private void RunSetting(Action action)
    {
        try { action(); _settingsNotice.Text = ""; }
        catch { _settingsNotice.Text = "Could not complete that action. Please try again."; }
    }

    private async Task LoadHistoryAsync()
    {
        if (_disposing || !Visible || _page != 1) return;
        if (_loadingHistory) { _reloadHistory = true; return; }
        _loadingHistory = true; _historyRefresh.Enabled = false; _historyState.Text = "Loading history…";
        int generation = _historyGeneration;
        var cancellation = new CancellationTokenSource(); _historyCancellation = cancellation;
        try
        {
            var clips = await _historyClient.GetRecentAsync(50, cancellation.Token);
            if (_disposing || generation != _historyGeneration || !Visible || _page != 1) return;
            var selectedId = (_history.SelectedItem as HistoryRow)?.Clip.ClipId;
            _history.BeginUpdate();
            try
            {
                _history.Items.Clear();
                foreach (var clip in clips.Take(50)) _history.Items.Add(new HistoryRow(clip, _localDevice));
                var index = _history.Items.Cast<HistoryRow>().ToList().FindIndex(r => r.Clip.ClipId == selectedId);
                _history.SelectedIndex = index;
            }
            finally { _history.EndUpdate(); }
            _historyState.Text = clips.Count == 0 ? "Your clipboard history starts with the next copy." : $"{_history.Items.Count} recent entries  ·  Select to read";
            _history.Visible = clips.Count > 0;
            _historyEmpty.Visible = clips.Count == 0;
            _historyEmpty.Text = "Your words will find a home here.\nCopy something to get started.";
            _detailTitle.Text = _history.SelectedIndex < 0 ? clips.Count == 0 ? "A fresh start" : "Select an entry" : _detailTitle.Text;
            _detailMeta.Text = _history.SelectedIndex < 0 ? "Only the selected entry shows its full text." : _detailMeta.Text;
            _historyRefresh.Text = "Refresh";
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!_disposing && generation == _historyGeneration)
            {
                _history.Items.Clear(); _detail.Clear(); _copyHistory.Enabled = false;
                _history.Visible = false; _historyEmpty.Visible = true;
                _historyEmpty.Text = "We couldn’t load your history.\nUse Retry to try again.";
                _historyState.Text = "History is unavailable. Try again in a moment.";
                _historyRefresh.Text = "Retry";
                _detailTitle.Text = "Unable to load history";
                _detailMeta.Text = "Your saved entries remain on the relay.";
            }
        }
        finally
        {
            cancellation.Dispose();
            if (_historyCancellation == cancellation) _historyCancellation = null;
            _loadingHistory = false;
            if (!_disposing)
            {
                _historyRefresh.Enabled = true;
                if (_reloadHistory) { _reloadHistory = false; _ = LoadHistoryAsync(); }
            }
        }
    }
    private void SelectHistory()
    {
        _detail.Clear();
        _copyHistory.Enabled = _history.SelectedItem is HistoryRow;
        _detailPlaceholder.Visible = _history.SelectedItem is not HistoryRow;
        _detail.Visible = _history.SelectedItem is HistoryRow;
        if (_history.SelectedItem is not HistoryRow row) return;
        _detailTitle.Text = HistoryPresentation.Route(row.Clip, _localDevice);
        _detailMeta.Text = $"{HistoryPresentation.Time(row.Clip.ReceivedAt)}   ·   {HistoryPresentation.Size(row.Clip.SizeBytes)}";
        _detail.Text = row.Clip.Content;
        _detail.Select(0, 0);
    }
    private void ClearHistory()
    {
        _historyGeneration++; _historyCancellation?.Cancel(); _reloadHistory = false;
        _history.Items.Clear(); _detail.Clear(); _copyHistory.Enabled = false;
    }

    private void OnAppearanceChanged()
    {
        if (_disposing) return;
        if (InvokeRequired) { if (IsHandleCreated) BeginInvoke(ApplyTheme); }
        else ApplyTheme();
    }
    private void ApplyTheme()
    {
        var p = _appearance.Colors;
        BackColor = p.Background; _rail.BackColor = p.Rail; _body.BackColor = p.Background;
        void Visit(Control parent, Color background)
        {
            foreach (Control c in parent.Controls)
            {
                c.BackColor = background;
                c.ForeColor = Equals(c.Tag, "muted") ? p.Muted : p.Text;
                if (c is Surface surface) { surface.Colors = p; Visit(surface, p.Surface); }
                else { if (c is ActionButton button) button.Colors = p; Visit(c, background); }
                c.Invalidate();
            }
        }
        Visit(_rail, p.Rail); Visit(_body, p.Background);
        _bridge.Colors = p; _history.Colors = p; _history.BackColor = p.Surface;
        _detail.BackColor = p.Surface; _detail.ForeColor = p.Text;
        _detailScroll.Colors = p; _detailScroll.BackColor = p.Surface; _detailScroll.Invalidate();
        _textMenu.Renderer = new TrayRenderer(p);
        for (int i = 0; i < 3; i++) { _themes[i].Selected = (int)_appearance.Choice == i; _themes[i].Invalidate(); }
        WindowChrome.Apply(this, p.Dark); WindowChrome.Scrollbar(_history, p.Dark); WindowChrome.Scrollbar(_detail, p.Dark);
        _tooltip.BackColor = p.Surface; _tooltip.ForeColor = p.Text;
        Present(_snapshot);
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); if (_ready) ApplyTheme(); }
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Establish the initial dimensions after WinForms has adopted the monitor DPI.
        // Setting these before handle creation can shrink a programmatic form at 150%.
        float scale = DeviceDpi / 96f;
        MinimumSize = new Size((int)(720 * scale), (int)(620 * scale));
        ClientSize = new Size((int)(704 * scale), (int)(604 * scale));
        CenterToScreen(); PerformLayout();
    }
    protected override void OnActivated(EventArgs e) { base.OnActivated(e); if (_ready) { _actions.RefreshStartup(); Present(_snapshot); } }
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!_ready) return;
        if (!Visible) { ClearHistory(); if (!_disposing) SelectPage(0); }
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (CloseToTray && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Hide(); return true; }
        if (keyData is (Keys.Control | Keys.D1) or (Keys.Control | Keys.D2) or (Keys.Control | Keys.D3))
        { SelectPage((int)(keyData & Keys.KeyCode) - (int)Keys.D1); _nav[_page].Focus(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e); if (!_ready) return;
        float d = DeviceDpi / 96f, w = ClientSize.Width / d, h = ClientSize.Height / d;
        void Put(Control c, float x, float y, float width, float height) => c.SetBounds((int)Math.Round(x * d), (int)Math.Round(y * d), (int)Math.Round(width * d), (int)Math.Round(height * d));
        Put(_rail, 0, 0, 136, h); Put(_body, 160, 0, w - 184, h);
        Put(_brand, 18, 26, 118, 32); Put(_footnote, 18, h - 94, 112, 64);
        for (int i = 0; i < 3; i++) Put(_nav[i], 10, 100 + i * 48, 116, 40);
        float bw = w - 184, ph = h - 102;
        Put(_heading, 0, 23, bw - 185, 40); Put(_subtitle, 0, 65, bw, 25);
        Put(_status, bw - 196, 30, 196, 30); _status.TextAlign = ContentAlignment.MiddleRight;
        foreach (var page in _pages) Put(page, 0, 102, bw, ph);
        Put(_bridgeSurface, 0, 0, bw, 200); Put(_bridge, 1, 1, bw - 2, 135);
        Put(_syncTitle, 18, 145, bw - 184, 24); Put(_syncHelp, 18, 169, bw - 184, 22);
        Put(_pause, bw - 158, 149, 140, 38);
        Put(_addressSurface, 0, 214, bw, 108);
        Put(_addressSurface.Controls[0], 18, 13, 250, 23);
        Put(_address, 18, 39, bw - 178, 28); Put(_addressHelp, 18, 77, bw - 178, 22);
        Put(_copyAddress, bw - 158, 14, 140, 38); Put(_homeSetup, bw - 158, 58, 140, 38);
        Put(_recentTitle, 0, 348, bw, 26); Put(_emptyRecent, 0, 384, bw, 57);
        for (int i = 0; i < 2; i++)
        { Put(_recentRoute[i], 0, 384 + 39 * i, bw - 190, 30); Put(_recentMeta[i], bw - 205, 384 + 39 * i, 205, 30); }
        Put(_historyState, 0, 0, bw - 100, 36); Put(_historyRefresh, bw - 92, 0, 92, 34);
        Put(_historySurface, 0, 47, bw, 154); Put(_history, 4, 4, bw - 8, 146);
        Put(_historyEmpty, 18, 24, bw - 36, 106);
        Put(_detailTitle, 0, 215, bw, 26); Put(_detailMeta, 0, 242, bw, 23);
        Put(_detailSurface, 0, 277, bw, Math.Max(100, ph - 339));
        Put(_detail, 14, 12, bw - 46, Math.Max(76, ph - 363));
        Put(_detailScroll, bw - 24, 12, 16, Math.Max(76, ph - 363));
        Put(_detailPlaceholder, 14, 12, bw - 28, Math.Max(76, ph - 363));
        Put(_copyHistory, bw - 125, ph - 48, 125, 36); Put(_historyNotice, 0, ph - 48, bw - 139, 42);
        foreach (var item in _settingLayout)
        {
            float pw = item.Control.Parent!.ClientSize.Width / d;
            float x = item.X < 0 ? pw + item.X : item.X;
            float width = item.Width < 0 ? pw + item.Width : item.Width;
            Put(item.Control, x, item.Y, width, item.Height);
        }
        Put(_settingsNotice, 0, 440, bw - (_retryRelay.Visible ? 150 : 0), 38);
        Put(_retryRelay, bw - 142, 440, 142, 36);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposing)
        {
            _disposing = true; _appearance.Changed -= OnAppearanceChanged;
            ClearHistory(); _textMenu.Dispose(); _tooltip.Dispose();
        }
        base.Dispose(disposing);
    }
}
