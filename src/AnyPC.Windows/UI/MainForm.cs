using System.Drawing;
using AnyPC.Core;
using QRCoder;

namespace AnyPC.Windows.UI;

/// <summary>Pairing info (QR + PIN), paired devices and settings.</summary>
internal sealed class MainForm : Form
{
    static readonly Color Accent = Color.FromArgb(37, 99, 235);
    static readonly Color Muted = Color.FromArgb(100, 116, 139);

    readonly AnyPCHost _host;
    readonly AppLog _log;
    readonly PictureBox _qr = new() { SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(220, 220), Margin = new Padding(0, 0, 16, 0) };
    readonly Label _status = new() { AutoSize = true, Font = new Font("Segoe UI", 10.5f), Margin = new Padding(0, 0, 0, 12) };
    readonly Label _pin = new() { AutoSize = true, Font = new Font("Consolas", 34f, FontStyle.Bold), ForeColor = Accent };
    readonly Label _pinExpiry = new() { AutoSize = true, ForeColor = Muted };
    readonly Label _address = new() { AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
    readonly Label _fingerprint = new() { AutoSize = true, ForeColor = Muted, Font = new Font("Consolas", 9f) };
    readonly ListView _devices = new() { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Dock = DockStyle.Fill, MultiSelect = false };
    readonly Button _remove = new() { Text = "Remove device", AutoSize = true, Enabled = false };
    readonly CheckBox _autostart = new() { Text = "Start AnyPC when I sign in to Windows", AutoSize = true };
    readonly CheckBox _paused = new() { Text = "Pause remote control", AutoSize = true };
    readonly TextBox _logBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 8.5f), BackColor = SystemColors.Window };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    string _qrPayload = "";
    bool _allowClose;

    public event Action? ExitRequested;

    public MainForm(AnyPCHost host, AppLog log)
    {
        _host = host;
        _log = log;

        Text = "AnyPC";
        Icon = TrayApp.AppIcon;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 620);
        Size = new Size(700, 700);
        BackColor = Color.White;

        BuildLayout();

        _devices.Columns.Add("Device", 220);
        _devices.Columns.Add("Paired", 140);
        _devices.Columns.Add("Last connected", 160);
        _devices.SelectedIndexChanged += (_, _) => _remove.Enabled = _devices.SelectedItems.Count > 0;
        _remove.Click += (_, _) => RemoveSelected();

        _autostart.Checked = Autostart.IsEnabled;
        _autostart.CheckedChanged += (_, _) =>
        {
            try { Autostart.Set(_autostart.Checked); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "AnyPC"); }
        };
        _paused.CheckedChanged += (_, _) =>
        {
            if (_paused.Checked != host.Server.Paused) host.Server.SetPaused(_paused.Checked);
            RefreshState();
        };

        host.Pairing.PinChanged += _ => BeginInvokeSafe(RefreshPairing);
        host.Pairing.Devices.Changed += () => BeginInvokeSafe(RefreshDevices);
        log.Line += line => BeginInvokeSafe(() => _logBox.AppendText(line + Environment.NewLine));
        _logBox.Lines = log.Tail();

        _timer.Tick += (_, _) => RefreshPairing();
        _timer.Start();

        RefreshPairing();
        RefreshDevices();
        RefreshState();
    }

    void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 6 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));

        var title = new Label { Text = "AnyPC", AutoSize = true, Font = new Font("Segoe UI Semibold", 20f), ForeColor = Color.FromArgb(15, 23, 42) };
        root.Controls.Add(title);
        root.Controls.Add(_status);

        // Pairing card
        var pairing = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 12) };
        pairing.Controls.Add(_qr);
        var info = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        info.Controls.Add(new Label { Text = "Pair your iPhone", AutoSize = true, Font = new Font("Segoe UI Semibold", 12f) });
        info.Controls.Add(new Label
        {
            Text = "Open AnyPC on your iPhone and scan this QR code,\nor pick this PC from the list and enter the PIN:",
            AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 4, 0, 8),
        });
        info.Controls.Add(_pin);
        info.Controls.Add(_pinExpiry);
        info.Controls.Add(_address);
        info.Controls.Add(_fingerprint);
        pairing.Controls.Add(info);
        root.Controls.Add(pairing);

        // Devices
        var devicesBox = new GroupBox { Text = "Paired devices", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var devicesLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        devicesLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        devicesLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        devicesLayout.Controls.Add(_devices);
        devicesLayout.Controls.Add(_remove);
        devicesBox.Controls.Add(devicesLayout);
        root.Controls.Add(devicesBox);

        var options = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 8, 0, 8) };
        options.Controls.Add(_autostart);
        options.Controls.Add(_paused);
        var exit = new LinkLabel { Text = "Quit AnyPC", AutoSize = true, Margin = new Padding(24, 3, 0, 0) };
        exit.LinkClicked += (_, _) => ExitRequested?.Invoke();
        options.Controls.Add(exit);
        root.Controls.Add(options);

        var logBox = new GroupBox { Text = "Activity", Dock = DockStyle.Fill, Padding = new Padding(8) };
        logBox.Controls.Add(_logBox);
        root.Controls.Add(logBox);

        Controls.Add(root);
    }

    void BeginInvokeSafe(Action a)
    {
        if (IsDisposed) return;
        if (!IsHandleCreated) { a(); return; }
        try { BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    public void RefreshPairing()
    {
        var pin = _host.Pairing.CurrentPin;
        _pin.Text = $"{pin[..3]} {pin[3..]}";
        var left = _host.Pairing.PinRemaining;
        _pinExpiry.Text = $"New PIN in {Math.Max(0, (int)left.TotalMinutes)}:{Math.Max(0, left.Seconds):00}";

        var ips = AnyPCHost.LocalAddresses();
        _address.Text = ips.Count == 0
            ? "No network connection found — connect this PC to Wi-Fi or Ethernet."
            : $"This PC: {_host.ServerName}   •   {string.Join(", ", ips.Take(2))}   •   port {_host.Server.Port}";
        var fp = _host.Fingerprint;
        _fingerprint.Text = "Security code: " + string.Join(" ", Enumerable.Range(0, 4).Select(i => fp.Substring(i * 4, 4))).ToUpperInvariant();

        var payload = _host.PairingUri();
        if (payload != _qrPayload)
        {
            _qrPayload = payload;
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(10);
            var old = _qr.Image;
            _qr.Image = Image.FromStream(new MemoryStream(png));
            old?.Dispose();
        }
    }

    void RefreshDevices()
    {
        _devices.BeginUpdate();
        _devices.Items.Clear();
        foreach (var d in _host.Pairing.Devices.All.OrderByDescending(d => d.LastSeen))
        {
            var item = new ListViewItem([d.Name, d.PairedAt.LocalDateTime.ToString("g"), d.LastSeen.LocalDateTime.ToString("g")]) { Tag = d.DeviceId };
            _devices.Items.Add(item);
        }
        _devices.EndUpdate();
        _remove.Enabled = _devices.SelectedItems.Count > 0;
    }

    void RemoveSelected()
    {
        if (_devices.SelectedItems.Count == 0) return;
        var item = _devices.SelectedItems[0];
        if (MessageBox.Show(this, $"Remove \"{item.Text}\"? It will need to pair again with a PIN.", "AnyPC", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        var id = (string)item.Tag!;
        _host.Pairing.Devices.Remove(id);
        _host.Server.DisconnectDevice(id);
    }

    public void RefreshState()
    {
        if (InvokeRequired) { BeginInvokeSafe(RefreshState); return; }
        _paused.Checked = _host.Server.Paused;
        var sessions = _host.Server.ActiveSessions;
        if (_host.Server.Paused)
        {
            _status.Text = "⏸  Remote control is paused. Phones cannot connect.";
            _status.ForeColor = Color.FromArgb(180, 83, 9);
        }
        else if (sessions.Count > 0)
        {
            _status.Text = "●  Connected: " + string.Join(", ", sessions.Select(s => $"{s.DeviceName} ({s.Remote})"));
            _status.ForeColor = Color.FromArgb(22, 163, 74);
        }
        else
        {
            _status.Text = "Ready — waiting for your iPhone on the same Wi-Fi network.";
            _status.ForeColor = Muted;
        }
    }

    public void AllowClose() => _allowClose = true;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window keeps AnyPC running in the tray.
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _qr.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
