using Haptics.Core;

namespace Haptics.Lab;

internal static class Ui
{
    public static readonly Font Body = new("Segoe UI", 10f);
    public static readonly Font Big = new("Segoe UI Semibold", 16f);
    public static readonly Font Mono = new("Cascadia Mono", 9f);

    /// <summary>A clickable, non-focusable "button" (a Label never takes keyboard focus or activates a window).</summary>
    public static Label Choice(string text, Color back, EventHandler onClick, int width = 120)
    {
        var l = new Label
        {
            Text = text,
            AutoSize = false,
            Size = new Size(width, 44),
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = back,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 11f),
            Cursor = Cursors.Hand,
            Margin = new Padding(4),
        };
        l.Click += onClick;
        return l;
    }

    public static Label Text(string text, Font? font = null, int width = 0) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(width == 0 ? 900 : width, 0),
        Font = font ?? Body,
        Margin = new Padding(4, 6, 4, 4),
    };

    public static Color StatusColor(ExperimentStatus s) => s switch
    {
        ExperimentStatus.PASS => Color.FromArgb(16, 124, 16),
        ExperimentStatus.FAIL => Color.FromArgb(196, 43, 28),
        ExperimentStatus.BLOCKED => Color.FromArgb(138, 80, 0),
        ExperimentStatus.INDETERMINATE => Color.FromArgb(0, 90, 158),
        _ => Color.DimGray,
    };
}

/// <summary>
/// Non-activating 1–5 rating panel (helpful, annoying) for blinded style comparisons. Like the EXP-020 answer
/// panel, clicking it never takes focus from the app the user is working in.
/// </summary>
internal sealed class RatingPanel : Form
{
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private int? _helpful;
    private int? _annoying;

    public RatingPanel(string title, Action<int, int> onRated)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(32, 32, 40);
        ForeColor = Color.White;
        Size = new Size(470, 190);
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), FlowDirection = FlowDirection.LeftToRight };
        flow.Controls.Add(new Label { Text = title, AutoSize = true, MaximumSize = new Size(450, 0), Font = new Font("Segoe UI Semibold", 10.5f), ForeColor = Color.White });
        flow.SetFlowBreak(flow.Controls[^1], true);
        Row(flow, "Helpful?", v => _helpful = v);
        Row(flow, "Annoying?", v => _annoying = v);
        Controls.Add(flow);

        void Row(FlowLayoutPanel f, string label, Action<int> set)
        {
            f.Controls.Add(new Label { Text = label, Width = 90, ForeColor = Color.White, Font = Ui.Body, Margin = new Padding(4, 14, 4, 4) });
            var choices = new List<Label>();
            for (var i = 1; i <= 5; i++)
            {
                var v = i;
                var l = Ui.Choice(v.ToString(), Color.FromArgb(70, 70, 80), (_, _) => { }, 56);
                l.Click += (_, _) =>
                {
                    set(v);
                    foreach (var c in choices)
                    {
                        c.BackColor = c == l ? Color.FromArgb(0, 120, 212) : Color.FromArgb(70, 70, 80);
                    }

                    if (_helpful is { } h && _annoying is { } a)
                    {
                        onRated(h, a);
                    }
                };
                choices.Add(l);
                f.Controls.Add(l);
            }

            f.SetFlowBreak(f.Controls[^1], true);
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = MA_NOACTIVATE;
            return;
        }

        base.WndProc(ref m);
    }
}

/// <summary>
/// Topmost, NON-activating answer panel for background trials. Clicking it never makes the lab the
/// foreground app (WS_EX_NOACTIVATE + MA_NOACTIVATE), so the app under test keeps focus.
/// </summary>
internal sealed class AnswerPanel : Form
{
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    private readonly Label _message = new() { AutoSize = false, Dock = DockStyle.Top, Height = 92, Font = new Font("Segoe UI", 10.5f), Padding = new Padding(8), ForeColor = Color.White };
    private readonly FlowLayoutPanel _buttons = new() { Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(4) };

    public AnswerPanel(Action<TrialAnswer> onAnswer, Action onAbort)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(32, 32, 40);
        Size = new Size(460, 160);
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
        _buttons.Controls.Add(Ui.Choice("Felt it", Color.FromArgb(16, 124, 16), (_, _) => onAnswer(TrialAnswer.FeltYes), 105));
        _buttons.Controls.Add(Ui.Choice("Not felt", Color.FromArgb(96, 96, 96), (_, _) => onAnswer(TrialAnswer.FeltNo), 105));
        _buttons.Controls.Add(Ui.Choice("Unsure", Color.FromArgb(0, 90, 158), (_, _) => onAnswer(TrialAnswer.Uncertain), 105));
        _buttons.Controls.Add(Ui.Choice("Stop", Color.FromArgb(140, 40, 30), (_, _) => onAbort(), 90));
        Controls.Add(_buttons);
        Controls.Add(_message);
        SetAnswersEnabled(false);
    }

    public Rectangle ScreenBounds { get; private set; }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void SetMessage(string text, Color? back = null)
    {
        _message.Text = text;
        _message.BackColor = back ?? Color.FromArgb(32, 32, 40);
    }

    public void SetAnswersEnabled(bool enabled)
    {
        for (var i = 0; i < 3; i++)
        {
            _buttons.Controls[i].Enabled = enabled;
            _buttons.Controls[i].BackColor = enabled ? (i == 0 ? Color.FromArgb(16, 124, 16) : i == 1 ? Color.FromArgb(96, 96, 96) : Color.FromArgb(0, 90, 158)) : Color.FromArgb(60, 60, 60);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ScreenBounds = Bounds;
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        ScreenBounds = Bounds;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = MA_NOACTIVATE;
            return;
        }

        base.WndProc(ref m);
    }
}
