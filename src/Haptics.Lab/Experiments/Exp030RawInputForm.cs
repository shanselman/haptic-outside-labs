using Haptics.Core;
using Haptics.Windows;

namespace Haptics.Lab.Experiments;

/// <summary>
/// EXP-030: register mouse-only Raw Input (RIDEV_INPUTSINK) and count physical background events per device
/// while the operator uses another app. Never sends haptics. Never registers keyboard input.
/// </summary>
public sealed class Exp030RawInputForm : Form, IExperimentForm
{
    private readonly CliOptions _opt;
    private readonly Label _text = new() { Dock = DockStyle.Fill, Font = Ui.Mono, Padding = new Padding(10) };
    private readonly FlowLayoutPanel _bar = new() { Dock = DockStyle.Bottom, Height = 60, Padding = new Padding(6) };
    private readonly Button _start = new() { Text = "Start capture", AutoSize = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly Dictionary<long, (long Total, long Background, long Buttons, long Wheel)> _byDevice = new();
    private readonly Dictionary<string, long> _bySource = new();
    private readonly string _target;
    private MessageWindowThread? _raw;
    private ExperimentSession? _session;
    private double _endAt;
    private bool _done;
    private int _seconds;

    public Exp030RawInputForm(CliOptions opt)
    {
        _opt = opt;
        _target = Exp010ForegroundForm.ParseTarget(opt.Get("target"));
        _seconds = Math.Clamp(opt.Int("seconds", 20), 5, 300);
        Text = "EXP-030 · Passive raw input and device attribution";
        ClientSize = new Size(760, 460);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Ui.Body;
        _text.Text = $"Press Start, then switch to ANOTHER app and use ONLY the target device ({_target}) normally for {_seconds}s.\n" +
                     "This window only counts events. It never sends haptics, never reads keys, never changes focus.";
        _bar.Controls.Add(_start);
        Controls.Add(_text);
        Controls.Add(_bar);
        _start.Click += (_, _) => Begin();
        _timer.Tick += (_, _) => Tick();
        FormClosing += (_, _) =>
        {
            Finish(aborted: true);
            _raw?.Dispose();
        };
    }

    public string? ResultPath { get; private set; }

    private void Begin()
    {
        _start.Enabled = false;
        _session = ExperimentSession.Start("EXP-030", "physical", _opt.Get("out"));
        _session.Result.Method["operatorTargetDevice"] = _target;
        _session.Result.Limitations.Add("The target is operator-selected, not independently identified by Raw Input. Promoted pen/touch signatures are attribution metadata, not physical proof.");
        _raw = new MessageWindowThread("exp030-raw", MessageWindowKind.MessageOnly, registerRawMouse: true);
        _raw.RawMouse += OnRaw;
        _raw.Start();
        _endAt = MonotonicClock.Instance.NowMs + (_seconds * 1000);
        _timer.Start();
    }

    private void OnRaw(RawMouseInput i)
    {
        lock (_byDevice)
        {
            var c = _byDevice.GetValueOrDefault(i.DeviceHandle);
            _byDevice[i.DeviceHandle] = (c.Total + 1,
                c.Background + (i.Code == RawInputCode.Background ? 1 : 0),
                c.Buttons + ((i.Buttons & ~(RawMouseButtons.Wheel | RawMouseButtons.HWheel)) != 0 ? 1 : 0),
                c.Wheel + (i.WheelDelta != 0 ? 1 : 0));
            var source = $"{DescribeSource(i)}/{i.Code}";
            _bySource[source] = _bySource.GetValueOrDefault(source) + 1;
        }
    }

    private void Tick()
    {
        var left = (_endAt - MonotonicClock.Instance.NowMs) / 1000;
        lock (_byDevice)
        {
            _text.Text = $"Capturing… {Math.Max(0, left):0}s left. Max WM_INPUT callback {_raw?.MaxCallbackMs:0.000} ms\n\n" +
                         string.Join("\n", _byDevice.Select(kv => $"{Describe(kv.Key),-40} total {kv.Value.Total,6}  background {kv.Value.Background,6}  buttons {kv.Value.Buttons,4}  wheel {kv.Value.Wheel,4}")) +
                         "\n\nInput sources: " + string.Join(", ", _bySource.Select(kv => $"{kv.Key}={kv.Value}"));
        }

        if (left <= 0)
        {
            Finish(aborted: false);
        }
    }

    private static string Describe(long h) => h == 0 ? "synthetic/unknown (hDevice=0)" : $"{RawInputDevices.Describe(h).VidPid ?? "?"} {RawInputDevices.Describe(h).Connection}";

    internal static string DescribeSource(RawMouseInput input) =>
        input.InputClass is PointerInputClass.Pen or PointerInputClass.Promoted
            ? input.InputClass == PointerInputClass.Pen ? "promoted-pen" : "promoted-touch"
            : input.InputClass.ToString();

    private void Finish(bool aborted)
    {
        if (_session is null || _done)
        {
            return;
        }

        _done = true;
        _timer.Stop();
        var normal = !aborted && MessageBox.Show(this, $"Did the target device ({_target}) behave completely normally in the other app during capture (no lag, no lost clicks, no focus changes)?",
            "EXP-030 operator check", MessageBoxButtons.YesNo) == DialogResult.Yes;
        var r = _session.Result;
        Dictionary<long, (long Total, long Background, long Buttons, long Wheel)> snapshot;
        lock (_byDevice)
        {
            snapshot = new(_byDevice);
            r.Observations["inputSources"] = new Dictionary<string, long>(_bySource);
        }

        var hardwareBg = snapshot.Where(kv => kv.Key != 0).Sum(kv => kv.Value.Background);
        r.Observations["devices"] = snapshot.Select(kv => new
        {
            device = kv.Key == 0 ? null : RawInputDevices.Describe(kv.Key),
            kv.Value.Total,
            kv.Value.Background,
            kv.Value.Buttons,
            kv.Value.Wheel,
        }).ToList();
        r.Observations["hardwareBackgroundEvents"] = hardwareBg;
        r.Observations["registered"] = _raw?.RawInputRegistered;
        if (_target == "mouse")
        {
            r.Observations["operatorReportsNormalMouse"] = normal;
        }
        r.Observations["operatorReportsNormalTarget"] = normal;
        r.LatencyMs["maxRawCallback"] = _raw?.MaxCallbackMs;
        r.Method["registration"] = "UsagePage 0x01 / Usage 0x02 (mouse only), RIDEV_INPUTSINK | RIDEV_DEVNOTIFY. No NOLEGACY, no CAPTUREMOUSE, no keyboard.";
        r.Method["seconds"] = _seconds;
        _session.Assert("raw input registered", _raw?.RawInputRegistered == true);
        _session.Assert(">= 50 nonzero-handle background events", hardwareBg >= 50, $"{hardwareBg}");
        _session.Assert("callback non-blocking (< 2 ms max)", (_raw?.MaxCallbackMs ?? 99) < 2, $"{_raw?.MaxCallbackMs:0.000}ms");
        _session.Assert("operator: target device behaved normally", normal);
        r.Status = aborted || hardwareBg == 0 ? ExperimentStatus.INDETERMINATE : r.Assertions.All(a => a.Passed) ? ExperimentStatus.PASS : ExperimentStatus.FAIL;
        r.EvidenceClass = EvidenceClass.OBSERVED;
        r.Explanation = $"{hardwareBg} nonzero-handle background events across {snapshot.Count(kv => kv.Key != 0)} device(s); operator-selected target={_target}; max callback {_raw?.MaxCallbackMs:0.000}ms; operator normal={normal}.";
        if (r.Status == ExperimentStatus.PASS)
        {
            r.AddLabel(EvidenceLabels.RawReceived);
        }

        ResultPath = _session.Complete();
        _session.Dispose();
        _text.Text += $"\n\n{r.Status}: {r.Explanation}\nSaved {ResultPath}";
    }
}
