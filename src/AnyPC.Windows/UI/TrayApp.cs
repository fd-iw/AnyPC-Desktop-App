using System.Drawing;
using AnyPC.Core;

namespace AnyPC.Windows.UI;

/// <summary>Notification-area icon that owns the main window.</summary>
internal sealed class TrayApp : ApplicationContext
{
    readonly AnyPCHost _host;
    readonly NotifyIcon _icon;
    readonly MainForm _form;
    readonly ToolStripMenuItem _pauseItem;
    readonly SynchronizationContext _ui;
    int _lastSessionCount;

    public static Icon AppIcon { get; } = LoadIcon();

    static Icon LoadIcon()
    {
        using var s = typeof(TrayApp).Assembly.GetManifestResourceStream("anypc.ico");
        return s is null ? SystemIcons.Application : new Icon(s);
    }

    public TrayApp(AnyPCHost host, AppLog log, bool startMinimized)
    {
        _host = host;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _form = new MainForm(host, log);
        _form.ExitRequested += ExitApp;

        _pauseItem = new ToolStripMenuItem("Pause remote control", null, (_, _) => TogglePause());
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open AnyPC", null, (_, _) => ShowForm()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));

        _icon = new NotifyIcon
        {
            Icon = AppIcon,
            Text = "AnyPC — waiting for iPhone",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowForm(); };

        host.Server.SessionsChanged += () => _ui.Post(_ => OnSessionsChanged(), null);
        Autostart.Refresh();

        if (!startMinimized) ShowForm();
    }

    void ShowForm()
    {
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    void TogglePause()
    {
        _host.Server.SetPaused(!_host.Server.Paused);
        _pauseItem.Text = _host.Server.Paused ? "Resume remote control" : "Pause remote control";
        _form.RefreshState();
    }

    void OnSessionsChanged()
    {
        var sessions = _host.Server.ActiveSessions;
        var text = _host.Server.Paused ? "AnyPC — paused"
            : sessions.Count == 0 ? "AnyPC — waiting for iPhone"
            : $"AnyPC — controlled by {sessions[0].DeviceName}";
        _icon.Text = text.Length > 63 ? text[..63] : text;

        if (sessions.Count > _lastSessionCount)
            _icon.ShowBalloonTip(3000, "AnyPC", $"{sessions[^1].DeviceName} is now controlling this PC.", ToolTipIcon.Info);
        _lastSessionCount = sessions.Count;
        _form.RefreshState();
    }

    void ExitApp()
    {
        _icon.Visible = false;
        _form.AllowClose();
        _form.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Dispose();
            _form.Dispose();
        }
        base.Dispose(disposing);
    }
}
