using System.Collections.Concurrent;
using System.Diagnostics;
using Haptics.Core;
using Haptics.Lab.Experiments;
using Haptics.Windows;

namespace Haptics.Lab;

/// <summary>
/// Haptics Everywhere Showcase: feel the vocabulary, try it on real controls, see why each tick happened (or didn't),
/// compare styles blind. The engine is the same one the tray uses. The research lab lives in a quiet tab.
/// </summary>
public sealed class ShowcaseForm : Form, IExperimentForm, IMessageFilter
{
    private const int WM_MOUSEMOVE = 0x0200;

    private static readonly (string Name, string Waveform, string Meaning, bool AppsOnly)[] Vocabulary =
    [
        ("Land", "Hover", "You landed on something you can do: a button, menu command, checkbox, link, taskbar button.", false),
        ("Land · big", "Click", "A large target, like a ribbon's big buttons. A firmer landing.", false),
        ("Detent", "Step", "A new choice or region: entering a toolbar, ribbon group, menu or tab strip; another tab or option; slider ticks.", false),
        ("Edge", "Collide", "End of the line: sliding off the end of a strip, or a slider reaching its limit.", false),
        ("Align", "Align", "Apps only: a snap guide appeared. Sent by cooperating apps over the local pipe.", true),
        ("Success", "Success", "Apps only: an outcome the app knows (saved, dropped, sent).", true),
        ("Error", "Error", "Apps only: an outcome the app knows (rejected, failed).", true),
    ];

