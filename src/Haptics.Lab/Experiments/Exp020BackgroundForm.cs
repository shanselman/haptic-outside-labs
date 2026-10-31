using Haptics.Core;
using Haptics.Windows;
using Windows.Devices.Haptics;

namespace Haptics.Lab.Experiments;

/// <summary>
/// EXP-020 (+ EXP-022 arms). Another application is foreground. Our dedicated raw-input thread receives
/// physical background WM_INPUT (RIDEV_INPUTSINK) and, on that same thread, tries each candidate route:
///   R2  GetForCurrentThread() on the raw-input thread
///   R3a SimpleHapticsController captured earlier from our own foreground interaction, used directly
///   R3b SetOverrideHapticsController(captured) on the raw-input thread's manager, send, clear
/// interleaved with silent controls. The operator answers on a non-activating panel so focus never moves.
/// </summary>
public sealed class Exp020BackgroundForm : Form, IExperimentForm
{
    private enum Phase { Setup, Prime, Trials, Done }

    private readonly CliOptions _opt;
    private readonly CheckedListBox _arms = new() { Height = 70, Width = 260, CheckOnClick = true };
    private readonly ComboBox _waveform = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly ComboBox _window = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly ComboBox _target = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly NumericUpDown _perArm = new() { Minimum = 3, Maximum = 30, Value = 6, Width = 60 };
    private readonly NumericUpDown _controls = new() { Minimum = 3, Maximum = 30, Value = 6, Width = 60 };
    private readonly NumericUpDown _intensity = new() { Minimum = 0.05m, Maximum = 1m, Increment = 0.05m, DecimalPlaces = 2, Value = 1m, Width = 60 };
    private readonly Button _start = new() { Text = "1. Start (prime)", AutoSize = true };
    private readonly Button _begin = new() { Text = "2. Begin background trials", AutoSize = true, Enabled = false };
    private readonly Label _baseline = new() { AutoSize = true, Font = Ui.Body };
    private readonly Panel _pad = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(36, 40, 52), Cursor = Cursors.Cross };
    private readonly Label _padText = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, Font = Ui.Big };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 64, Font = Ui.Body, Padding = new Padding(8) };
    private readonly List<TrialRecord> _trials = new();
    private readonly Dictionary<long, int> _primeDevices = new();
    private readonly Dictionary<string, long> _rawCounts = new();
    private readonly List<Dictionary<string, object?>> _rawThreadSamples = new();
    private readonly Random _rng = new();
    private MessageWindowThread? _raw;
    private AnswerPanel? _panel;
    private ExperimentSession? _session;
    private OutputOwnership? _outputOwnership;
    private CapturedController? _captured;
    private IReadOnlyList<ScheduledTrial> _schedule = [];
    private Phase _phase = Phase.Setup;
    private int _next;
    private bool _baselinePassed;
    private string _waveName = "Click";
    private double _intensityValue = 1.0;

    // Raw-thread-only state.
    private ScheduledTrial? _pending;
    private double _armedAt, _delay, _foreignSince = -1, _lastSample;
    private long _targetDevice;
    private bool _targetLocked;
    private Rectangle _panelBounds;

    private TrialRecord? _awaiting;

    public Exp020BackgroundForm(CliOptions opt)
    {
        _opt = opt;
        Text = "EXP-020 · Background output route (critical gate)";
        ClientSize = new Size(940, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Ui.Body;

        _arms.Items.Add(OutputRoutes.RawThreadManager, true);
        _arms.Items.Add(OutputRoutes.CachedController, true);
        _arms.Items.Add(OutputRoutes.OverrideOnRawThread, true);
        foreach (var w in HapticsApi.Waveforms.Keys)
        {
            _waveform.Items.Add(w);
        }

        _waveform.SelectedItem = opt.Get("waveform") ?? "Click";
        if (_waveform.SelectedIndex < 0 && _waveform.Items.Count > 0)
        {
            _waveform.SelectedIndex = 0;
        }

        _window.Items.AddRange(["message", "hidden"]);
        _window.SelectedItem = opt.Get("window") ?? "message";
        _target.Items.AddRange(["mouse", "touchpad", "any"]);
        _target.SelectedItem = (opt.Get("target") ?? "mouse").ToLowerInvariant();
        _perArm.Value = Math.Clamp(opt.Int("trials-per-arm", 6), 3, 30);
        _controls.Value = Math.Clamp(opt.Int("controls", 6), 3, 30);
        _intensity.Value = (decimal)Math.Clamp(opt.Double("intensity", 1.0), 0.05, 1.0);

        _baseline.Text = "EXP-010 baseline is unverified until the target controller is captured. 'Not felt' requires the SAME controller, waveform and no higher baseline intensity to count as FAIL.";
        _baseline.ForeColor = Color.FromArgb(138, 80, 0);
        _baseline.MaximumSize = new Size(880, 0);

        var intro = Ui.Text(
            "1) Start: glide the TARGET device (mouse or touchpad, as selected) over the pad below for a second. This captures its controller for the R3 arms and identifies the device. " +
            "2) Begin: this window minimizes and a small non-activating panel appears bottom-right. Click into another app (e.g. Notepad) and keep " +
            "moving the pointer over it with the same device. When the panel says NOW, judge, then click Felt / Not felt / Unsure on the panel. The panel never takes focus.", width: 900);
        var setup = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8) };
        setup.Controls.AddRange([
            intro, Ui.Text("Routes:"), _arms, Ui.Text("Waveform:"), _waveform, Ui.Text("Intensity:"), _intensity,
            Ui.Text("Raw window:"), _window, Ui.Text("Target:"), _target, Ui.Text("Pulses/arm:"), _perArm, Ui.Text("Controls:"), _controls,
            _baseline, _start, _begin,
        ]);
        setup.SetFlowBreak(intro, true);
        setup.SetFlowBreak(_controls, true);
        _pad.Controls.Add(_padText);
        _padText.Text = HapticsApi.TypePresent ? "Configure, then Start." : "InputHapticsManager not present — BLOCKED.";
        _padText.MouseMove += (_, _) => OnPrimeMove();
        _pad.MouseMove += (_, _) => OnPrimeMove();
        Controls.Add(_pad);
        Controls.Add(_status);
        Controls.Add(setup);

        _start.Click += (_, _) => StartPrime();
        _begin.Click += (_, _) => BeginTrials();
        FormClosing += (_, _) =>
        {
            Finish(aborted: true);
            _raw?.Dispose();
            _panel?.Close();
            _outputOwnership?.Dispose();
            _outputOwnership = null;
        };
    }

    public string? ResultPath { get; private set; }

    private string TargetDevice => (string)_target.SelectedItem!;

    private void StartPrime()
    {
        if (!HapticsApi.TypePresent)
        {
            return;
        }

        _outputOwnership = OutputOwnership.Create("EXP-020");
        if (!_outputOwnership.TryAcquire())
        {
            _outputOwnership.Dispose();
            _outputOwnership = null;
            _status.Text = "Another Haptics Everywhere engine owns output. Stop it before running this physical experiment.";
            return;
        }

        _waveName = (string)_waveform.SelectedItem!;
        _intensityValue = (double)_intensity.Value;
        var kind = (string)_window.SelectedItem! == "hidden" ? MessageWindowKind.HiddenTopLevel : MessageWindowKind.MessageOnly;
        try
        {
            _raw = new MessageWindowThread("exp020-raw-input", kind, registerRawMouse: true);
            _raw.RawMouse += OnRaw;
            _raw.Start();
            _session = ExperimentSession.Start("EXP-020", "physical", _opt.Get("out"));
        }
        catch
        {
            _raw?.Dispose();
            _raw = null;
            _outputOwnership.Dispose();
            _outputOwnership = null;
            throw;
        }

        _session.Result.Method["outputOwner"] = _outputOwnership.Owner;
        _session.Result.Method["outputOwnershipGranted"] = _outputOwnership.IsOwner;
        _phase = Phase.Prime;
        _start.Enabled = false;
        _padText.Text = "Move the TARGET mouse around inside this pad…";
        _status.Text = _raw.RawInputRegistered
            ? $"Raw input registered (mouse only, RIDEV_INPUTSINK) on thread {_raw.ThreadId}, window kind {kind}."
            : $"Raw input registration FAILED (win32 {_raw.RawInputRegistrationError}).";
    }

    private void OnPrimeMove()
    {
        if (_phase != Phase.Prime)
        {
            return;
        }

        HapticDeviceType? require = TargetDevice switch { "mouse" => HapticDeviceType.Mouse, "touchpad" => HapticDeviceType.Touchpad, _ => null };
        var cap = HapticsApi.TryCaptureCurrentController(require);
        if (cap is not null)
        {
            _captured = cap;
        }

        var st = HapticsApi.InspectCurrentThread();
        _padText.Text = _captured is not null
            ? $"Controller captured ({_captured.DeviceType}, id {_captured.IdHash}, {_captured.SupportedWaveforms.Length} waveforms).\nNow press '2. Begin background trials'."
            : $"No {TargetDevice} controller yet (UI thread sees device={st.DeviceType}). Keep moving the {TargetDevice}…";
        _begin.Enabled = _captured is not null || TargetDevice == "any";
    }

    private void BeginTrials()
    {
        if (_phase != Phase.Prime)
        {
            return;
        }

        _begin.Enabled = false;
        var arms = _arms.CheckedItems.Cast<string>().ToList();
        var skipped = new List<string>();
        if (_captured is null)
        {
            skipped.AddRange(arms.Where(a => a != OutputRoutes.RawThreadManager));
            arms.RemoveAll(a => a != OutputRoutes.RawThreadManager);
        }

        if (arms.Count == 0)
        {
            _status.Text = "No runnable arms (R3 arms need a captured controller).";
            _begin.Enabled = true;
            return;
        }

        var m0 = _session!.Result.Method;
        lock (_primeDevices)
        {
            _targetLocked = TargetDevice != "any" && _primeDevices.Count > 0;
            _targetDevice = _targetLocked ? _primeDevices.MaxBy(kv => kv.Value).Key : 0;
            m0["primeRawDevices"] = PrimeDeviceCounts(_primeDevices, RawInputDevices.Describe);
        }

        var seed = _opt.Int("seed", Random.Shared.Next());
        _schedule = TrialSchedule.Build(arms, (int)_perArm.Value, (int)_controls.Value, seed);
        var m = _session!.Result.Method;
        m["arms"] = arms;
        m["armsSkipped"] = skipped;
        m["routeDescriptions"] = arms.ToDictionary(a => a, OutputRoutes.Describe);
        m["waveform"] = _waveName;
        m["intensity"] = _intensityValue;
        m["rawWindowKind"] = _window.SelectedItem;
        m["rawThreadId"] = _raw!.ThreadId;
        m["targetDevice"] = TargetDevice;
        m["targetControllerIdHash"] = _captured?.IdHash;
        m["targetRawDevice"] = !_targetLocked ? "any" : _targetDevice == 0 ? "no-handle (precision touchpad or synthetic)" : RawInputDevices.Describe(_targetDevice);
        if (_targetLocked && _targetDevice == 0)
        {
            _session.Result.Limitations.Add("Trial device delivered raw input without a device handle (typical of precision touchpads). " +
                "Such events cannot be distinguished from synthetic input by hDevice alone; no synthetic input source was running, and the raw-thread device type is recorded per trial.");
        }
        m["capturedController"] = _captured is null ? null : new
        {
            deviceType = _captured.DeviceType.ToString(),
            idHash = _captured.IdHash,
            capturedOnThread = _captured.CapturedOnThread,
            supportedWaveforms = _captured.SupportedWaveforms.Select(HapticsApi.WaveformName).ToList(),
            _captured.IsIntensitySupported,
        };
        m["seed"] = seed;
        _baselinePassed = EvidenceLedger.ForegroundBaselinePassed(EvidenceLedger.Load(), _session.Result.Machine.MachineFingerprint,
            _waveName, _intensityValue, _captured?.IdHash);
        m["baselinePassedForSameControllerWaveformAndIntensity"] = _baselinePassed;
        m["fireConditions"] = "foreground PID != lab, cursor not over lab panel, physical (hDevice != 0) movement from the primed device for >= 400ms after a random 0.8-2.5s delay; fired inside WM_INPUT handling on the raw-input thread.";
        _session.Result.NegativeControls.Add($"{(int)_controls.Value} randomized silent controls fired under identical conditions.");

        _phase = Phase.Trials;
        _panel = new AnswerPanel(OnAnswer, () =>
        {
            if (_phase == Phase.Done)
            {
                _panel?.Close();
            }
            else
            {
                Finish(aborted: true);
            }
        });
        _panel.Show();
        _panelBounds = _panel.Bounds;
        WindowState = FormWindowState.Minimized;
        _next = 0;
        ArmNext();
    }

    internal sealed record PrimeDeviceCount(string NameHash, string? VidPid, string Connection, int Events);

    internal static IReadOnlyList<PrimeDeviceCount> PrimeDeviceCounts(IReadOnlyDictionary<long, int> devices,
        Func<long, RawDeviceInfo> describe) =>
        devices.Select(kv =>
        {
            var device = describe(kv.Key);
            return new PrimeDeviceCount(device.NameHash, device.VidPid, device.Connection, kv.Value);
        }).OrderBy(d => d.NameHash, StringComparer.Ordinal).ToArray();

    private void ArmNext()
    {
        if (_next >= _schedule.Count)
        {
            Finish(aborted: false);
            return;
        }

        var plan = _schedule[_next];
        _panel!.SetAnswersEnabled(false);
        _panel.SetMessage($"Trial {_next + 1}/{_schedule.Count}: keep the OTHER app in front and keep moving the mouse over it. Wait for NOW…");
        var bounds = _panel.Bounds;
        _raw!.Post(() =>
        {
            _panelBounds = bounds;
            _pending = plan;
            _armedAt = MonotonicClock.Instance.NowMs;
            _delay = 800 + (_rng.NextDouble() * 1700);
            _foreignSince = -1;
        });
    }

    // ---- raw-input thread ----
    private void OnRaw(RawMouseInput input)
    {
        var key = $"{input.Code}/{(input.DeviceHandle == 0 ? "synthetic" : "device")}";
        lock (_rawCounts)
        {
            _rawCounts[key] = _rawCounts.GetValueOrDefault(key) + 1;
        }

        // Count every prime-phase event, including hDevice == 0: precision touchpads may deliver mouse raw input
        // without a device handle, and the device actually used during priming becomes the trial device.
        if (_phase == Phase.Prime && (input.LastX != 0 || input.LastY != 0))
        {
            lock (_primeDevices)
            {
                _primeDevices[input.DeviceHandle] = _primeDevices.GetValueOrDefault(input.DeviceHandle) + 1;
            }
        }

        var now = MonotonicClock.Instance.NowMs;
        if (_phase == Phase.Trials && now - _lastSample > 250)
        {
            _lastSample = now;
            var st = HapticsApi.InspectCurrentThread();
            lock (_rawThreadSamples)
            {
                _rawThreadSamples.Add(new()
                {
                    ["code"] = input.Code.ToString(),
                    ["origin"] = input.Origin.ToString(),
                    ["managerOk"] = st.ManagerOk,
                    ["controllerPresent"] = st.ControllerPresent,
                    ["deviceType"] = st.DeviceType,
                });
            }
        }

        if (_phase != Phase.Trials || _pending is not { } plan)
        {
            return;
        }

        if (_targetLocked && input.DeviceHandle != _targetDevice)
        {
            return;
        }

        var fg = DesktopContext.Foreground().Pid;
        var (cx, cy) = DesktopContext.CursorPosition();
        var moving = input.LastX != 0 || input.LastY != 0;
        var provenanceOk = input.Origin == InputOrigin.HardwareRaw || (_targetLocked && _targetDevice == 0);
        if (fg == DesktopContext.OwnProcessId || fg == 0 || _panelBounds.Contains(cx, cy) || !provenanceOk)
        {
            _foreignSince = -1;
            return;
        }

        if (_foreignSince < 0)
        {
            _foreignSince = now;
        }

        if (!moving || now - _armedAt < _delay || now - _foreignSince < 400)
        {
            return;
        }

        _pending = null;
        Fire(plan, input, fg, cx, cy, now);
    }

    private void Fire(ScheduledTrial plan, RawMouseInput input, int fg, int cx, int cy, double now)
    {
        var under = DesktopContext.TopLevelAt(cx, cy).Pid;
        var st = HapticsApi.InspectCurrentThread();
        var t = new TrialRecord { Index = plan.Index, Arm = plan.Arm, IsControl = plan.IsControl, FiredAtMs = now };
        _outputOwnership?.Record(t);
        t.Context["rawCode"] = input.Code.ToString();
        t.Context["origin"] = input.Origin.ToString();
        t.Context["foregroundIsLab"] = fg == DesktopContext.OwnProcessId;
        t.Context["cursorWindowIsLab"] = under == DesktopContext.OwnProcessId;
        t.Context["rawThreadManagerOk"] = st.ManagerOk;
        t.Context["rawThreadControllerPresent"] = st.ControllerPresent;
        t.Context["rawThreadDeviceType"] = st.DeviceType;
        t.Context["msSinceArmed"] = Math.Round(now - _armedAt, 0);
        t.Context["labels"] = new List<string> { EvidenceLabels.RawReceived };
        if (input.Code != RawInputCode.Background)
        {
            t.Invalidate("raw-input-code-was-foreground");
        }

        if (under == DesktopContext.OwnProcessId)
        {
            t.Invalidate("cursor-over-lab-window");
        }

        if (!plan.IsControl && _outputOwnership?.TryRun(() =>
            {
                t.Attempt = plan.Arm switch
                {
                    OutputRoutes.RawThreadManager => HapticsApi.SendViaThreadManager(plan.Arm, _waveName, _intensityValue, input.TimestampMs),
                    OutputRoutes.CachedController => HapticsApi.SendViaController(plan.Arm, _captured, _waveName, _intensityValue, input.TimestampMs),
                    OutputRoutes.OverrideOnRawThread => HapticsApi.SendViaOverride(plan.Arm, _captured, _waveName, _intensityValue, input.TimestampMs),
                    _ => null,
                };
            }) != true)
        {
            t.Invalidate("output-owner-not-held");
        }

        BeginInvoke(() =>
        {
            if (_phase != Phase.Trials)
            {
                return;
            }

            _awaiting = t;
            _panel?.SetMessage($"NOW — trial {plan.Index}. Did you feel a pulse?", Color.FromArgb(200, 120, 0));
            _panel?.SetAnswersEnabled(true);
        });
    }

    // ---- UI thread ----
    private void OnAnswer(TrialAnswer a)
    {
        if (_phase != Phase.Trials || _awaiting is null)
        {
            return;
        }

        _awaiting.Answer = a;
        _awaiting.AnsweredAtMs = MonotonicClock.Instance.NowMs;
        _awaiting.Context["foregroundIsLabAtAnswer"] = DesktopContext.Foreground().Pid == DesktopContext.OwnProcessId;
        _trials.Add(_awaiting);
        _session!.Artifacts.Trace(new { type = "trial", trial = _awaiting });
        _awaiting = null;
        _next++;
        ArmNext();
    }

    internal static bool ArmBaselinePassed(IEnumerable<TrialRecord> trials, string arm, string? targetControllerIdHash,
        bool targetBaselinePassed)
    {
        var pulses = trials.Where(t => t.ProvenanceValid && t.Answer != TrialAnswer.None && !t.IsControl && t.Arm == arm).ToArray();
        return targetBaselinePassed && EvidenceLedger.KnownControllerIdentity(targetControllerIdHash) &&
               pulses.Length > 0 && pulses.All(t => t.Attempt?.ControllerIdHash == targetControllerIdHash);
    }

    private void Finish(bool aborted)
    {
        if (_session is null || _phase == Phase.Done)
        {
            return;
        }

        _outputOwnership?.StopSending();
        _phase = Phase.Done;
        _raw?.Post(() => _pending = null);
        var r = _session.Result;
        var verdicts = new Dictionary<string, ArmVerdict>();
        var baselines = new Dictionary<string, bool>();
        foreach (var arm in _schedule.Where(x => !x.IsControl).Select(x => x.Arm).Distinct())
        {
            baselines[arm] = ArmBaselinePassed(_trials, arm, _captured?.IdHash, _baselinePassed);
            verdicts[arm] = PhysicalEvaluator.Evaluate(arm, _trials, baselines[arm]);
        }

        r.Method["baselinePassedByRoute"] = baselines;
        r.Routes = verdicts;
        (r.Status, r.EvidenceClass) = PhysicalEvaluator.Combine(verdicts.Values);
        foreach (var label in verdicts.Values.SelectMany(v => v.Labels).Distinct())
        {
            r.AddLabel(label);
        }

        List<Dictionary<string, object?>> samples;
        lock (_rawThreadSamples)
        {
            samples = _rawThreadSamples.ToList();
        }

        var handleless = _targetLocked && _targetDevice == 0;
        var bgSamples = samples.Where(x => (string?)x["code"] == "Background" && (handleless || (string?)x["origin"] == "HardwareRaw")).ToList();
        var bgWithController = bgSamples.Count(x => x["controllerPresent"] is true);
        if (bgSamples.Count > 0)
        {
            r.AddLabel(EvidenceLabels.RawReceived);
        }

        if (bgWithController > 0)
        {
            r.AddLabel(EvidenceLabels.CurrentControllerPresent);
        }

        var attempts = _trials.Where(t => t.Attempt is not null).Select(t => t.Attempt!).ToList();
        lock (_rawCounts)
        {
            r.Observations["rawEventCounts"] = new Dictionary<string, long>(_rawCounts);
        }

        r.Observations["rawThreadSamples"] = samples.Count;
        r.Observations["rawThreadBackgroundSamples"] = bgSamples.Count;
        r.Observations["rawThreadBackgroundSamplesWithController"] = bgWithController;
        r.Observations["rawThreadBackgroundDeviceTypes"] = bgSamples.GroupBy(x => x["deviceType"]?.ToString() ?? "?").ToDictionary(g => g.Key, g => g.Count());
        r.Observations["apiAcceptedByRoute"] = attempts.GroupBy(a => a.Route).ToDictionary(g => g.Key, g => $"{g.Count(a => a.Accepted)}/{g.Count()}");
        r.Observations["apiErrors"] = attempts.Where(a => a.Error is not null).Select(a => $"{a.Route}: {a.Error}").Distinct().ToList();
        r.Observations["trialsAnswered"] = _trials.Count;
        r.Observations["trialsScheduled"] = _schedule.Count;
        r.Observations["invalidTrials"] = _trials.Where(t => !t.ProvenanceValid).Select(t => $"#{t.Index}: {string.Join(",", t.ProvenanceIssues)}").ToList();
        r.Observations["trials"] = _trials;
        r.LatencyMs["inputToCallP50"] = Pct(attempts.Select(a => a.InputToCallMs ?? double.NaN), 0.5);
        r.LatencyMs["inputToCallP95"] = Pct(attempts.Select(a => a.InputToCallMs ?? double.NaN), 0.95);
        r.Explanation = (aborted ? "ABORTED early. " : "") +
                        $"Raw thread saw a controller in {bgWithController}/{bgSamples.Count} background samples. " +
                        string.Join(" | ", verdicts.Select(v => $"{v.Key}: {v.Value.Status} ({v.Value.Hits}/{v.Value.Hits + v.Value.Misses} felt, controls {v.Value.FalseAlarms}/{v.Value.FalseAlarms + v.Value.CorrectRejections}, api {v.Value.ApiAccepted} accepted) — {v.Value.Reason}"));
        r.Limitations.Add("One machine, one mouse, one OS build. A PASS is scoped to this fingerprint and route only.");
        r.Limitations.Add("R3 arms reuse a controller captured from our own foreground interaction; its lifetime across sleep/re-pair is untested (EXP-082).");
        r.NextDecision = r.Status switch
        {
            ExperimentStatus.PASS => "A background route is physically confirmed: the tray may enable 'On' with that route. Next: EXP-070 live, EXP-075, EXP-080.",
            ExperimentStatus.FAIL => "No background route works with a passing foreground baseline: output architecture pivot (SPEC §15).",
            _ => "Inconclusive: check device/answers; run EXP-010 first if it has not passed; rerun with more trials.",
        };
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
        void ShowDone()
        {
            _panel?.SetAnswersEnabled(false);
            _panel?.SetMessage($"Done: {r.Status}. Open the minimized 'EXP-020' window for details. Click Stop to close this panel.", Ui.StatusColor(r.Status));

            // Deliberately not restoring the window: SW_RESTORE would activate it and take focus from the app under test.
            _pad.BackColor = Ui.StatusColor(r.Status);
            _padText.Font = Ui.Body;
            _padText.Text = $"{r.Status} [{r.EvidenceClass}]\n\n{r.Explanation}";
            _status.Text = $"Saved {ResultPath}";
        }

        try
        {
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(ShowDone);
            }
        }
        catch (InvalidOperationException)
        {
            // Window is closing.
        }
    }

    private static double? Pct(IEnumerable<double> values, double p)
    {
        var a = values.Where(v => !double.IsNaN(v)).Order().ToArray();
        return a.Length == 0 ? null : Math.Round(a[Math.Clamp((int)Math.Ceiling(p * a.Length) - 1, 0, a.Length - 1)], 2);
    }
}
