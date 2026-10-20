using System.Drawing;
using System.Windows.Forms;

namespace KeyShiftBridge;

internal sealed class MainForm : Form
{
    private readonly AudioController _controller;
    private readonly CheckBox _enabled;
    private readonly TrackBar _pitch;
    private readonly Label _pitchValue;
    private readonly Label _status;
    private readonly Label _device;
    private readonly System.Windows.Forms.Timer _timer;
    private bool _syncing;
    private bool _changingPitch;

    internal bool AllowClose;

    public MainForm(AudioController controller)
    {
        _controller = controller;
        Text = "Key Shift";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(500, 430);
        MinimumSize = new Size(460, 410);
        BackColor = Color.FromArgb(20, 20, 20);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10F);

        var title = new Label
        {
            Text = "Key Shift",
            Font = new Font("Segoe UI", 25F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(28, 24),
        };
        var subtitle = new Label
        {
            Text = "Меняй тональность Spotify, сохраняя темп музыки.",
            AutoSize = true,
            ForeColor = Color.Silver,
            Location = new Point(31, 72),
        };
        _status = new Label
        {
            AutoSize = false,
            Location = new Point(31, 349),
            Size = new Size(438, 32),
            ForeColor = Color.Gainsboro,
            Text = controller.StatusMessage,
        };
        _device = new Label
        {
            AutoSize = false,
            Location = new Point(31, 383),
            Size = new Size(438, 24),
            ForeColor = Color.Gray,
        };

        _enabled = new CheckBox
        {
            Text = "Обработка звука Spotify",
            AutoSize = true,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            Location = new Point(31, 122),
            UseVisualStyleBackColor = false,
            BackColor = BackColor,
            ForeColor = ForeColor,
        };
        _enabled.CheckedChanged += async (_, _) =>
        {
            if (_syncing) return;
            _enabled.Enabled = false;
            _status.Text = _enabled.Checked ? "Подключаюсь к Spotify…" : "Отключаю обработку…";
            await _controller.SetEnabledAsync(_enabled.Checked);
            _enabled.Enabled = true;
            RefreshState();
        };

        var pitchTitle = new Label
        {
            Text = "Тональность",
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(31, 176),
        };
        _pitchValue = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(30, 215, 96),
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            Location = new Point(31, 207),
        };

        _pitch = new TrackBar
        {
            Minimum = -12,
            Maximum = 12,
            Value = 0,
            TickFrequency = 1,
            SmallChange = 1,
            LargeChange = 1,
            AutoSize = false,
            Height = 46,
            Location = new Point(25, 238),
            Width = 438,
            BackColor = BackColor,
        };
        _pitch.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _changingPitch = true;
            _controller.SetSemitones(_pitch.Value);
            UpdatePitchLabel();
            _changingPitch = false;
        };

        var lower = MakeButton("−", 31, 294, 42, 38);
        lower.Click += (_, _) => SetPitch(Math.Max(-12, _pitch.Value - 1));
        var raise = MakeButton("+", 80, 294, 42, 38);
        raise.Click += (_, _) => SetPitch(Math.Min(12, _pitch.Value + 1));
        var reset = MakeButton("Сбросить", 136, 294, 112, 38);
        reset.Click += (_, _) => SetPitch(0);

        Controls.AddRange([title, subtitle, _enabled, pitchTitle, _pitchValue, _pitch,
            lower, raise, reset, _status, _device]);

        FormClosing += (_, e) =>
        {
            if (!AllowClose)
            {
                e.Cancel = true;
                Hide();
            }
        };

        _timer = new System.Windows.Forms.Timer { Interval = 500 };
        _timer.Tick += (_, _) => RefreshState();
        _timer.Start();
        UpdatePitchLabel();
        RefreshState();
    }

    private static Button MakeButton(string text, int x, int y, int width, int height) => new()
    {
        Text = text,
        Location = new Point(x, y),
        Size = new Size(width, height),
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(42, 42, 42),
        ForeColor = Color.White,
        Font = new Font("Segoe UI", 10F, FontStyle.Bold),
        Cursor = Cursors.Hand,
    };

    private void SetPitch(int value)
    {
        _pitch.Value = value;
        if (_controller.Semitones != value)
            _controller.SetSemitones(value);
        UpdatePitchLabel();
    }

    private void UpdatePitchLabel()
    {
        var value = _pitch.Value;
        _pitchValue.Text = value switch
        {
            0 => "0 полутонов · исходная тональность",
            > 0 => $"+{value} полутонов",
            _ => $"{value} полутонов",
        };
    }

    private void RefreshState()
    {
        if (IsDisposed) return;
        _syncing = true;
        if (!_enabled.Focused) _enabled.Checked = _controller.Enabled;
        if (!_changingPitch && !_pitch.Focused)
        {
            var value = (int)Math.Round(_controller.Semitones);
            if (value is >= -12 and <= 12) _pitch.Value = value;
            UpdatePitchLabel();
        }
        _status.Text = _controller.StatusMessage;
        _device.Text = _controller.OutputDeviceName is { Length: > 0 } name
            ? $"Устройство вывода: {name}"
            : "";
        _syncing = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
