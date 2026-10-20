using System.Drawing;
using System.Windows.Forms;

namespace KeyShiftBridge;

internal sealed class TrayContext : ApplicationContext
{
    private readonly AudioController _controller;
    private readonly LocalApi _api;
    private readonly MainForm _form;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _status;
    private readonly ToolStripMenuItem _toggle;
    private readonly System.Windows.Forms.Timer _timer;
    private bool _exiting;

    public TrayContext(AudioController controller, LocalApi api, bool showWindow)
    {
        _controller = controller;
        _api = api;
        _api.Start();
        _form = new MainForm(controller);
        MainForm = _form;

        var menu = new ContextMenuStrip();
        _status = new ToolStripMenuItem("Key Shift запущен") { Enabled = false };
        var open = new ToolStripMenuItem("Открыть Key Shift", null, (_, _) => OpenWindow());
        _toggle = new ToolStripMenuItem("Включить обработку Spotify", null, async (_, _) =>
        {
            await _controller.SetEnabledAsync(!_controller.Enabled);
            RefreshText();
        });
        var quit = new ToolStripMenuItem("Выход", null, async (_, _) => await ExitAsync());
        menu.Items.AddRange([_status, open, _toggle, new ToolStripSeparator(), quit]);

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Key Shift — обработка Spotify",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenWindow();

        _timer = new System.Windows.Forms.Timer { Interval = 500 };
        _timer.Tick += (_, _) => RefreshText();
        _timer.Start();
        RefreshText();

        if (showWindow) OpenWindow();
    }

    private void OpenWindow()
    {
        if (_form.IsDisposed) return;
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized)
            _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _timer.Stop();
        await _controller.SetEnabledAsync(false);
        _form.AllowClose = true;
        _icon.Visible = false;
        _form.Close();
        ExitThread();
    }

    private void RefreshText()
    {
        _status.Text = _controller.StatusMessage;
        _toggle.Text = _controller.Enabled ? "Выключить обработку Spotify" : "Включить обработку Spotify";
        _icon.Text = _controller.Enabled ? "Key Shift — Spotify обрабатывается" : "Key Shift — готов";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _form.Dispose();
            _api.Stop();
            _controller.Dispose();
        }
        base.Dispose(disposing);
    }
}
