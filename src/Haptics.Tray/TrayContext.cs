using System.Diagnostics;
using System.Drawing.Drawing2D;
using Haptics.Core;
using Haptics.Windows;
using Windows.Devices.Haptics;

namespace Haptics.Tray;

/// <summary>
/// Tiny tray prototype (SPEC §3.2, §8.4, PR 9). Off / Observe-only / On, with On LOCKED until local evidence
/// shows a physically confirmed background route for this machine. Starts in Observe-only (if consented) or Off.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon = new() { Visible = true };
    private readonly ContextMenuStrip _menu = new();
    private readonly TraySettings _settings = TraySettings.Load();
    private readonly string _fp = EnvironmentProbe.CurrentFingerprint();
    private readonly System.Windows.Forms.Timer _foregroundTimer = new() { Interval = 1000 };
    private ObservationPipeline? _pipeline;
    private HapticsMode _mode = HapticsMode.Off;
    private string? _route;
    private CapturedController? _controller;
    private string? _lastForeignApp;

    public TrayContext()
    {
        _icon.ContextMenuStrip = _menu;
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _menu.Show(Cursor.Position);
            }
        };
        _menu.Opening += (_, _) => BuildMenu();
        _foregroundTimer.Tick += (_, _) => TrackForeground();
        RefreshRoute();
        SetMode(_settings.ConsentGiven ? HapticsMode.ObserveOnly : HapticsMode.Off, prompt: false);
        if (!_settings.ConsentGiven)
        {
            _icon.ShowBalloonTip(5000, "Haptics Everywhere", "Off until you consent. Right-click the tray icon to review what it observes.", ToolTipIcon.Info);
        }
    }

    private void RefreshRoute()
    {
        var results = EvidenceLedger.Load(ArtifactLocator.RunsRoot()).Concat(EvidenceLedger.Load(LocalRunsRoot())).ToList();
        _route = EvidenceLedger.VerifiedBackgroundRoutes(results, _fp).FirstOrDefault();
    }

    private static string LocalRunsRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HapticsEverywhere", "artifacts", "runs");

    private bool NeedsController => _route is OutputRoutes.CachedController or OutputRoutes.OverrideOnRawThread;

    private bool OnAvailable => _route is not null && (!NeedsController || _controller is not null);

    private void BuildMenu()
    {
        RefreshRoute();
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripLabel($"Haptics Everywhere · {_mode}") { Font = new Font(_menu.Font, FontStyle.Bold) });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("Stop now (Off)", () => SetMode(HapticsMode.Off), _mode == HapticsMode.Off));
        _menu.Items.Add(Item("Observe only (no haptics)", () => SetMode(HapticsMode.ObserveOnly), _mode == HapticsMode.ObserveOnly));
        var onText = _route is null
            ? "On — locked: no physically verified background route (run EXP-020 in Haptics Lab)"
            : NeedsController && _controller is null
                ? $"On — needs 'Prime haptic mouse' first ({_route})"
                : $"On ({_route})";
        var on = Item(onText, () => SetMode(HapticsMode.On), _mode == HapticsMode.On);
        on.Enabled = OnAvailable;
        _menu.Items.Add(on);
        if (NeedsController)
        {
            _menu.Items.Add(Item(_controller is null ? "Prime haptic mouse…" : $"Re-prime haptic mouse (captured {_controller.DeviceType})", Prime));
        }

        _menu.Items.Add(new ToolStripSeparator());
        var profile = new ToolStripMenuItem("Effect profile");
        foreach (var p in Enum.GetValues<EffectProfile>())
        {
            profile.DropDownItems.Add(Item(p.ToString(), () =>
            {
                _settings.Profile = p;
                Apply();
            }, _settings.Profile == p));
        }

        _menu.Items.Add(profile);
        var intensity = new ToolStripMenuItem("Intensity");
        foreach (var (name, value) in new[] { ("Low", 0.25), ("Medium", 0.5), ("Strong", 0.8) })
        {
            intensity.DropDownItems.Add(Item(name, () =>
            {
                _settings.IntensityScale = value;
                Apply();
            }, Math.Abs(_settings.IntensityScale - value) < 0.01));
        }

        _menu.Items.Add(intensity);
        var apps = new ToolStripMenuItem("Apps");
        apps.DropDownItems.Add(Item("Allow all apps (except exclusions)", () =>
        {
            _settings.AppPolicy = _settings.AppPolicy == AppPolicy.AllExceptExclusions ? AppPolicy.OptInAllowlist : AppPolicy.AllExceptExclusions;
            Apply();
        }, _settings.AppPolicy == AppPolicy.AllExceptExclusions));
        if (_lastForeignApp is { } app)
        {
            var allowed = _settings.AllowedProcesses.Contains(app, StringComparer.OrdinalIgnoreCase);
            apps.DropDownItems.Add(Item($"Allow '{app}'", () =>
            {
                if (allowed)
                {
                    _settings.AllowedProcesses.RemoveAll(x => string.Equals(x, app, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    _settings.AllowedProcesses.Add(app);
                }

                Apply();
            }, allowed));
            apps.DropDownItems.Add(Item($"Exclude '{app}'", () =>
            {
                _settings.ExtraExcludedProcesses.Add(app);
                _settings.AllowedProcesses.RemoveAll(x => string.Equals(x, app, StringComparison.OrdinalIgnoreCase));
                Apply();
            }));
        }

        if (_settings.AllowedProcesses.Count > 0)
        {
            apps.DropDownItems.Add(new ToolStripLabel("Allowed: " + string.Join(", ", _settings.AllowedProcesses)));
        }

        _menu.Items.Add(apps);
        _menu.Items.Add(Item("Status…", ShowStatus));
        _menu.Items.Add(Item("What does this observe? (consent)", () => AskConsent(force: true)));
        _menu.Items.Add(Item("Open Haptics Lab", OpenLab));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("Quit", () =>
        {
            SetMode(HapticsMode.Off);
            _icon.Visible = false;
            ExitThread();
        }));
    }

    private static ToolStripMenuItem Item(string text, Action onClick, bool isChecked = false)
    {
        var i = new ToolStripMenuItem(text) { Checked = isChecked };
        i.Click += (_, _) => onClick();
        return i;
    }

    private void SetMode(HapticsMode mode, bool prompt = true)
    {
        if (mode != HapticsMode.Off && !_settings.ConsentGiven && (!prompt || !AskConsent(force: false)))
        {
            mode = HapticsMode.Off;
        }

        if (mode == HapticsMode.On && !OnAvailable)
        {
            mode = HapticsMode.ObserveOnly;
        }

        _mode = mode;
        if (mode == HapticsMode.Off)
        {
            // Off really means off: unregister raw input, kill the UIA worker, stop polling.
            _pipeline?.Dispose();
            _pipeline = null;
            _foregroundTimer.Stop();
            _lastForeignApp = null;
        }
        else
        {
            if (_pipeline is null)
            {
                _pipeline = new ObservationPipeline(_settings.ToPolicy(mode), new PipelineOptions { Route = _route ?? OutputRoutes.DryRun });
                _pipeline.Start();
            }

            _foregroundTimer.Start();
        }

        Apply();
    }

    private void Apply()
    {
        _settings.Save();
        if (_pipeline is not null)
        {
            _pipeline.Policy.Config = _settings.ToPolicy(_mode);
            _pipeline.Policy.ConsentGiven = _settings.ConsentGiven;
            _pipeline.Route = _route ?? OutputRoutes.DryRun;
            _pipeline.Controller = _controller;
            _pipeline.Policy.OutputRouteVerified = _mode == HapticsMode.On && OnAvailable;
        }

        _icon.Text = $"Haptics Everywhere: {_mode}{(_mode == HapticsMode.On ? $" via {_route}" : "")}";
        var old = _icon.Icon;
        _icon.Icon = MakeIcon(_mode switch
        {
            HapticsMode.On => Color.FromArgb(16, 124, 16),
            HapticsMode.ObserveOnly => Color.FromArgb(0, 90, 158),
            _ => Color.Gray,
        });
        old?.Dispose();
    }

    private bool AskConsent(bool force)
    {
        const string text =
            "Haptics Everywhere adds tactile feedback to apps that never asked for it.\n\n" +
            "While Observe-only or On, it:\n" +
            "• reads raw MOUSE input (movement, buttons, wheel) — never the keyboard;\n" +
            "• asks Windows UI Automation what kind of control is under the pointer (button, tab, slider…) and its bounds;\n" +
            "• never reads names, text, values, passwords, URLs, or document content, and takes no screenshots;\n" +
            "• never moves the pointer, clicks, changes focus, or controls other apps;\n" +
            "• stores nothing about what you hover and sends nothing over the network.\n\n" +
            "Password fields, excluded apps, and protected desktops are always silent. 'Stop now (Off)' tears everything down.\n\n" +
            "Allow observation?";
        var ok = MessageBox.Show(text, "Haptics Everywhere — consent", force && _settings.ConsentGiven ? MessageBoxButtons.OK : MessageBoxButtons.YesNo,
            MessageBoxIcon.Information) is DialogResult.Yes or DialogResult.OK;
        if (!force || !_settings.ConsentGiven)
        {
            _settings.ConsentGiven = ok;
            _settings.Save();
        }

        if (!_settings.ConsentGiven && _mode != HapticsMode.Off)
        {
            SetMode(HapticsMode.Off);
        }

        return _settings.ConsentGiven;
    }

    private void Prime()
    {
        using var f = new Form { Text = "Prime haptic mouse", ClientSize = new Size(420, 200), StartPosition = FormStartPosition.CenterScreen, TopMost = true };
        var l = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 11f), Text = "Move your haptic MOUSE over this box." };
        f.Controls.Add(l);
        l.MouseMove += (_, _) =>
        {
            var c = HapticsApi.TryCaptureCurrentController(HapticDeviceType.Mouse);
            if (c is not null)
            {
                _controller = c;
                l.Text = $"Captured {c.DeviceType} controller ({c.IdHash}). You can close this window.";
                Apply();
            }
        };
        f.ShowDialog();
    }

    private void ShowStatus()
    {
        var s = _pipeline?.Stats();
        var text = $"Mode: {_mode}\nVerified background route: {_route ?? "none (On locked)"}\nController captured: {_controller?.DeviceType.ToString() ?? "no"}\n" +
                   $"Profile: {_settings.Profile}, intensity {_settings.IntensityScale:0.00}, apps: {_settings.AppPolicy}\n\n" +
                   (s is null ? "Pipeline stopped." :
                       $"Raw events {s.RawEvents} (background {s.BackgroundRawEvents})\nUIA lookup p50 {s.LookupP50:0.0} ms, p95 {s.LookupP95:0.0} ms\n" +
                       $"Decisions: emit {s.Emits}, dry-run {s.DryRuns}, suppressed {s.Suppressed}\nOutput: accepted {s.ApiAccepted}, rejected {s.ApiRejected}, expired {s.Expired}\n" +
                       $"Input→call p50 {s.InputToCallP50:0.0} ms, p95 {s.InputToCallP95:0.0} ms\nUIA worker restarts {s.Uia?.Restarts}, timeouts {s.Uia?.Timeouts}");
        MessageBox.Show(text, "Haptics Everywhere — status");
    }

    private void TrackForeground()
    {
        var pid = DesktopContext.Foreground().Pid;
        if (pid != 0 && pid != DesktopContext.OwnProcessId)
        {
            _lastForeignApp = DesktopContext.ProcessName(pid) ?? _lastForeignApp;
        }
    }

    private static void OpenLab()
    {
        var dir = AppContext.BaseDirectory;
        var candidates = new List<string> { Path.Combine(dir, "haptics-lab.exe") };
        var repo = ArtifactLocator.FindRepoRoot(dir);
        if (repo is not null)
        {
            candidates.AddRange(Directory.GetFiles(Path.Combine(repo, "src", "Haptics.Lab", "bin"), "haptics-lab.exe", SearchOption.AllDirectories));
        }

        var exe = candidates.FirstOrDefault(File.Exists);
        if (exe is null)
        {
            MessageBox.Show("haptics-lab.exe not found next to the tray or in the repository build output.", "Haptics Everywhere");
            return;
        }

        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    private static Icon MakeIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(color);
            g.FillEllipse(b, 1, 1, 30, 30);
            using var f = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, "H", f, new Rectangle(0, 0, 32, 32), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pipeline?.Dispose();
            _icon.Dispose();
            _menu.Dispose();
            _foregroundTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