    private readonly string _fp = EnvironmentProbe.CurrentFingerprint();
    private readonly ControllerBank _bank = new();
    private readonly ConcurrentQueue<PipelineEvent> _events = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 100 };
    private readonly ToolTip _tips = new();
    private readonly Label _status = new() { AutoSize = true, Font = Ui.Body, Margin = new Padding(12, 10, 12, 4) };
    private readonly Label _intensityValue = new() { Text = "80%", AutoSize = true, Margin = new Padding(0, 6, 8, 4) };
    private readonly ComboBox _style = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly TrackBar _intensity = new() { Minimum = 1, Maximum = 10, Value = 8, Width = 160, TickFrequency = 1 };
    private readonly CheckBox _boost = new() { Text = "Boost touchpad", Checked = true, AutoSize = true };
    private readonly CheckBox _engineOn = new() { Text = "Engine on", Checked = true, AutoSize = true };
    private readonly Button _compare = new() { Text = "Compare styles (blind)…", AutoSize = true };
    private readonly ListBox _felt = new() { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 11f), IntegralHeight = false, BorderStyle = BorderStyle.None };
    private readonly ListView _inspector = NewList(("t (ms)", 80), ("Event", 140), ("Control", 110), ("Outcome", 90), ("Reason", 240), ("Waveform", 80), ("Detail", 360));
    private readonly Label _stats = new() { Dock = DockStyle.Top, Height = 26, Font = Ui.Mono, Padding = new Padding(6, 6, 0, 0) };
    private readonly ListView _gates = NewList(("Gate", 330), ("State", 190), ("Detail", 560));
    private readonly ListView _experiments = NewList(("Id", 80), ("Title", 360), ("Mode", 80), ("Impl", 50), ("Latest (this machine)", 150), ("Summary", 420));
    private readonly ListView _runs = NewList(("When (UTC)", 130), ("Experiment", 90), ("Status", 120), ("Class", 90), ("Explanation", 680));
    private readonly bool _startCompare;
    private ObservationPipeline? _pipeline;
    private CooperativeServer? _cooperation;
    private double _lastPrime;
    private double _startMs;
    private CompareRun? _compareRun;

    public ShowcaseForm(bool startCompare = false)
    {
        _startCompare = startCompare;
        Text = "Haptics Everywhere — Showcase";
        ClientSize = new Size(LogicalToDeviceUnits(1280), LogicalToDeviceUnits(860));
        StartPosition = FormStartPosition.CenterScreen;
        Font = Ui.Body;
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(ShowcasePage());
        tabs.TabPages.Add(InspectorPage());
        tabs.TabPages.Add(LabPage());
        Controls.Add(tabs);
        Controls.Add(Header());
        _uiTimer.Tick += (_, _) => Drain();
        _uiTimer.Start();
        Application.AddMessageFilter(this);
        Shown += (_, _) => OnShownCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.RemoveMessageFilter(this);
            _uiTimer.Dispose();
            _tips.Dispose();
            _pipeline?.Dispose();
            _cooperation?.Dispose();
        }

        base.Dispose(disposing);
    }

    public string? ResultPath { get; private set; }

    private EffectProfile SelectedStyle => (EffectProfile)(_style.SelectedIndex < 0 ? 1 : _style.SelectedIndex);

    private double Intensity => _intensity.Value / 10.0;

    // Any mouse/touchpad movement over any of our windows primes that device ("whatever haptic device is wiggled").
    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg == WM_MOUSEMOVE)
        {
            var now = MonotonicClock.Instance.NowMs;
            if (now - _lastPrime > 200)
            {
                _lastPrime = now;
                if (_bank.TryCapture(_pipeline?.LastDeviceHandle ?? 0))
                {
                    _bank.ApplyTo(_pipeline);
                    UpdateStatus();
                }
            }
        }

        return false;
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

    private Control Header()
    {
        var p = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 6, 8, 2), BackColor = Color.FromArgb(243, 246, 250) };
        p.Controls.Add(new Label { Text = "Haptics Everywhere", Font = new Font("Segoe UI Semibold", 18f), AutoSize = true, Margin = new Padding(4, 2, 16, 2) });
        _style.Items.AddRange(["Quiet", "Focused", "Everything"]);
        _style.SelectedIndex = 1;
        _tips.SetToolTip(_style, "Quiet: structure only (groups, tabs, edges, sliders)\nFocused: plus a tick when you land on a control\nEverything: every crossing (dense)");
        _intensity.AccessibleName = "Haptic intensity";
        _tips.SetToolTip(_intensity, "Start low and increase until the sensations are comfortable.");
        _tips.SetToolTip(_boost, "Touchpads usually feel lighter than mice. Boost adds 30% for touchpads.");
        _style.SelectedIndexChanged += (_, _) => ApplySettings();
        _intensity.ValueChanged += (_, _) =>
        {
            _intensityValue.Text = $"{_intensity.Value * 10}%";
            ApplySettings();
        };
        _boost.CheckedChanged += (_, _) => ApplySettings();
        _engineOn.CheckedChanged += (_, _) => SetEngine(_engineOn.Checked);
        _compare.Click += (_, _) => StartCompare();
        p.Controls.AddRange([Ui.Text("Style:"), _style, Ui.Text("Intensity:"), _intensity, _intensityValue, _boost, _engineOn, _compare]);
        p.SetFlowBreak(_compare, true);
        p.Controls.Add(_status);
        return p;
    }

    private TabPage ShowcasePage()
    {
        var page = new TabPage("Showcase");
        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(330)));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(330)));
        split.Controls.Add(VocabularyPanel(), 0, 0);
        split.Controls.Add(DemoPanel(), 1, 0);
        var felt = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };
        felt.Controls.Add(_felt);
        felt.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            MaximumSize = new Size(LogicalToDeviceUnits(300), 0),
            Padding = new Padding(0, 0, 0, 8),
            Text = "Live engine decisions, not proof of a felt pulse. Open Inspector for output details.",
        });
        felt.Controls.Add(new Label { Dock = DockStyle.Top, Height = 30, Text = "Engine activity", Font = new Font("Segoe UI Semibold", 12f) });
        split.Controls.Add(felt, 2, 0);
        page.Controls.Add(split);
        page.Controls.Add(GettingStartedPanel());
        return page;
    }

    private Control GettingStartedPanel()
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(12, 8, 12, 10),
            BackColor = Color.FromArgb(235, 243, 252),
        };
        flow.Controls.Add(new Label { Text = "Start here", Font = Ui.Big, AutoSize = true });
        flow.Controls.Add(Ui.Text("1. Connect: move your haptic mouse or touchpad over this window. Each device needs its own movement."));
        flow.Controls.Add(Ui.Text("2. Feel: click Feel it next to Land. If nothing plays, check device support and Windows haptic settings."));
        flow.Controls.Add(Ui.Text("3. Explore: move slowly over the controls below, then try another app. Keep this window open; Engine on controls background feedback."));
        flow.Controls.Add(Ui.Text("No haptic device? You can still explore the controls and Inspector. Nothing moves or clicks on your behalf."));
        flow.SizeChanged += (_, _) =>
        {
            foreach (Control child in flow.Controls)
            {
                child.MaximumSize = new Size(Math.Max(1, flow.ClientSize.Width - flow.Padding.Horizontal - child.Margin.Horizontal), 0);
            }
        };
        return flow;
    }

    private Control VocabularyPanel()
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(6) };
        flow.Controls.Add(new Label { Text = "The vocabulary", Font = new Font("Segoe UI Semibold", 12f), AutoSize = true });
        flow.Controls.Add(Ui.Text("A few sensations, one meaning each. Click Feel it.", width: LogicalToDeviceUnits(300)));
        foreach (var (name, waveform, meaning, appsOnly) in Vocabulary)
        {
            var card = new TableLayoutPanel { Width = LogicalToDeviceUnits(300), AutoSize = true, ColumnCount = 2, BackColor = appsOnly ? Color.FromArgb(246, 246, 246) : Color.FromArgb(235, 243, 252), Margin = new Padding(0, 4, 0, 4), Padding = new Padding(6) };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            card.Controls.Add(new Label { Text = $"{name}  ·  {waveform}", Font = new Font("Segoe UI Semibold", 10.5f), AutoSize = true }, 0, 0);
            var feel = new Button { Text = "Feel it", AutoSize = true };
            feel.Click += (_, _) => FeelIt(waveform, appsOnly);
            card.Controls.Add(feel, 1, 0);
            var text = new Label { Text = meaning, AutoSize = true, MaximumSize = new Size(LogicalToDeviceUnits(285), 0), ForeColor = Color.FromArgb(60, 60, 60) };
            card.Controls.Add(text, 0, 1);
            card.SetColumnSpan(text, 2);
            flow.Controls.Add(card);
        }

        return flow;
    }

    private Control DemoPanel()
    {
        var root = new Panel { Dock = DockStyle.Fill };
        var menu = new MenuStrip { Dock = DockStyle.Top };
        foreach (var (top, items) in new[] { ("File", new[] { "New", "Open", "Save", "Print" }), ("Edit", new[] { "Undo", "Cut", "Copy", "Paste" }), ("View", new[] { "Zoom in", "Zoom out", "Full screen" }) })
        {
            var m = new ToolStripMenuItem(top);
            foreach (var i in items)
            {
                m.DropDownItems.Add(new ToolStripMenuItem(i));
            }

            menu.Items.Add(m);
        }

        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(10) };
        flow.Controls.Add(new Label { Text = "Try it — move slowly, then fast; stop on things", Font = new Font("Segoe UI Semibold", 12f), AutoSize = true });
        flow.Controls.Add(Ui.Text("Menu bar above: a detent when you enter it, lands on each item. Open a menu: a detent when you enter it.", width: LogicalToDeviceUnits(560)));

        flow.Controls.Add(Section("Toolbar — detent on entering, a land per button, an edge if you slide off the end"));
        var tb = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.None, AutoSize = true };
        foreach (var t in new[] { "B", "I", "U", "|", "⟵", "⟶", "|", "+", "−", "⤢" })
        {
            tb.Items.Add(t == "|" ? new ToolStripSeparator() : new ToolStripButton(t) { AutoSize = false, Size = S(34, 30) });
        }

        flow.Controls.Add(tb);

        flow.Controls.Add(Section("Ribbon — each group is a region; big buttons land with a firmer Click"));
        var ribbon = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        foreach (var (group, big, small) in new[] { ("Clipboard", "Paste", new[] { "Cut", "Copy" }), ("Insert", "Picture", new[] { "Table", "Shape" }), ("Share", "Send", new[] { "Link", "Export" }) })
        {
            var g = new GroupBox { Text = group, Size = S(170, 118) };
            g.Controls.Add(new Button { Text = big, Location = P(8, 22), Size = S(64, 88) });
            for (var i = 0; i < small.Length; i++)
            {
                g.Controls.Add(new Button { Text = small[i], Location = P(80, 22 + (i * 34)), Size = S(80, 30) });
            }

            ribbon.Controls.Add(g);
        }

        flow.Controls.Add(ribbon);

        flow.Controls.Add(Section("Tabs — a detent per tab; the tab you're on stays silent"));
        var tabs = new TabControl { Size = S(520, 60) };
        foreach (var t in new[] { "Home", "Insert", "Design", "Review", "View" })
        {
            tabs.TabPages.Add(new TabPage(t));
        }

        flow.Controls.Add(tabs);

        flow.Controls.Add(Section("Options — radio buttons are detents, checkboxes land"));
        var opts = new GroupBox { Text = "Size", Size = S(520, 64) };
        var x = 10;
        foreach (var r in new[] { "Small", "Medium", "Large" })
        {
            opts.Controls.Add(new RadioButton { Text = r, Location = P(x, 26), AutoSize = true, Checked = r == "Medium" });
            x += 100;
        }

        opts.Controls.Add(new CheckBox { Text = "Bold", Location = P(x + 20, 26), AutoSize = true });
        opts.Controls.Add(new CheckBox { Text = "Italic", Location = P(x + 100, 26), AutoSize = true });
        flow.Controls.Add(opts);

        flow.Controls.Add(Section("Slider — hold the button and drag: a detent every 10, an edge at each end"));
        flow.Controls.Add(new System.Windows.Forms.Integration.ElementHost
        {
            Size = S(520, 44),
            Child = new System.Windows.Controls.Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = 50,
                SmallChange = 1,
                LargeChange = 10,
                TickFrequency = 10,
                TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
            },
        });

        flow.Controls.Add(Section("Stays silent — on purpose"));
        var silent = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        for (var i = 0; i < 4; i++)
        {
            silent.Controls.Add(new Button { Size = S(9, 9), Margin = new Padding(2, 12, 2, 2), FlatStyle = FlatStyle.Flat });
        }

        silent.Controls.Add(Ui.Text("too small"));
        silent.Controls.Add(new Button { Text = "Disabled", Enabled = false, AutoSize = true });
        silent.Controls.Add(new TextBox { UseSystemPasswordChar = true, Text = "not-a-secret", Width = 110 });
        var painted = new Panel { Size = S(150, 34), BackColor = Color.FromArgb(40, 44, 52) };
        painted.Paint += (_, e) => TextRenderer.DrawText(e.Graphics, "Painted 'button'", Font, P(10, 8), Color.White);
        silent.Controls.Add(painted);
        flow.Controls.Add(silent);

        root.Controls.Add(flow);
        root.Controls.Add(menu);
        return root;

        Size S(int w, int h) => new(LogicalToDeviceUnits(w), LogicalToDeviceUnits(h));

        Point P(int x, int y) => new(LogicalToDeviceUnits(x), LogicalToDeviceUnits(y));

        static Label Section(string text) =>
            new() { Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 10f), ForeColor = Color.FromArgb(0, 84, 166), Margin = new Padding(0, 14, 0, 4) };
    }

    private TabPage InspectorPage()
    {
        var page = new TabPage("Inspector");
        page.Controls.Add(_inspector);
        page.Controls.Add(_stats);
        page.Controls.Add(new Label
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            Padding = new Padding(6),
            Text = "Every decision the engine makes, in any app, with its reason. Passive: mouse-only Raw Input + read-only UI Automation (type, bounds, state; never names or text). Nothing is saved.",
        });
        return page;
    }

    private TabPage LabPage()
    {
        var page = new TabPage("Lab");
        var inner = new TabControl { Dock = DockStyle.Fill };
        var gates = new TabPage("Gates");
        gates.Controls.Add(_gates);
        var exps = new TabPage("Experiments");
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44 };
        var run = new Button { Text = "Run selected (separate window)", AutoSize = true };
        run.Click += (_, _) =>
        {
            if (_experiments.SelectedItems.Count > 0)
            {
                StartChild(["run", _experiments.SelectedItems[0].Text], _experiments.SelectedItems[0].Text);
            }
        };
        var fixture = new Button { Text = "Open fixture", AutoSize = true };
        fixture.Click += (_, _) => StartChild(["fixture"], null);
        var report = new Button { Text = "Write report", AutoSize = true };
        report.Click += (_, _) => Process.Start(new ProcessStartInfo(Report.Write().Html) { UseShellExecute = true });
        bar.Controls.AddRange([run, fixture, report]);
        exps.Controls.Add(_experiments);
        exps.Controls.Add(bar);
        var results = new TabPage("Results");
        _runs.DoubleClick += (_, _) =>
        {
            if (_runs.SelectedItems.Count > 0 && _runs.SelectedItems[0].Tag is string dir)
            {
                var summary = Path.Combine(dir, "summary.md");
                Process.Start(new ProcessStartInfo(File.Exists(summary) ? summary : dir) { UseShellExecute = true });
            }
        };
        results.Controls.Add(_runs);
        inner.TabPages.AddRange([gates, exps, results]);
        page.Controls.Add(inner);
        page.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(6),
            Text = "The research behind it. Blinded, felt experiments established that background haptics work (EXP-010/EXP-020 PASS on a Surface Laptop 8 + Surface Mouse 2nd Edition). " +
                   "You don't need to run anything here to use Haptics Everywhere; it's for re-checking new devices and builds.",
        });
        RefreshLab();
        return page;
    }

    private void OnShownCore()
    {
        var trayRunning = Mutex.TryOpenExisting(@"Local\HapticsEverywhereTray", out var m);
        m?.Dispose();
        if (trayRunning)
        {
            _engineOn.Checked = false; // The tray already gives haptics everywhere; two engines would double-buzz.
            _status.Text = "The tray is running and already provides haptics (including on this window). Showcase engine paused; tick Engine on to use this one instead (quit the tray first).";
        }
        else
        {
            SetEngine(true);
        }

        if (_startCompare)
        {
            StartCompare();
        }
    }

    private void SetEngine(bool on)
    {
        if (on && _pipeline is null)
        {
            _pipeline = new ObservationPipeline(PolicyConfig.Sample(SelectedStyle, Intensity), new PipelineOptions { Route = OutputRoutes.CachedController });
            _pipeline.Policy.ConsentGiven = true;
            _pipeline.Policy.OutputRouteVerified = true;
            _pipeline.Event += e => _events.Enqueue(e);
            _bank.ApplyTo(_pipeline);
            _startMs = MonotonicClock.Instance.NowMs;
            ApplySettings();
            _pipeline.Start();
            _cooperation ??= new CooperativeServer((w, pid) => _pipeline?.SubmitCooperative(w, pid));
            _cooperation.Start();
        }
        else if (!on && _pipeline is not null)
        {
            _pipeline.Dispose();
            _pipeline = null;
            _cooperation?.Dispose();
            _cooperation = null;
        }

        UpdateStatus();
    }

    private void ApplySettings(EffectProfile? overrideStyle = null)
    {
        if (_pipeline is null)
        {
            return;
        }

        _pipeline.Policy.Config = PolicyConfig.Sample(overrideStyle ?? SelectedStyle, Intensity);
        _pipeline.TouchpadBoost = _boost.Checked ? 1.3 : 1.0;
    }

    private void UpdateStatus()
    {
        if (_compareRun is not null)
        {
            return;
        }

        if (_pipeline is null)
        {
            if (!_status.Text.StartsWith("The tray", StringComparison.Ordinal))
            {
                _status.Text = "Engine off. Turn on Engine on to explore feedback; Feel it still previews individual sensations.";
            }

            _status.ForeColor = SystemColors.ControlText;
            return;
        }

        _status.Text = _bank.Any
            ? $"Controller captured: {_bank.Describe()}. Try Feel it, then explore here or in another app. Only you can confirm what you feel."
            : "Waiting for a device: move your haptic mouse or touchpad over this window. No background haptics until a controller is captured.";
        _status.ForeColor = _bank.Any ? Color.FromArgb(16, 110, 16) : Color.FromArgb(160, 90, 0);
    }

    private void FeelIt(string waveform, bool appsOnly)
    {
        // App-only sensations go through the real cooperative pipe → policy → background route, end to end.
        if (appsOnly && CooperativeServer.Send(waveform))
        {
            return;
        }

        var a = HapticsApi.SendViaThreadManager(OutputRoutes.ForegroundManager, waveform, Intensity);
        if (!a.Accepted)
        {
            Felt($"(couldn't play {waveform}: {a.Error ?? "device didn't accept it"})");
        }
    }

    private void Drain()
    {
        if (_pipeline is not null)
        {
            var s = _pipeline.Stats();
            _stats.Text = $"raw {s.RawEvents} (bg {s.BackgroundRawEvents})  uia p50 {s.LookupP50:0.0}/p95 {s.LookupP95:0.0}ms  emit {s.Emits} supp {s.Suppressed}  sent {s.ApiAccepted} rejected {s.ApiRejected} late {s.Expired}  landing ≤{_pipeline.Interpreter.EffectiveLandingSpeed:0}px/s";
        }

        if (_events.IsEmpty)
        {
            return;
        }

        _inspector.BeginUpdate();
        var n = 0;
        while (n++ < 300 && _events.TryDequeue(out var e))
        {
            switch (e)
            {
                case DecisionEvent d:
                    if (d.Decision.Reason != "no-haptic-for-event")
                    {
                        Row(e.AtMs, d.Decision.Event.Kind.ToString(), d.Decision.Event.ControlType, d.Decision.Outcome.ToString(), d.Decision.Reason, d.Decision.Intent?.Waveform,
                            $"conf {d.Decision.Event.Confidence:0.00} speed {d.Decision.Event.PointerSpeedPxPerSec:0}px/s");
                        if (Friendly(d.Decision) is { } text)
                        {
                            Felt(text);
                        }
                    }

                    break;
                case NoteEvent note:
                    Row(e.AtMs, "Note", note.Element?.ControlType ?? "", "", note.Note, null, "");
                    Felt(note.Note switch
                    {
                        "skip:already-selected" => "silent — you're already on this one",
                        "skip:too-small" => "silent — too small to be a real target",
                        "skip:inline-link" => "silent — a link inside text",
                        "passed-through" => "silent — flying past, not landing",
                        _ => $"silent — {note.Note}",
                    }, quiet: true);
                    break;
                case OutputEvent o when o.Attempt is { Accepted: false } a:
                    Row(e.AtMs, "Output", "", o.Status, a.Route, o.Intent.Waveform, a.Error ?? "");
                    break;
                case DiagnosticEvent g:
                    Row(e.AtMs, "Diagnostic", "", "", g.Message, null, "");
                    break;
            }
        }

        while (_inspector.Items.Count > 500)
        {
            _inspector.Items.RemoveAt(_inspector.Items.Count - 1);
        }

        _inspector.EndUpdate();
    }

    private void Row(double at, string a, string b, string c, string d, string? w, string detail) =>
        _inspector.Items.Insert(0, new ListViewItem([$"{at - _startMs:0}", a, b, c, d, w ?? "", detail]));

    private void Felt(string text, bool quiet = false)
    {
        if (_compareRun is not null)
        {
            return; // Blind comparison: don't reveal what's happening.
        }

        if (quiet && _felt.Items.Count > 0 && _felt.Items[0] is string top && top == text)
        {
            return;
        }

        _felt.Items.Insert(0, text);
        while (_felt.Items.Count > 40)
        {
            _felt.Items.RemoveAt(_felt.Items.Count - 1);
        }
    }

    /// <summary>Plain-language explanation of a decision for the "What you just felt" list.</summary>
    internal static string? Friendly(PolicyDecision d)
    {
        var e = d.Event;
        var what = e.Kind switch
        {
            SemanticEventKind.CrossGroupBoundary => $"entered a {e.Element?.ParentControlType?.ToLowerInvariant() ?? "group"}",
            SemanticEventKind.GroupEdge => $"end of the {e.Element?.ParentControlType?.ToLowerInvariant() ?? "strip"}",
            SemanticEventKind.EnterControl => $"landed on a {Human(e.ControlType)}",
            SemanticEventKind.RangeStep => "slider tick",
            SemanticEventKind.RangeLimit => "slider end",
            SemanticEventKind.Cooperative => $"an app sent {e.RequestedWaveform}",
            _ => null,
        };
        if (what is null)
        {
            return null;
        }

        if (d.Outcome != PolicyOutcome.Suppressed && d.Intent is { } i)
        {
            var feel = i.Kind switch
            {
                IntentKind.Hover => "Land",
                IntentKind.Click => "Land · big",
                IntentKind.Step => "Detent",
                IntentKind.Collide => "Edge",
                _ => i.Waveform,
            };
            return $"{feel} — {what}";
        }

        var why = d.Reason switch
        {
            "coalesced-with-group-boundary" => null, // The detent for the same move already explained it.
            "reentry-cooldown" => "just felt this one",
            "density-backoff" => "busy app: structure only for a moment",
            "disabled-control" => "it's disabled",
            "protected-context" => "password field",
            "process-excluded" => "excluded app",
            "stale-intent" => "answer came too late",
            "rule-disabled:land" or "rule-disabled:land-large" => "Quiet style",
            _ when d.Reason.StartsWith("native-suppression", StringComparison.Ordinal) => "Windows/Office already buzz here",
            _ when d.Reason.StartsWith("rate-limit", StringComparison.Ordinal) => "rate limit",
            _ => d.Reason,
        };
        return why is null ? null : $"silent — {what} ({why})";

        static string Human(string t) => t switch
        {
            "SplitButton" => "split button",
            "MenuItem" => "menu item",
            "TabItem" => "tab",
            "CheckBox" => "checkbox",
            "RadioButton" => "option",
            "Hyperlink" => "link",
            "ComboBox" => "drop-down",
            _ => t.ToLowerInvariant(),
        };
    }

    private void StartCompare()
    {
        if (_compareRun is not null)
        {
            return;
        }

        if (_pipeline is null)
        {
            MessageBox.Show(this, "Turn the engine on (and quit the tray) first.", "Compare styles");
            return;
        }

        if (MessageBox.Show(this,
                "Blind comparison (EXP-090): three 60-second blocks, one per style, in random order. Use your PC normally — any apps. " +
                "After each block a small panel asks how helpful and how annoying it felt. This window won't show which style is on.\n\nStart?",
                "Compare styles", MessageBoxButtons.OKCancel) != DialogResult.OK)
        {
            return;
        }

        var order = new[] { EffectProfile.Quiet, EffectProfile.Focused, EffectProfile.Everything }.OrderBy(_ => Random.Shared.Next()).ToList();
        _compareRun = new CompareRun(order, ExperimentSession.Start("EXP-090", "physical", null));
        _style.Enabled = false;
        NextBlock();
    }

    private void NextBlock()
    {
        var run = _compareRun!;
        if (run.Index >= run.Order.Count)
        {
            FinishCompare();
            return;
        }

        var style = run.Order[run.Index];
        ApplySettings(style);
        var remaining = 60;
        _status.ForeColor = Color.FromArgb(0, 84, 166);
        _status.Text = $"Blind comparison: block {run.Index + 1} of {run.Order.Count} — use your PC normally. Rating in {remaining} s.";
        var t = new System.Windows.Forms.Timer { Interval = 1000 };
        t.Tick += (_, _) =>
        {
            remaining--;
            _status.Text = $"Blind comparison: block {run.Index + 1} of {run.Order.Count} — use your PC normally. Rating in {remaining} s.";
            if (remaining > 0)
            {
                return;
            }

            t.Dispose();
            RatingPanel? panel = null;
            panel = new RatingPanel($"Block {run.Index + 1} of {run.Order.Count}: how did the haptics feel?", (helpful, annoying) =>
            {
                run.Ratings.Add((style, helpful, annoying));
                run.Session.Artifacts.Trace(new { type = "rating", block = run.Index + 1, style = style.ToString(), helpful, annoying });
                panel!.Close();
                run.Index++;
                NextBlock();
            });
            panel.Show();
        };
        t.Start();
    }

    private void FinishCompare()
    {
        var run = _compareRun!;
        var r = run.Session.Result;
        r.Method["blocks"] = run.Order.Select(s => s.ToString()).ToList();
        r.Method["blockSeconds"] = 60;
        r.Method["blinding"] = "Style hidden from the operator during blocks; order randomized.";
        r.Observations["ratings"] = run.Ratings.Select(x => new { style = x.Style.ToString(), x.Helpful, x.Annoying }).ToList();
        var best = run.Ratings.OrderByDescending(x => x.Helpful - x.Annoying).First();
        r.Status = ExperimentStatus.PASS;
        r.EvidenceClass = EvidenceClass.OBSERVED;
        r.Explanation = $"Preferred: {best.Style} (helpful {best.Helpful}/5, annoying {best.Annoying}/5). " +
                        string.Join(" · ", run.Ratings.Select(x => $"{x.Style}: helpful {x.Helpful}, annoying {x.Annoying}"));
        r.Limitations.Add("One person, one session, 60 s per style: a first impression, not a durable preference (SPEC EXP-090 suggests repeat sessions).");
        ResultPath = run.Session.Complete();
        run.Session.Dispose();
        _compareRun = null;
        _style.Enabled = true;
        ApplySettings();
        RefreshLab();
        UpdateStatus();
        MessageBox.Show(this, r.Explanation + $"\n\nSaved {ResultPath}", "Compare styles — result");
    }

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
        p.EnableRaisingEvents = true;
        p.Exited += (_, _) => BeginInvoke(RefreshLab);
        if (id is not null && ExperimentCatalog.Find(id) is { NeedsHuman: false })
        {
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

    private void RefreshLab()
    {
        var results = EvidenceLedger.Load();
        _gates.BeginUpdate();
        _gates.Items.Clear();
        foreach (var g in EvidenceLedger.Gates(results, _fp))
        {
            _gates.Items.Add(new ListViewItem([g.Gate, g.State, g.Detail]));
        }

        _gates.EndUpdate();
        _experiments.BeginUpdate();
        _experiments.Items.Clear();
        foreach (var e in ExperimentCatalog.All)
        {
            var latest = EvidenceLedger.Latest(results, e.Id, _fp)?.Result.Status.ToString() ?? "NOT_RUN";
            _experiments.Items.Add(new ListViewItem([e.Id, e.Title, e.Mode, e.Implemented ? "yes" : "no", latest, e.Summary]) { ForeColor = e.Implemented ? Color.Black : Color.Gray });
        }

        _experiments.EndUpdate();
        _runs.BeginUpdate();
        _runs.Items.Clear();
        foreach (var r in results.AsEnumerable().Reverse())
        {
            _runs.Items.Add(new ListViewItem([$"{r.Result.EndedAtUtc:yyyy-MM-dd HH:mm:ss}", r.Result.ExperimentId, r.Result.Status.ToString(), r.Result.EvidenceClass.ToString(), r.Result.Explanation])
            {
                Tag = r.Directory,
                ForeColor = Ui.StatusColor(r.Result.Status),
            });
        }

        _runs.EndUpdate();
    }

    private sealed class CompareRun(List<EffectProfile> order, ExperimentSession session)
    {
        public List<EffectProfile> Order { get; } = order;

        public ExperimentSession Session { get; } = session;

        public List<(EffectProfile Style, int Helpful, int Annoying)> Ratings { get; } = new();

        public int Index { get; set; }
    }
}
