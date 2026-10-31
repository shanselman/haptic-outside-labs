using System.Diagnostics;
using System.Collections.Concurrent;
using Haptics.Core;
using Haptics.Windows;

namespace Haptics.Tray;

/// <summary>
/// The sample tray: starts On, drives haptic devices from the background through acquired controllers
/// (route R3a). Startup tries optional device discovery; unavailable devices retain real-input priming in our
/// settings window/menu. Discovery is not physical evidence. Passive only — never moves, clicks, or focuses
/// anything in other apps.
/// </summary>
internal sealed class TrayContext : ApplicationContext, ITrayHost
{
    private readonly NotifyIcon _icon = new() { Visible = true };
    private readonly ContextMenuStrip _menu = new();
    private readonly ControllerBank _bank = new();
    private readonly System.Windows.Forms.Timer _foreground = new() { Interval = 1000 };
    private ObservationPipeline? _pipeline;
    private CooperativeServer? _cooperation;
    private SettingsWindow? _window;
    private readonly ConcurrentQueue<DeviceChangeEvent> _deviceChanges = new();
    private readonly ConcurrentQueue<ControllerLostEvent> _lostControllers = new();
    private readonly ControllerRefresh _controllerRefresh = new(() => Task.Run(ControllerDiscovery.Discover));

