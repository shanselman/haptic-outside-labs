using Haptics.Core;
using Haptics.Windows;

namespace Haptics.Lab.Experiments;

/// <summary>
/// EXP-010: our own focused window receives physical mouse input; on that UI thread we call the documented
/// GetForCurrentThread().TrySendHapticWaveform. Randomized pulse/no-pulse trials; the operator answers
/// before learning which was which. The API return value is logged separately and never counts as proof.
/// </summary>
public sealed class Exp010ForegroundForm : Form, IExperimentForm
{
    private enum State { Setup, Arming, AwaitAnswer, Done }

    private readonly CliOptions _opt;
    private readonly CheckedListBox _arms = new() { Height = 120, Width = 160, CheckOnClick = true };
    private readonly NumericUpDown _perArm = new() { Minimum = 3, Maximum = 30, Value = 6, Width = 60 };
    private readonly NumericUpDown _controls = new() { Minimum = 3, Maximum = 30, Value = 6, Width = 60 };
    private readonly NumericUpDown _intensity = new() { Minimum = 0.05m, Maximum = 1m, Increment = 0.05m, DecimalPlaces = 2, Value = 1m, Width = 60 };
    private readonly ComboBox _target = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly CheckBox _nativeConfirmed = new() { Text = "I have felt this device's native haptics (e.g. Snap on a mouse, a click on the touchpad) at the current intensity", AutoSize = true };
    private readonly Button _start = new() { Text = "Start blinded trials", AutoSize = true };
    private readonly Panel _pad = new() { BackColor = Color.FromArgb(36, 40, 52), Dock = DockStyle.Fill, Cursor = Cursors.Cross };
    private readonly Label _padText = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, Font = Ui.Big };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 46, Font = Ui.Body, Padding = new Padding(8) };
    private readonly Label _live = new() { Dock = DockStyle.Bottom, Height = 24, Font = Ui.Mono, Padding = new Padding(8, 0, 8, 0), ForeColor = Color.DimGray };
    private readonly FlowLayoutPanel _answers = new() { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(4) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private readonly List<TrialRecord> _trials = new();
    private OutputOwnership? _outputOwnership;
    private readonly Random _rng = new();
    private ExperimentSession? _session;
    private IReadOnlyList<ScheduledTrial> _schedule = [];
    private State _state = State.Setup;
    private int _next;
    private double _armedAt, _delay, _lastMove, _lastLive;
    private TrialRecord? _current;
    private int _seed;

    public Exp010ForegroundForm(CliOptions opt)
    {
        _opt = opt;
        Text = "EXP-010 · Foreground physical haptic baseline";
        ClientSize = new Size(900, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Ui.Body;
        KeyPreview = true;

        foreach (var w in HapticsApi.Waveforms.Keys)
        {
            _arms.Items.Add(w, w is "Click" or "Step");
        }

        _target.Items.AddRange(["mouse", "touchpad", "any"]);
        _target.SelectedItem = (opt.Get("target") ?? "mouse").ToLowerInvariant();
        _perArm.Value = Math.Clamp(opt.Int("trials-per-arm", 6), 3, 30);
        _controls.Value = Math.Clamp(opt.Int("controls", 6), 3, 30);
        _intensity.Value = (decimal)Math.Clamp(opt.Double("intensity", 1.0), 0.05, 1.0);

        var intro = Ui.Text(
            $"Use ONLY the target haptic device ({(opt.Get("target") ?? "mouse").ToLowerInvariant()}). Glide it inside the dark pad (on a touchpad: glide, don't press). After a random delay the pad flashes NOW: " +
            "a pulse may or may not have been sent. Answer honestly (keys F / N / U, or the buttons). About a third of the trials are silent controls.", width: 860);
        var setup = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8) };
        setup.Controls.AddRange([
            intro,
            Ui.Text("Waveform arms:"), _arms,
            Ui.Text("Pulses per arm:"), _perArm, Ui.Text("Controls:"), _controls, Ui.Text("Intensity:"), _intensity,
            Ui.Text("Target device:"), _target, _nativeConfirmed, _start,
        ]);
        setup.SetFlowBreak(intro, true);
        setup.SetFlowBreak(_intensity, true);
        _answers.Controls.Add(Ui.Choice("Felt it (F)", Color.FromArgb(16, 124, 16), (_, _) => Answer(TrialAnswer.FeltYes), 150));
        _answers.Controls.Add(Ui.Choice("Not felt (N)", Color.FromArgb(96, 96, 96), (_, _) => Answer(TrialAnswer.FeltNo), 150));
        _answers.Controls.Add(Ui.Choice("Unsure (U)", Color.FromArgb(0, 90, 158), (_, _) => Answer(TrialAnswer.Uncertain), 150));
        _answers.Visible = false;

        _pad.Controls.Add(_padText);
        _padText.Text = HapticsApi.TypePresent ? "Configure, then Start." : "InputHapticsManager not present on this OS — EXP-010 is BLOCKED.";
        _padText.MouseMove += (_, _) => _lastMove = MonotonicClock.Instance.NowMs;
        _pad.MouseMove += (_, _) => _lastMove = MonotonicClock.Instance.NowMs;
        Controls.Add(_pad);
        Controls.Add(_answers);
        Controls.Add(_live);
        Controls.Add(_status);
        Controls.Add(setup);

        _start.Click += (_, _) => Begin();
        _timer.Tick += (_, _) => Tick();
        KeyDown += OnKey;
        FormClosing += (_, _) => FinishIfNeeded(aborted: true);
        _timer.Start();
    }

    public string? ResultPath { get; private set; }

    internal static ExperimentSession StartOwnedSession(OutputOwnership ownership, Func<ExperimentSession> start)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(start);
        try
        {
            return start();
        }
        catch
        {
            ownership.Dispose();
            throw;
        }
    }

    private void Begin()
    {
        var arms = _arms.CheckedItems.Cast<string>().ToList();
        if (arms.Count == 0 || !HapticsApi.TypePresent)
        {
            _status.Text = arms.Count == 0 ? "Pick at least one waveform." : "API not present.";
            return;
        }

        _outputOwnership = OutputOwnership.Create("EXP-010");
        if (!_outputOwnership.TryAcquire())
        {
            _outputOwnership.Dispose();
            _outputOwnership = null;
            _status.Text = "Another Haptics Everywhere engine owns output. Stop it before running this physical experiment.";
            return;
        }

        _seed = _opt.Int("seed", Random.Shared.Next());
        _schedule = TrialSchedule.Build(arms, (int)_perArm.Value, (int)_controls.Value, _seed);
        try
        {
            _session = StartOwnedSession(_outputOwnership, () => ExperimentSession.Start("EXP-010", "physical", _opt.Get("out")));
        }
        catch
        {
            _outputOwnership = null;
            throw;
        }

        var m = _session.Result.Method;
        m["route"] = OutputRoutes.ForegroundManager;
        m["routeDescription"] = OutputRoutes.Describe(OutputRoutes.ForegroundManager);
        m["arms"] = arms;
        m["pulsesPerArm"] = (int)_perArm.Value;
        m["controls"] = (int)_controls.Value;
        m["intensity"] = (double)_intensity.Value;
        m["fallbackWaveform"] = 0;
        m["targetDevice"] = _target.SelectedItem;
        m["seed"] = _seed;
        m["operatorConfirmedNativeHaptics"] = _nativeConfirmed.Checked;
        m["outputOwner"] = _outputOwnership.Owner;
        m["outputOwnershipGranted"] = _outputOwnership.IsOwner;
        m["blinding"] = "Operator answers before the trial type is revealed; identical visual cue for pulse and control.";
        _session.Result.NegativeControls.Add($"{(int)_controls.Value} randomized no-call control trials with the identical NOW cue.");
        foreach (Control c in _start.Parent!.Controls)
        {
            if (c is not Label)
            {
                c.Enabled = false;
            }
        }

        _next = 0;
        ArmNext();
    }

    private void ArmNext()
    {
        if (_next >= _schedule.Count)
        {
            FinishIfNeeded(aborted: false);
            return;
        }

        _armedAt = MonotonicClock.Instance.NowMs;
        _delay = 700 + _rng.NextDouble() * 1500;
        _state = State.Arming;
        _answers.Visible = false;
        _pad.BackColor = Color.FromArgb(36, 40, 52);
        _padText.Text = $"Trial {_next + 1} of {_schedule.Count}\nkeep moving the mouse in this pad…";
    }

    private void Tick()
    {
        var now = MonotonicClock.Instance.NowMs;
        if (now - _lastLive > 250)
        {
            _lastLive = now;
            var st = HapticsApi.InspectCurrentThread();
            _live.Text = $"UI thread {st.ThreadId}: manager={(st.ManagerOk ? "ok" : "none")} device={st.DeviceType} controller={(st.ControllerPresent ? "present" : "none")}";
        }

        if (_state != State.Arming)
        {
            return;
        }

        var inPad = _pad.ClientRectangle.Contains(_pad.PointToClient(Cursor.Position));
        if (inPad && now - _lastMove < 150 && now - _armedAt >= _delay)
        {
            Fire(now);
        }
    }

    private void Fire(double now)
    {
        var plan = _schedule[_next];
        var st = HapticsApi.InspectCurrentThread();
        var t = new TrialRecord { Index = plan.Index, Arm = plan.Arm, IsControl = plan.IsControl, FiredAtMs = now };
        t.Context["deviceType"] = st.DeviceType;
        t.Context["controllerPresent"] = st.ControllerPresent;
        t.Context["controllerIdHash"] = st.ControllerIdHash;
        t.Context["inputAgeMs"] = Math.Round(now - _lastMove, 1);
        t.Context["labForeground"] = ActiveForm == this;
        _outputOwnership?.Record(t);
        var target = (string)_target.SelectedItem!;
        if (target != "any" && !string.Equals(st.DeviceType, target, StringComparison.OrdinalIgnoreCase))
        {
            t.Invalidate($"device-type:{st.DeviceType} (target {target})");
        }

        if (ActiveForm != this)
        {
            t.Invalidate("lab-window-not-foreground");
        }

        if (!plan.IsControl && _outputOwnership?.TryRun(() =>
                t.Attempt = HapticsApi.SendViaThreadManager(OutputRoutes.ForegroundManager, plan.Arm, (double)_intensity.Value, _lastMove)) != true)
        {
            t.Invalidate("output-owner-not-held");
        }

        _current = t;
        _state = State.AwaitAnswer;
        _pad.BackColor = Color.FromArgb(200, 120, 0);
        _padText.Text = $"NOW — trial {plan.Index}\nDid you feel a pulse?";
        _answers.Visible = true;
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (_state != State.AwaitAnswer)
        {
            return;
        }

        var a = e.KeyCode switch
        {
            Keys.F => TrialAnswer.FeltYes,
            Keys.N => TrialAnswer.FeltNo,
            Keys.U => TrialAnswer.Uncertain,
            _ => TrialAnswer.None,
        };
        if (a != TrialAnswer.None)
        {
            e.Handled = true;
            Answer(a);
        }
    }

    private void Answer(TrialAnswer a)
    {
        if (_state != State.AwaitAnswer || _current is null)
        {
            return;
        }

        _current.Answer = a;
        _current.AnsweredAtMs = MonotonicClock.Instance.NowMs;
        _trials.Add(_current);
        _session!.Artifacts.Trace(new { type = "trial", trial = _current });
        _current = null;
        _next++;
        ArmNext();
    }

    private void FinishIfNeeded(bool aborted)
    {
        if (_session is null || _state == State.Done)
        {
            return;
        }

        _outputOwnership?.StopSending();
        _state = State.Done;
        _timer.Stop();
        var r = _session.Result;
        var verdicts = new Dictionary<string, ArmVerdict>();
        foreach (var arm in _schedule.Where(x => !x.IsControl).Select(x => x.Arm).Distinct())
        {
            verdicts[$"{OutputRoutes.ForegroundManager}:{arm}"] = PhysicalEvaluator.Evaluate(arm, _trials, _nativeConfirmed.Checked);
        }

        r.Routes = verdicts;
        (r.Status, r.EvidenceClass) = PhysicalEvaluator.Combine(verdicts.Values);
        foreach (var label in verdicts.Values.SelectMany(v => v.Labels).Distinct())
        {
            r.AddLabel(label);
        }

        var attempts = _trials.Where(t => t.Attempt is not null).Select(t => t.Attempt!).ToList();
        r.Method["targetControllerIdHash"] = TargetControllerIdentity(_trials);
        r.Observations["trialsAnswered"] = _trials.Count;
        r.Observations["trialsScheduled"] = _schedule.Count;
        r.Observations["apiAccepted"] = attempts.Count(a => a.Accepted);
        r.Observations["apiRejected"] = attempts.Count(a => a.Called && !a.Accepted);
        r.Observations["deviceTypesSeen"] = _trials.Select(t => t.Context["deviceType"]?.ToString()).Distinct().ToList();
        r.Observations["invalidTrials"] = _trials.Count(t => !t.ProvenanceValid);
        r.Observations["trials"] = _trials;
        r.LatencyMs["inputToCallP50"] = Pct(attempts.Select(a => a.InputToCallMs ?? double.NaN), 0.5);
        r.LatencyMs["callDurationP95"] = Pct(attempts.Select(a => a.CallEndMs - a.CallStartMs), 0.95);
        r.Explanation = (aborted ? "ABORTED early. " : "") + string.Join(" | ", verdicts.Select(v => $"{v.Key}: {v.Value.Status} — {v.Value.Reason}"));
        r.Limitations.Add("The operator could potentially hear an actuator click; an acoustic cue is still a physical effect but is noted here.");
        r.Limitations.Add("Software timestamps measure call issue time (t6), not physical onset (t8).");
        r.NextDecision = r.Status == ExperimentStatus.PASS
            ? "Foreground actuation confirmed. Run EXP-020 with another app in the foreground."
            : "Check the mouse's haptic intensity in Windows Settings, battery, pairing, and that you used the mouse (not the touchpad); then rerun.";
        try
        {
            ResultPath = _session.Complete();
        }
        finally
        {
            _session.Dispose();
            _outputOwnership?.Dispose();
            _outputOwnership = null;
        }
        _answers.Visible = false;
        _pad.BackColor = Ui.StatusColor(r.Status);
        _padText.Text = $"{r.Status} [{r.EvidenceClass}]\n{string.Join("\n", verdicts.Select(v => $"{v.Key}: {v.Value.Hits}/{v.Value.Hits + v.Value.Misses} felt, controls {v.Value.FalseAlarms}/{v.Value.FalseAlarms + v.Value.CorrectRejections}"))}";
        _status.Text = $"Saved {ResultPath}";
    }

    internal static string? TargetControllerIdentity(IEnumerable<TrialRecord> trials)
    {
        var identities = trials.Where(t => t.ProvenanceValid && t.Answer != TrialAnswer.None)
            .Select(t => t.IsControl
                ? t.Context.GetValueOrDefault("controllerIdHash") as string
                : t.Attempt?.ControllerIdHash).ToArray();
        return identities.Length > 0 && identities.All(EvidenceLedger.KnownControllerIdentity) &&
               identities.Distinct(StringComparer.Ordinal).Count() == 1 ? identities[0] : null;
    }

    private static double? Pct(IEnumerable<double> values, double p)
    {
        var a = values.Where(v => !double.IsNaN(v)).Order().ToArray();
        return a.Length == 0 ? null : Math.Round(a[Math.Clamp((int)Math.Ceiling(p * a.Length) - 1, 0, a.Length - 1)], 2);
    }
}
