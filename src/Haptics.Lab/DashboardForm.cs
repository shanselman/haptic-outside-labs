using System.Collections.Concurrent;
using System.Diagnostics;
using Haptics.Core;
using Haptics.Windows;
using Windows.Devices.Haptics;

namespace Haptics.Lab;

/// <summary>
/// One discoverable lab surface (SPEC §3.2, §13.4). Experiments run as child processes so a hung probe
/// cannot freeze the dashboard. The dashboard itself never calls UIA on its UI thread.
/// </summary>
public sealed class DashboardForm : Form
{
    private readonly string _fp = EnvironmentProbe.CurrentFingerprint();
    private readonly ListView _gates = NewList(("Gate", 330), ("State", 190), ("Detail", 560));
    private readonly ListView _experiments = NewList(("Id", 80), ("Title", 360), ("Mode", 80), ("Impl", 50), ("Latest (this machine)", 150), ("Summary", 420));
    private readonly ListView _runs = NewList(("When (UTC)", 130), ("Experiment", 90), ("Status", 120), ("Class", 90), ("Explanation", 680));
    private readonly ListView _events = NewList(("t (ms)", 80), ("Event", 140), ("Control", 100), ("Outcome", 90), ("Reason", 230), ("Waveform", 80), ("Detail", 360));
    private readonly Label _next = new() { AutoSize = true, Font = Ui.Big, Margin = new Padding(8) };
    private readonly Label _running = new() { AutoSize = true, Margin = new Padding(8), ForeColor = Color.DimGray };
    private readonly Label _banner = new() { Dock = DockStyle.Top, Height = 34, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0), Font = new Font("Segoe UI Semibold", 10f) };
    private readonly ConcurrentQueue<PipelineEvent> _pending = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 100 };
    private ObservationPipeline? _pipeline;
    private Label? _liveStats;
    private ComboBox? _route;
    private ComboBox? _mode;
    private ComboBox? _profile;
    private CheckBox? _allowUnverified;
    private Label? _primeState;
    private CapturedController? _captured;
    private string? _nextId;
    private double _startMs;

    public DashboardForm()
    {
        Text = "Haptics Lab — Haptics Everywhere";
        ClientSize = new Size(1180, 760);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Ui.Body;
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(GatesPage());
        tabs.TabPages.Add(ExperimentsPage());
        tabs.TabPages.Add(ResultsPage());
        tabs.TabPages.Add(LivePage());
        tabs.TabPages.Add(WaveformsPage());
        Controls.Add(tabs);
        Controls.Add(_banner);
        Activated += (_, _) => RefreshAll();
        _uiTimer.Tick += (_, _) => DrainEvents();
        _uiTimer.Start();
        FormClosing += (_, _) => _pipeline?.Dispose();
        RefreshAll();
    }

    private static ListView NewList(params (string Name, int Width)[] cols)
    {
        var lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, Font = Ui.Body };
        foreach (var (n, w) in cols)
        {
            lv.Columns.Add(n, w);
        }

        return lv;
    }

    private TabPage GatesPage()
    {
        var page = new TabPage("Gates");
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 64, Padding = new Padding(4) };
        var run = new Button { Text = "Run next experiment", AutoSize = true, Margin = new Padding(8, 14, 8, 8) };
        run.Click += (_, _) =>
        {
            if (_nextId is not null)
            {
                Launch(_nextId);
            }
        };
        var report = new Button { Text = "Write report", AutoSize = true, Margin = new Padding(8, 14, 8, 8) };
        report.Click += (_, _) =>
        {
            var (_, html) = Report.Write();
            Process.Start(new ProcessStartInfo(html) { UseShellExecute = true });
        };
        top.Controls.AddRange([_next, run, report, _running]);
        var unknowns = new TextBox
        {
            Dock = DockStyle.Bottom,
            Height = 170,
            Multiline = true,
            ReadOnly = true,
            Font = Ui.Body,
            Text = "What remains unknown (SPEC §2.2, §20.1):\r\n" +
                   "• H1 Does background WM_INPUT give our raw-input thread a haptic route to the mouse?  → EXP-020 arm R2\r\n" +
                   "• H2 Can a controller captured in a foreground moment drive the mouse while another app is foreground?  → EXP-020 arms R3a/R3b\r\n" +
                   "• H3/H4 Are UIA semantics fast and broad enough on real apps?  → EXP-040 (fixture, automated) then Live Observer over real apps\r\n" +
                   "• H5 Is it timely and unobtrusive?  H7 Can native double-buzz be avoided?  H8 Is it useful?  → need a passing A2 gate first\r\n" +
                   "Nothing in this window treats an API 'true' as a felt pulse. Only blinded operator answers produce PHYSICAL evidence.",
        };
        page.Controls.Add(_gates);
        page.Controls.Add(unknowns);
        page.Controls.Add(top);
        return page;
    }

    private TabPage ExperimentsPage()
    {
        var page = new TabPage("Experiments");
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(4) };
        var run = new Button { Text = "Run selected (child process)", AutoSize = true };
        run.Click += (_, _) =>
        {
            if (_experiments.SelectedItems.Count > 0)
            {
                Launch(_experiments.SelectedItems[0].Text);
            }
        };
        var fixture = new Button { Text = "Open fixture", AutoSize = true };
        fixture.Click += (_, _) => StartChild(["fixture"], null);
        bar.Controls.AddRange([run, fixture, Ui.Text("Physical experiments open their own window. Automated ones never move the pointer or send haptics.")]);
        _experiments.DoubleClick += (_, _) => run.PerformClick();
        page.Controls.Add(_experiments);
        page.Controls.Add(bar);
        return page;
    }

    private TabPage ResultsPage()
    {
        var page = new TabPage("Results");
        _runs.DoubleClick += (_, _) =>
        {
            if (_runs.SelectedItems.Count > 0 && _runs.SelectedItems[0].Tag is string dir)
            {
                var summary = Path.Combine(dir, "summary.md");
                Process.Start(new ProcessStartInfo(File.Exists(summary) ? summary : dir) { UseShellExecute = true });
            }
        };
        page.Controls.Add(_runs);
        page.Controls.Add(new Label { Dock = DockStyle.Top, Height = 28, Text = $"Local evidence: {ArtifactLocator.RunsRoot()}  (double-click a run to open its summary)", Padding = new Padding(6) });
        return page;
    }

    private TabPage LivePage()
    {
        var page = new TabPage("Live observer");
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 84, Padding = new Padding(4) };
        var start = new Button { Text = "Start", AutoSize = true };
        var stop = new Button { Text = "Stop", AutoSize = true, Enabled = false };
        _mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        _mode.Items.AddRange([HapticsMode.ObserveOnly, HapticsMode.On]);
        _mode.SelectedIndex = 0;
        _profile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        _profile.Items.AddRange([EffectProfile.Quiet, EffectProfile.Balanced, EffectProfile.Experimental]);
        _profile.SelectedItem = EffectProfile.Balanced;
        _route = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
        _route.Items.Add(OutputRoutes.DryRun);
        foreach (var r in OutputRoutes.Background)
        {
            _route.Items.Add(r);
        }

        _route.SelectedIndex = 0;
        _allowUnverified = new CheckBox { Text = "Allow an unverified route (experiment, not evidence)", AutoSize = true };
        var prime = new Panel { Width = 170, Height = 40, BackColor = Color.FromArgb(36, 40, 52), Cursor = Cursors.Cross };
        _primeState = new Label { Text = "Move mouse here to capture controller (R3 routes)", ForeColor = Color.White, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 8f) };
        prime.Controls.Add(_primeState);
        _primeState.MouseMove += (_, _) =>
        {
            var c = HapticsApi.TryCaptureCurrentController();
            if (c is not null)
            {
                _captured = c;
                _primeState.Text = $"Captured {c.DeviceType} ({c.IdHash})";
                if (_pipeline is not null)
                {
                    _pipeline.Controller = c;
                }
            }
        };
        _liveStats = new Label { AutoSize = true, Font = Ui.Mono, Margin = new Padding(8) };
        start.Click += (_, _) =>
        {
            StartLive();
            start.Enabled = false;
            stop.Enabled = true;
        };
        stop.Click += (_, _) =>
        {
            _pipeline?.Dispose();
            _pipeline = null;
            start.Enabled = true;
            stop.Enabled = false;
        };
        _mode.SelectedIndexChanged += (_, _) => ApplyLiveSettings();
        _profile.SelectedIndexChanged += (_, _) => ApplyLiveSettings();
        _route.SelectedIndexChanged += (_, _) => ApplyLiveSettings();
        _allowUnverified.CheckedChanged += (_, _) => ApplyLiveSettings();
        bar.Controls.AddRange([start, stop, Ui.Text("Mode:"), _mode, Ui.Text("Profile:"), _profile, Ui.Text("Route:"), _route, _allowUnverified, prime]);
        _liveStats.Dock = DockStyle.Top;
        _liveStats.AutoSize = false;
        _liveStats.Height = 24;
        _liveStats.Padding = new Padding(6, 4, 0, 0);
        page.Controls.Add(_events);
        page.Controls.Add(_liveStats);
        page.Controls.Add(bar);
        page.Controls.Add(new Label
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            Padding = new Padding(6),
            Text = "Passive: mouse-only Raw Input + read-only UI Automation in an isolated worker process. Only control types/bounds are read — never names, text, or values (except slider range). Nothing is persisted from this view.",
        });
        return page;
    }

    private TabPage WaveformsPage()
    {
        var page = new TabPage("Waveform playground");
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        var intensity = new NumericUpDown { Minimum = 0.05m, Maximum = 1m, Increment = 0.05m, DecimalPlaces = 2, Value = 0.6m, Width = 70 };
        var last = new Label { AutoSize = true, Font = Ui.Mono, Margin = new Padding(8) };
        var hoverMode = new CheckBox { Text = "Send on hover (instead of click)", AutoSize = true };
        flow.Controls.Add(Ui.Text("Exploration only — NOT evidence. These use the documented foreground pattern (R1) on this window's UI thread. Use EXP-010 for blinded proof.", width: 1100));
        flow.Controls.Add(Ui.Text("Intensity:"));
        flow.Controls.Add(intensity);
        flow.Controls.Add(hoverMode);
        flow.SetFlowBreak(hoverMode, true);
        foreach (var w in HapticsApi.Waveforms.Keys)
        {
            var b = new Button { Text = w, Width = 100, Height = 48 };
            void Send()
            {
                var a = HapticsApi.SendViaThreadManager(OutputRoutes.ForegroundManager, w, (double)intensity.Value);
                last.Text = $"{w}: accepted={a.Accepted} device={a.DeviceType} controller={a.ControllerPresent} {a.Error}";
            }

            b.Click += (_, _) => Send();
            b.MouseEnter += (_, _) =>
            {
                if (hoverMode.Checked)
                {
                    Send();
                }
            };
            flow.Controls.Add(b);
        }

        var slider = new TrackBar { Minimum = 0, Maximum = 100, TickFrequency = 10, LargeChange = 10, Width = 600 };
        var prevTick = 0;
        slider.ValueChanged += (_, _) =>
        {
            var tick = slider.Value / 10;
            var wave = slider.Value is 0 or 100 ? "Collide" : tick != prevTick ? "Step" : null;
            prevTick = tick;
            if (wave is not null)
            {
                var a = HapticsApi.SendViaThreadManager(OutputRoutes.ForegroundManager, wave, (double)intensity.Value);
                last.Text = $"slider {slider.Value}: {wave} accepted={a.Accepted} device={a.DeviceType}";
            }
        };
        flow.SetFlowBreak(flow.Controls[^1], true);
        flow.Controls.Add(Ui.Text("Detent slider (Step every 10, Collide at the ends — the Microsoft sample pattern):"));
        flow.Controls.Add(slider);
        flow.SetFlowBreak(slider, true);
        flow.Controls.Add(last);
        page.Controls.Add(flow);
        return page;
    }

    private void StartLive()
    {
        var cfg = new PolicyConfig { Mode = (HapticsMode)_mode!.SelectedItem!, AppPolicy = AppPolicy.AllExceptExclusions };
        cfg.ApplyProfile((EffectProfile)_profile!.SelectedItem!);
        _pipeline = new ObservationPipeline(cfg, new PipelineOptions { Route = (string)_route!.SelectedItem! });
        _pipeline.Policy.ConsentGiven = true;
        _pipeline.Controller = _captured;
        _pipeline.Event += e => _pending.Enqueue(e);
        _startMs = MonotonicClock.Instance.NowMs;
        ApplyLiveSettings();
        _pipeline.Start();
    }

    private void ApplyLiveSettings()
    {
        if (_pipeline is null)
        {
            return;
        }

        var verified = EvidenceLedger.VerifiedBackgroundRoutes(EvidenceLedger.Load(), _fp);
        var route = (string)_route!.SelectedItem!;
        _pipeline.Route = route;
        _pipeline.Policy.Config.Mode = (HapticsMode)_mode!.SelectedItem!;
        _pipeline.Policy.Config.ApplyProfile((EffectProfile)_profile!.SelectedItem!);
        _pipeline.Policy.OutputRouteVerified = route != OutputRoutes.DryRun && (verified.Contains(route) || _allowUnverified!.Checked);
    }

    private void DrainEvents()
    {
        if (_pipeline is not null && _liveStats is not null)
        {
            var s = _pipeline.Stats();
            _liveStats.Text = $"raw {s.RawEvents} (bg {s.BackgroundRawEvents})  uia p50 {s.LookupP50:0.0}/p95 {s.LookupP95:0.0}ms  emit {s.Emits} dry {s.DryRuns} supp {s.Suppressed}  api ok {s.ApiAccepted}/rej {s.ApiRejected} expired {s.Expired}  in→call p95 {s.InputToCallP95:0.0}ms";
        }

        if (_pending.IsEmpty)
        {
            return;
        }

        _events.BeginUpdate();
        var n = 0;
        while (n++ < 300 && _pending.TryDequeue(out var e))
        {
            ListViewItem? item = e switch
            {
                DecisionEvent d when d.Decision.Reason != "no-haptic-for-event" => Row(e.AtMs, d.Decision.Event.Kind.ToString(), d.Decision.Event.ControlType, d.Decision.Outcome.ToString(), d.Decision.Reason, d.Decision.Intent?.Waveform, $"conf {d.Decision.Event.Confidence:0.00} speed {d.Decision.Event.PointerSpeedPxPerSec:0}px/s"),
                OutputEvent o => Row(e.AtMs, "Output", "", o.Status, o.Attempt?.Route ?? "", o.Intent.Waveform, o.Attempt is null ? "" : $"device {o.Attempt.DeviceType} in→call {o.Attempt.InputToCallMs:0.0}ms"),
                DiagnosticEvent g => Row(e.AtMs, "Diagnostic", "", "", g.Message, null, ""),
                _ => null,
            };
            if (item is not null)
            {
                _events.Items.Insert(0, item);
            }
        }

        while (_events.Items.Count > 500)
        {
            _events.Items.RemoveAt(_events.Items.Count - 1);
        }

        _events.EndUpdate();
    }

    private ListViewItem Row(double at, string a, string b, string c, string d, string? w, string detail) =>
        new([$"{at - _startMs:0}", a, b, c, d, w ?? "", detail]);

    private void Launch(string id) => StartChild(["run", id], id);

    private void StartChild(string[] args, string? id)
    {
        var exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
        }

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        var p = Process.Start(psi)!;
        var info = id is null ? null : ExperimentCatalog.Find(id);
        _running.Text = $"running {string.Join(' ', args)} (pid {p.Id})";
        p.EnableRaisingEvents = true;
        p.Exited += (_, _) => BeginInvoke(() =>
        {
            _running.Text = $"{string.Join(' ', args)} exited ({p.ExitCode})";
            RefreshAll();
        });

        if (info is { NeedsHuman: false })
        {
            // Watchdog: automated experiments must finish; a hung probe is killed without touching the dashboard.
            _ = Task.Delay(TimeSpan.FromMinutes(3)).ContinueWith(_ =>
            {
                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Gone.
                }
            });
        }
    }

    private void RefreshAll()
    {
        var results = EvidenceLedger.Load();
        _gates.BeginUpdate();
        _gates.Items.Clear();
        foreach (var g in EvidenceLedger.Gates(results, _fp))
        {
            _gates.Items.Add(new ListViewItem([g.Gate, g.State, g.Detail]));
        }

        _gates.EndUpdate();
        var next = EvidenceLedger.NextExperiment(results, _fp);
        _nextId = next?.Id;
        _next.Text = next is null ? "All implemented experiments have passed here." : $"Next: {next.Id} — {next.Title}";

        _experiments.BeginUpdate();
        _experiments.Items.Clear();
        foreach (var e in ExperimentCatalog.All)
        {
            var latest = EvidenceLedger.Latest(results, e.Id, _fp)?.Result.Status.ToString() ?? "NOT_RUN";
            var item = new ListViewItem([e.Id, e.Title, e.Mode, e.Implemented ? "yes" : "no", latest, e.Summary]);
            if (!e.Implemented)
            {
                item.ForeColor = Color.Gray;
            }

            _experiments.Items.Add(item);
        }

        _experiments.EndUpdate();
        _runs.BeginUpdate();
        _runs.Items.Clear();
        foreach (var r in results.AsEnumerable().Reverse())
        {
            var item = new ListViewItem([$"{r.Result.EndedAtUtc:yyyy-MM-dd HH:mm:ss}", r.Result.ExperimentId, r.Result.Status.ToString(), r.Result.EvidenceClass.ToString(), r.Result.Explanation])
            {
                Tag = r.Directory,
                ForeColor = Ui.StatusColor(r.Result.Status),
            };
            _runs.Items.Add(item);
        }

        _runs.EndUpdate();
        var routes = EvidenceLedger.VerifiedBackgroundRoutes(results, _fp);
        var physical = results.Any(r => r.Result.EvidenceClass == EvidenceClass.PHYSICAL && r.Result.Machine.MachineFingerprint == _fp);
        _banner.Text = routes.Count > 0
            ? $"Background haptic output CONFIRMED on this machine via {string.Join(", ", routes)}."
            : physical ? "Physical evidence exists, but no background route is confirmed yet. Tray output stays locked."
            : "NOT_TESTED_ON_HARDWARE: no physical evidence on this machine yet. Haptic output is UNVERIFIED.";
        _banner.BackColor = routes.Count > 0 ? Color.FromArgb(223, 246, 221) : Color.FromArgb(255, 244, 206);
    }
}
