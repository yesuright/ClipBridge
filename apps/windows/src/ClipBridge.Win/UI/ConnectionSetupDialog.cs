namespace ClipBridge.Win.UI;

/// <summary>
/// The one screen a new user actually needs: the LAN address and the access token that
/// go into the two iPhone Shortcuts. Values are read straight from the running config —
/// nothing is regenerated here, so opening the dialog can never invalidate a Shortcut.
/// </summary>
public sealed class ConnectionSetupDialog : Form
{
    private readonly Palette _colors;
    private readonly TextBox _address;
    private readonly TextBox _token;
    private readonly Label _notice;
    private readonly Func<string, bool> _copy;
    private readonly bool _hasLan;
    private bool _revealed;

    public ConnectionSetupDialog(
        Palette colors,
        string? lanUrl,
        string token,
        string deviceId,
        string configPath,
        Func<string, bool> copy)
    {
        _colors = colors;
        _copy = copy;
        _hasLan = lanUrl is not null;

        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5f);
        Text = "ClipBridge connection";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = colors.Background;
        ForeColor = colors.Text;
        // Every fixed pixel below is expressed at 96 DPI and scaled by the monitor DPI;
        // WinForms scales the fonts of this programmatic form but not its explicit
        // sizes, and unsized pixels left at 96 DPI clipped the fields at 125/150%.
        float d = DeviceDpi / 96f;
        ClientSize = new Size((int)(560 * d), (int)(416 * d));

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding((int)(22 * d), (int)(18 * d), (int)(22 * d), (int)(16 * d)),
            BackColor = colors.Background,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(Heading("Connect your iPhone", 15.5f, bold: true, height: 30));
        root.Controls.Add(Heading(
            "These two values go into both Shortcuts. Keep them on this PC — the token is the only key to your relay.",
            9.5f, bold: false, height: 40));

        root.Controls.Add(FieldLabel("1 · Address (in both Shortcuts)"));
        _address = ValueBox(lanUrl ?? "No local network address right now", lanUrl is null);
        root.Controls.Add(WithButton(_address, "Copy", () => CopyAddress()));

        root.Controls.Add(FieldLabel("2 · Access token (same in both Shortcuts)"));
        _token = ValueBox(token, readOnly: true);
        _token.UseSystemPasswordChar = true;
        root.Controls.Add(WithButton(_token, "Show", (Action)ToggleReveal, "Copy", () => CopyValue(_token.Text, "Token copied.")));

        root.Controls.Add(Heading(
            $"Device name: {deviceId}.  Same Wi-Fi required. Never forward this port on your router.",
            9f, bold: false, height: 34, muted: true));
        root.Controls.Add(Heading($"Settings file: {configPath}", 8.5f, bold: false, height: 30, muted: true));

        _notice = Heading("", 9f, bold: false, height: 24);
        root.Controls.Add(_notice);

        var close = new ActionButton
        {
            Text = "Done",
            Primary = true,
            Colors = colors,
            DialogResult = DialogResult.OK,
            Anchor = AnchorStyles.Right,
            Size = new Size((int)(110 * d), (int)(36 * d)),
        };
        var footer = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            BackColor = colors.Background,
        };
        footer.Controls.Add(close);
        root.Controls.Add(footer);

        AcceptButton = close;
        CancelButton = close;
        ApplyTheme();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Same failure mode the main window already guards against: a programmatic form
        // sized before handle creation can end up at its 96-DPI size on a scaled
        // monitor. Re-assert the intended client size once the real monitor DPI is
        // known; the constructor already scales its fixed pixels the same way.
        float scale = DeviceDpi / 96f;
        ClientSize = new Size((int)(560 * scale), (int)(416 * scale));
    }

    private void ToggleReveal()
    {
        _revealed = !_revealed;
        _token.UseSystemPasswordChar = !_revealed;
    }

    private void CopyAddress()
    {
        if (!_hasLan)
        {
            _notice.ForeColor = _colors.Muted;
            _notice.Text = "Connect this PC to Wi-Fi or Ethernet first.";
            return;
        }
        CopyValue(_address.Text, "Address copied.");
    }

    private void CopyValue(string value, string message)
    {
        var copied = _copy(value);
        _notice.ForeColor = copied ? _colors.Good : _colors.Muted;
        _notice.Text = copied ? message : "Clipboard is busy. Please try again.";
    }

    private Label Heading(string text, float size, bool bold, int height, bool muted = false) => new()
    {
        Text = text,
        Font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size),
        ForeColor = muted ? _colors.Muted : _colors.Text,
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        UseMnemonic = false,
        Margin = new Padding(0, 0, 0, (int)(4 * DeviceDpi / 96f)),
        Height = (int)(height * DeviceDpi / 96f),
    };

    private Label FieldLabel(string text) => new()
    {
        Text = text,
        Font = new Font("Segoe UI Semibold", 9.5f),
        ForeColor = _colors.Text,
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.BottomLeft,
        UseMnemonic = false,
        Height = (int)(26 * DeviceDpi / 96f),
        Margin = new Padding(0, (int)(8 * DeviceDpi / 96f), 0, (int)(3 * DeviceDpi / 96f)),
    };

    private TextBox ValueBox(string text, bool readOnly) => new()
    {
        Text = text,
        ReadOnly = readOnly,
        Dock = DockStyle.Fill,
        Font = new Font("Consolas", 10f),
        BackColor = _colors.Surface,
        ForeColor = _colors.Text,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(0, 0, 0, 2),
    };

    private Control WithButton(TextBox box, params object[] buttons)
    {
        float d = DeviceDpi / 96f;
        var host = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = buttons.Length / 2 + 1,
            Height = (int)(34 * d),
            BackColor = _colors.Background,
            Margin = new Padding(0, 0, 0, (int)(6 * d)),
        };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.Controls.Add(box, 0, 0);
        for (var i = 0; i < buttons.Length; i += 2)
        {
            var button = new ActionButton
            {
                Text = (string)buttons[i],
                Colors = _colors,
                Dock = DockStyle.Fill,
                Margin = new Padding((int)(8 * d), 0, 0, 0),
                Width = (int)(76 * d),
            };
            var action = (Action)buttons[i + 1];
            button.Click += (_, _) => action();
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, (int)(84 * d)));
            host.Controls.Add(button, i / 2 + 1, 0);
        }
        return host;
    }

    private void ApplyTheme()
    {
        BackColor = _colors.Background;
        ForeColor = _colors.Text;
        foreach (Control control in Controls)
        {
            Paint(control);
        }
        WindowChrome.Apply(this, _colors.Dark);

        void Paint(Control parent)
        {
            parent.BackColor = _colors.Background;
            foreach (Control child in parent.Controls)
            {
                if (child is TextBox box)
                {
                    box.BackColor = _colors.Surface;
                    box.ForeColor = _colors.Text;
                }
                else if (child is ActionButton button)
                {
                    button.Colors = _colors;
                }
                else if (child is Label label && label.ForeColor != _colors.Good)
                {
                    label.ForeColor = label.Font.Bold ? _colors.Text : _colors.Muted;
                }
                Paint(child);
            }
        }
    }
}
