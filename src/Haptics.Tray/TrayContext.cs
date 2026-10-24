using System.Diagnostics;
using System.Drawing.Drawing2D;
using Haptics.Core;
using Haptics.Windows;
using Windows.Devices.Haptics;

namespace Haptics.Tray;

/// <summary>
/// The sample tray: starts On, drives the haptic mouse from the background through a controller captured from our own
/// UI (route R3a, physically confirmed by EXP-020). Priming is automatic: a small box at startup, and silently again
/// whenever the mouse passes over the tray menu. Passive only — never moves, clicks, or focuses anything.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon = new() { Visible = true };
    private readonly ContextMenuStrip _menu = new();
    private readonly TraySettings _settings = TraySettings.Load();
    private readonly System.Windows.Forms.Timer _foreground = new() { Interval = 1000 };
    private ObservationPipeline? _pipeline;
    private CapturedController? _controller;
    private IReadOnlyDictionary<long, CapturedController> _controllers = new Dictionary<long, CapturedController>();
    private string? _lastApp;

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
        _menu.MouseMove += (_, _) => TryPrime(); // Any mouse movement over our own menu refreshes the controller.
        _foreground.Tick += (_, _) => TrackForeground();
        _foreground.Start();
        Apply();
        if (_settings.Enabled)
        {
            ShowPrimeWindow();
        }
    }

    private void TryPrime()
    {
        // Any haptic device that just moved over our own UI (mouse, touchpad, pen) — whatever the user wiggled.
        var c = HapticsApi.TryCaptureCurrentController();
        if (c is null)
        {
            return;
        }

        _controller = c;
        var handle = _pipeline?.LastDeviceHandle ?? 0;
        if (_controllers.TryGetValue(handle, out var known) && known.IdHash == c.IdHash)
        {
            return; // Same device, already primed: nothing to do (this runs on every mouse move over our UI).
        }

        // Associate it with the raw device that is moving right now, so each device buzzes itself.
        _controllers = new Dictionary<long, CapturedController>(_controllers) { [handle] = c };
        if (_pipeline is not null)
        {
            _pipeline.Controller = c;
            _pipeline.Controllers = _controllers;
        }

        UpdateIcon();
    }

    private void Apply()
    {
        _settings.Save();
        if (_settings.Enabled && _pipeline is null)
        {
            _pipeline = new ObservationPipeline(_settings.ToPolicy(), new PipelineOptions { Route = OutputRoutes.CachedController });
            _pipeline.Start();
        }
        else if (!_settings.Enabled && _pipeline is not null)
        {
            _pipeline.Dispose(); // Off really means off: raw input unregistered, UIA worker killed.
            _pipeline = null;
        }

        if (_pipeline is not null)
        {
            _pipeline.Policy.Config = _settings.ToPolicy();
            _pipeline.Policy.ConsentGiven = true; // Launching the sample is the opt-in.
            _pipeline.Policy.OutputRouteVerified = true;
            _pipeline.Controller = _controller;
            _pipeline.Controllers = _controllers;
        }

        UpdateIcon();
    }

    private string Devices() =>
        string.Join(" + ", _controllers.Values.Select(c => c.DeviceType.ToString()).Append(_controller?.DeviceType.ToString() ?? "").Where(s => s.Length > 0).Distinct());

    private void UpdateIcon()
    {
        _icon.Text = !_settings.Enabled ? "Haptics Everywhere: Off"
            : _controller is null ? "Haptics Everywhere: On (needs prime)"
            : $"Haptics Everywhere: On ({Devices()})";
        var old = _icon.Icon;
        _icon.Icon = MakeIcon(!_settings.Enabled ? Color.Gray : _controller is null ? Color.FromArgb(200, 120, 0) : Color.FromArgb(16, 124, 16));
        old?.Dispose();
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripLabel(_controller is null && _settings.Enabled
            ? "Haptics Everywhere — move the mouse over this menu to prime"
            : "Haptics Everywhere") { Font = new Font(_menu.Font, FontStyle.Bold) });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("On", () =>
        {
            _settings.Enabled = !_settings.Enabled;
            Apply();
        }, _settings.Enabled));

        var intensity = new ToolStripMenuItem("Intensity");
        foreach (var (name, value) in new[] { ("Low", 0.3), ("Medium", 0.55), ("Strong", 0.8), ("Max", 1.0) })
        {
            intensity.DropDownItems.Add(Item(name, () =>
            {
                _settings.IntensityScale = value;
                Apply();
            }, Math.Abs(_settings.IntensityScale - value) < 0.01));
        }

        _menu.Items.Add(intensity);
        if (_lastApp is { } app)
        {
            var excluded = _settings.ExcludedProcesses.Contains(app, StringComparer.OrdinalIgnoreCase);
            _menu.Items.Add(Item(excluded ? $"Include '{app}' again" : $"Exclude '{app}'", () =>
            {
                if (excluded)
                {
                    _settings.ExcludedProcesses.RemoveAll(x => string.Equals(x, app, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    _settings.ExcludedProcesses.Add(app);
                }

                Apply();
            }));
        }

        _menu.Items.Add(Item("Status…", ShowStatus));
        _menu.Items.Add(Item("Open Haptics Lab", OpenLab));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("Quit", () =>
        {
            _pipeline?.Dispose();
            _pipeline = null;
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

    private void ShowPrimeWindow()
    {
        var f = new Form
        {
            Text = "Haptics Everywhere",
            ClientSize = new Size(380, 120),
            StartPosition = FormStartPosition.CenterScreen,
            TopMost = true,
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            BackColor = Color.FromArgb(36, 40, 52),
        };
        var l = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 12f),
            Text = "Wiggle your haptic mouse or touchpad over this box.",
        };
        f.Controls.Add(l);
        System.Windows.Forms.Timer? closeTimer = null;
        void OnMove(object? s, MouseEventArgs e)
        {
            TryPrime();
            if (_controller is null)
            {
                return;
            }

            l.Text = $"Got it: {Devices()}.\nWiggle another haptic device here to add it.";
            if (closeTimer is null)
            {
                closeTimer = new System.Windows.Forms.Timer { Interval = 4000 };
                closeTimer.Tick += (_, _) =>
                {
                    closeTimer.Dispose();
                    f.Close();
                };
                closeTimer.Start();
            }
        }

        l.MouseMove += OnMove;
        f.FormClosed += (_, _) => f.Dispose();
        f.Show();
    }

    private void ShowStatus()
    {
        var s = _pipeline?.Stats();
        var text = $"On: {_settings.Enabled}   Intensity: {_settings.IntensityScale:0.00}   Controller: {(_controller is null ? "not primed" : "primed")}\n\n" +
                   (s is null ? "Pipeline stopped." :
                       $"Raw events {s.RawEvents} (background {s.BackgroundRawEvents})\n" +
                       $"UIA lookup p50 {s.LookupP50:0.0} ms, p95 {s.LookupP95:0.0} ms\n" +
                       $"Pulses sent {s.ApiAccepted}, rejected {s.ApiRejected}, too late {s.Expired}\n" +
                       $"Decisions: emit {s.Emits}, suppressed {s.Suppressed}\n" +
                       $"Input→call p50 {s.InputToCallP50:0.0} ms, p95 {s.InputToCallP95:0.0} ms");
        MessageBox.Show(text, "Haptics Everywhere — status");
    }

    private void TrackForeground()
    {
        var pid = DesktopContext.Foreground().Pid;
        if (pid != 0 && pid != DesktopContext.OwnProcessId)
        {
            _lastApp = DesktopContext.ProcessName(pid) ?? _lastApp;
        }
    }

    private static void OpenLab()
    {
        var dir = AppContext.BaseDirectory;
        var candidates = new List<string> { Path.Combine(dir, "haptics-lab.exe") };
        var repo = ArtifactLocator.FindRepoRoot(dir);
        if (repo is not null && Directory.Exists(Path.Combine(repo, "src", "Haptics.Lab", "bin")))
        {
            candidates.AddRange(Directory.GetFiles(Path.Combine(repo, "src", "Haptics.Lab", "bin"), "haptics-lab.exe", SearchOption.AllDirectories));
        }

        var exe = candidates.FirstOrDefault(File.Exists);
        if (exe is not null)
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
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
            _foreground.Dispose();
        }

        base.Dispose(disposing);
    }
}