    public TrayContext()
    {
        _icon.ContextMenuStrip = _menu;
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowSettings(startup: false);
            }
        };
        _menu.Opening += (_, _) => BuildMenu();
        _menu.MouseMove += (_, _) => TryPrime(); // Any haptic device moving over our own menu refreshes its controller.
        _foreground.Tick += (_, _) =>
        {
            TrackForeground();
            PollStartupPriming();
        };
        _foreground.Start();
        Apply();
        PollStartupPriming();
    }

    public event EventHandler? Changed;

    public TraySettings Settings { get; } = TraySettings.Load();

    public bool Primed => _bank.Any;

    public string Devices => _bank.Describe();

    public string DiscoveryStatus { get; private set; } = "Automatic lookup has not run.";

    public string? LastApp { get; private set; }

    public bool StartWithWindows
    {
        get => StartupEntry.IsEnabled;
        set => StartupEntry.IsEnabled = value;
    }

    public PipelineStats? Stats => _pipeline?.Stats();

    public double? LandingSpeed => _pipeline?.Interpreter.EffectiveLandingSpeed;

    public void TryPrime()
    {
        if (_pipeline is { } pipeline &&
            pipeline.LatestInput is { } input &&
            _bank.TryCapture(input.DeviceHandle, input.InputClass, input.TimestampMs, MonotonicClock.Instance.NowMs))
        {
            _bank.ApplyTo(pipeline);
            UpdateIcon();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void PollStartupPriming()
    {
        var changed = false;
        while (_lostControllers.TryDequeue(out var lost))
        {
            if (HandleControllerLoss(_bank, _controllerRefresh, lost))
            {
                DiscoveryStatus = "A haptic controller was lost; looking again, or move over Settings to prime.";
                changed = true;
            }
        }

        while (_deviceChanges.TryDequeue(out var device))
        {
            _controllerRefresh.DeviceChanged(device.Arrived);
            if (!device.Arrived && _bank.RemoveDevice(device.Handle))
            {
                DiscoveryStatus = "A haptic device disconnected; reconnect it or move over Settings to prime.";
                changed = true;
            }
        }

        var (result, showFallback, started) = _controllerRefresh.Poll(Environment.TickCount64, Settings.Enabled, _bank.Any);
        if (started)
        {
            DiscoveryStatus = "Looking for haptic devices without window input...";
            changed = true;
        }

        if (result is not null)
        {
            _bank.AddDiscovered(result);
            DiscoveryStatus = result.Diagnostic;
            changed = true;
        }

        if (changed)
        {
            _bank.ApplyTo(_pipeline);
            UpdateIcon();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        if (showFallback)
        {
            if (result is null)
            {
                DiscoveryStatus = "Automatic lookup is taking too long; move over this window to prime instead.";
            }

            ShowSettings(startup: true);
        }
    }

    internal static bool HandleControllerLoss(ControllerBank bank, ControllerRefresh refresh, ControllerLostEvent lost)
    {
        if (!bank.RemoveLostController(lost.Handle, lost.Controller))
        {
            return false;
        }

        refresh.Request();
        return true;
    }

    public void Apply()
    {
        var saved = RevokeAndSave(Settings, _pipeline, Settings.Save);
        _pipeline = saved.Pipeline;
        if (Settings.Enabled && _pipeline is null)
        {
            _pipeline = new ObservationPipeline(Settings.ToPolicy(), new PipelineOptions { Route = OutputRoutes.CachedController });
            _pipeline.Event += e =>
            {
                if (e is DeviceChangeEvent device)
                {
                    _deviceChanges.Enqueue(device);
                }
                else if (e is ControllerLostEvent lost)
                {
                    _lostControllers.Enqueue(lost);
                }
            };
            _pipeline.Start();
            _controllerRefresh.Request();
        }
        if (_pipeline is not null)
        {
            _pipeline.Policy.Config = Settings.ToPolicy();
            _pipeline.Policy.ConsentGiven = true; // Launching the sample is the opt-in.
            _pipeline.Policy.OutputRouteVerified = true;
            _pipeline.TouchpadBoost = Settings.TouchpadBoost;
            _bank.ApplyTo(_pipeline);
        }

        var wantCooperation = Settings.Enabled && Settings.CooperationEnabled;
        if (wantCooperation && _cooperation is null)
        {
            _cooperation = new CooperativeServer((waveform, pid) => _pipeline?.SubmitCooperative(waveform, pid));
            _cooperation.Start();
        }
        else if (!wantCooperation && _cooperation is not null)
        {
            _cooperation.Dispose();
            _cooperation = null;
        }

        UpdateIcon();
        Changed?.Invoke(this, EventArgs.Empty);
        if (saved.Error is { } error)
        {
            MessageBox.Show(error, "Haptics Everywhere");
        }
    }

    internal static (ObservationPipeline? Pipeline, string? Error) RevokeAndSave(
        TraySettings settings, ObservationPipeline? pipeline, Action save)
    {
        if (!settings.Enabled)
        {
            pipeline?.Dispose(); // Revokes authorization and invalidates pending work before teardown.
            pipeline = null;
        }

        try
        {
            save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (pipeline, $"Couldn't save settings. Your changes apply for this session only.\n\n{ex.Message}");
        }

        return (pipeline, null);
    }

    public void OpenShowcase()
    {
        var dir = AppContext.BaseDirectory;
        var candidates = new List<string> { Path.Combine(dir, "haptics-lab.exe") };
        var repo = ArtifactLocator.FindRepoRoot(dir);
        if (repo is not null && Directory.Exists(Path.Combine(repo, "src", "Haptics.Lab", "bin")))
        {
            candidates.AddRange(Directory.GetFiles(Path.Combine(repo, "src", "Haptics.Lab", "bin"), "haptics-lab.exe", SearchOption.AllDirectories));
        }

        var exe = candidates.FirstOrDefault(File.Exists);
        if (exe is null)
        {
            MessageBox.Show("The Showcase (haptics-lab.exe) isn't next to the tray. It's in the full download or the repo.", "Haptics Everywhere");
            return;
        }

        TryLaunchShowcase(() => Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }),
            message => MessageBox.Show(message, "Haptics Everywhere"));
    }

    internal static void TryLaunchShowcase(Action launch, Action<string> reportError)
    {
        try
        {
            launch();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The user cancelled a security prompt; never bypass it.
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            reportError($"Couldn't open the Showcase.\n\n{ex.Message}");
        }
    }

    private void ShowSettings(bool startup)
    {
        if (_window is not null)
        {
            if (startup)
            {
                return; // Automatic recovery must not reactivate a window the user left.
            }

            _window.KeepOpen();
            if (_window.WindowState == System.Windows.WindowState.Minimized)
            {
                _window.WindowState = System.Windows.WindowState.Normal;
            }

            _window.Activate(); // Our own window, because you clicked our tray icon.
            return;
        }

        var window = new SettingsWindow(this, autoClose: startup);
        window.ShowActivated = !startup;
        window.Closing += (_, _) =>
        {
            if (window.IsLoaded)
            {
                Wpf.Ui.Appearance.SystemThemeWatcher.UnWatch(window); // Watch keeps windows in a static list otherwise.
            }
        };
        window.Closed += (_, _) => _window = null;
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(window);
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(window);
        _window = window;
        window.Show();
    }

    private void UpdateIcon()
    {
        var text = !Settings.Enabled ? "Haptics Everywhere: Off"
            : !_bank.Any ? "Haptics Everywhere: On (needs prime)"
            : $"Haptics Everywhere: On ({_bank.Describe()})";
        _icon.Text = text.Length > 63 ? text[..63] : text;
        var old = _icon.Icon;
        _icon.Icon = AppIcon.ForTray(!Settings.Enabled ? TrayState.Off : !_bank.Any ? TrayState.NeedsPrime : TrayState.Ready);
        old?.Dispose();
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripLabel(!_bank.Any && Settings.Enabled
            ? "Haptics Everywhere — move your mouse or touchpad over this menu"
            : $"Haptics Everywhere — {_bank.Describe()}") { Font = new Font(_menu.Font, FontStyle.Bold) });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("On", () =>
        {
            Settings.Enabled = !Settings.Enabled;
            Apply();
        }, Settings.Enabled));

        var style = new ToolStripMenuItem("Style");
        foreach (var s in TraySettings.Styles)
        {
            style.DropDownItems.Add(Item($"{s} — {TraySettings.Describe(s)}", () =>
            {
                Settings.Style = s;
                Apply();
            }, Settings.Style == s));
        }

        _menu.Items.Add(style);
        var intensity = new ToolStripMenuItem("Intensity");
        foreach (var (name, value) in TraySettings.IntensityLevels)
        {
            intensity.DropDownItems.Add(Item(name, () =>
            {
                Settings.IntensityScale = value;
                Apply();
            }, Math.Abs(Settings.IntensityScale - value) < 0.01));
        }

        intensity.DropDownItems.Add(new ToolStripSeparator());
        intensity.DropDownItems.Add(Item("Boost touchpad", () =>
        {
            Settings.TouchpadBoost = Settings.TouchpadBoost > 1.01 ? 1.0 : 1.3;
            Apply();
        }, Settings.TouchpadBoost > 1.01));
        _menu.Items.Add(intensity);
        if (LastApp is { } app)
        {
            var excluded = Settings.ExcludedProcesses.Contains(app, StringComparer.OrdinalIgnoreCase);
            _menu.Items.Add(Item(excluded ? $"Include '{app}' again" : $"Exclude '{app}'", () =>
            {
                if (excluded)
                {
                    Settings.ExcludedProcesses.RemoveAll(x => string.Equals(x, app, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    Settings.ExcludedProcesses.Add(app);
                }

                Apply();
            }));
        }

        _menu.Items.Add(Item("Let apps send haptics", () =>
        {
            Settings.CooperationEnabled = !Settings.CooperationEnabled;
            Apply();
        }, Settings.CooperationEnabled));
        _menu.Items.Add(Item("Settings…", () => ShowSettings(startup: false)));
        _menu.Items.Add(Item("Open Showcase", OpenShowcase));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("Quit", () =>
        {
            _window?.Close();
            _pipeline?.Dispose();
            _pipeline = null;
            _cooperation?.Dispose();
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

    private void TrackForeground()
    {
        var pid = DesktopContext.Foreground().Pid;
        if (pid != 0 && pid != DesktopContext.OwnProcessId)
        {
            LastApp = DesktopContext.ProcessName(pid) ?? LastApp;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _window?.Close();
            _pipeline?.Dispose();
            _cooperation?.Dispose();
            _icon.Dispose();
            _menu.Dispose();
            _foreground.Dispose();
        }

        base.Dispose(disposing);
    }
}
