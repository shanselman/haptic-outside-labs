using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace Haptics.Tray;

/// <summary>
/// The tray's window: status, settings and details. Moving a haptic device over it primes that device. Opened at
/// startup it closes itself a few seconds after the first device is primed, unless you start using it.
/// </summary>
internal partial class SettingsWindow
{
    private readonly ITrayHost _host;
    private readonly DispatcherTimer _live = new() { Interval = TimeSpan.FromSeconds(1) };
    private DispatcherTimer? _closeTimer;
    private bool _autoClose;
    private bool _syncing;
    private long _nextPrime;

    public SettingsWindow(ITrayHost host, bool autoClose)
    {
        _host = host;
        _autoClose = autoClose;
        InitializeComponent();
        Topmost = autoClose;
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 32);
        TitleIcon.Source = AppIcon.Frame(32);
        HeroImage.Source = AppIcon.Frame(256);
        StyleBox.ItemsSource = TraySettings.Styles;
        var version = typeof(SettingsWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        VersionText.Text = $"Version {version.Split('+')[0]}";

        // Checked/Unchecked rather than Click: Narrator's toggle action flips IsChecked without raising Click.
        OnToggled(EnabledSwitch, on => Change(s => s.Enabled = on));
        OnToggled(BoostSwitch, on => Change(s => s.TouchpadBoost = on ? 1.3 : 1.0));
        OnToggled(CooperationSwitch, on => Change(s => s.CooperationEnabled = on));
        OnToggled(StartupSwitch, SetStartup);
        StyleBox.SelectionChanged += (_, _) =>
        {
            if (!_syncing && StyleBox.SelectedIndex >= 0)
            {
                Change(s => s.Style = TraySettings.Styles[StyleBox.SelectedIndex]);
            }
        };
        IntensitySlider.ValueChanged += (_, e) =>
        {
            IntensityLabel.Text = TraySettings.IntensityName(e.NewValue);
            if (!_syncing)
            {
                Change(s => s.IntensityScale = Math.Round(e.NewValue, 2));
            }
        };
        ExcludeButton.Click += (_, _) =>
        {
            if (_host.LastApp is { } app)
            {
                Change(s => s.ExcludedProcesses.Add(app));
            }
        };
        ShowcaseButton.Click += (_, _) => _host.OpenShowcase();

        PreviewMouseMove += (_, _) => Prime();
        PreviewMouseDown += (_, _) => KeepOpen();
        PreviewMouseWheel += (_, _) => KeepOpen();
        PreviewKeyDown += (_, _) => KeepOpen();
        _live.Tick += (_, _) => RefreshLive();
        Loaded += (_, _) => _live.Start();
        host.Changed += OnHostChanged;
        Closed += (_, _) =>
        {
            host.Changed -= OnHostChanged;
            _live.Stop();
            _closeTimer?.Stop();
        };
        RefreshSettings();
        RefreshLive();
    }

    /// <summary>Cancels the startup auto-close: you're using the window now.</summary>
    public void KeepOpen()
    {
        if (!_autoClose)
        {
            return;
        }

        _autoClose = false;
        _closeTimer?.Stop();
        _closeTimer = null;
        Topmost = false;
        RefreshLive();
    }

    private void Prime()
    {
        var now = Environment.TickCount64;
        if (now < _nextPrime)
        {
            return;
        }

        _nextPrime = now + 150;
        _host.TryPrime();
    }

    private void OnHostChanged(object? sender, EventArgs e)
    {
        RefreshSettings();
        RefreshLive();
        if (_autoClose && _host.Primed && _closeTimer is null)
        {
            _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _closeTimer.Tick += (_, _) => Close();
            _closeTimer.Start();
        }
    }

    private void Change(Action<TraySettings> edit)
    {
        edit(_host.Settings);
        _host.Apply();
    }

    private void OnToggled(System.Windows.Controls.Primitives.ToggleButton toggle, Action<bool> apply)
    {
        System.Windows.RoutedEventHandler handler = (_, _) =>
        {
            if (!_syncing)
            {
                apply(toggle.IsChecked == true);
            }
        };
        toggle.Checked += handler;
        toggle.Unchecked += handler;
    }

    private void SetStartup(bool on)
    {
        try
        {
            _host.StartWithWindows = on;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Leave it as it was; the switch below shows the real state.
        }

        RefreshSettings();
    }

    private void RefreshSettings()
    {
        var s = _host.Settings;
        _syncing = true;
        EnabledSwitch.IsChecked = s.Enabled;
        StyleBox.SelectedIndex = Array.IndexOf(TraySettings.Styles, s.Style);
        var hint = TraySettings.Describe(s.Style);
        StyleHint.Text = char.ToUpperInvariant(hint[0]) + hint[1..];
        IntensitySlider.Value = s.IntensityScale;
        IntensityLabel.Text = TraySettings.IntensityName(s.IntensityScale);
        BoostSwitch.IsChecked = s.TouchpadBoost > 1.01;
        CooperationSwitch.IsChecked = s.CooperationEnabled;
        StartupSwitch.IsChecked = _host.StartWithWindows;
        ExcludedList.Children.Clear();
        foreach (var app in s.ExcludedProcesses)
        {
            ExcludedList.Children.Add(ExcludedRow(app));
        }

        ExcludedSummary.Text = s.ExcludedProcesses.Count switch
        {
            0 => "None. Every app can buzz.",
            1 => $"{s.ExcludedProcesses[0]} never buzzes",
            var n => $"{n} apps never buzz",
        };
        _syncing = false;
    }

    private void RefreshLive()
    {
        var s = _host.Settings;
        var (brush, status, hint) = !s.Enabled
            ? ("SystemFillColorNeutralBrush", "Off", "Turn Haptics on to feel the controls under your pointer.")
            : !_host.Primed
                ? ("SystemFillColorCautionBrush", "Waiting for your haptic device",
                    "Move your haptic mouse or touchpad over this window once. Windows only lets the window under the pointer use its haptics.")
                : ("SystemFillColorSuccessBrush", $"Ready: {_host.Devices}", _autoClose
                    ? "You're set. This window closes by itself; click the tray icon to come back."
                    : "Land on buttons, tabs and sliders in any app. To add another haptic device, move it over this window.");
        StatusDot.SetResourceReference(Shape.FillProperty, brush);
        StatusText.Text = status;
        StatusHint.Text = hint;
        StatsText.Text = Stats();
        var app = _host.LastApp;
        var canExclude = app is not null && !s.ExcludedProcesses.Contains(app, StringComparer.OrdinalIgnoreCase);
        ExcludeButton.Visibility = canExclude ? Visibility.Visible : Visibility.Collapsed;
        ExcludeButton.Content = $"Exclude {app}";
    }

    private string Stats()
    {
        if (_host.Stats is not { } s)
        {
            return "The engine is stopped while Haptics is off.";
        }

        return string.Join('\n',
            $"Devices        {_host.Devices}",
            $"Landing speed  {_host.LandingSpeed:0} px/s (learns from your clicks)",
            $"Raw events     {s.RawEvents} (background {s.BackgroundRawEvents})",
            $"UIA lookup     p50 {s.LookupP50:0.0} ms · p95 {s.LookupP95:0.0} ms",
            $"Decisions      {s.Emits} emitted · {s.Suppressed} suppressed",
            $"Pulse calls    {s.ApiAccepted} accepted · {s.ApiRejected} rejected · {s.Expired} too late",
            $"Input to call  p50 {s.InputToCallP50:0.0} ms · p95 {s.InputToCallP95:0.0} ms");
    }

    private Grid ExcludedRow(string app)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new Wpf.Ui.Controls.TextBlock { Text = app, VerticalAlignment = VerticalAlignment.Center });
        var remove = new Wpf.Ui.Controls.Button
        {
            Content = "Remove",
            Icon = new SymbolIcon(SymbolRegular.Dismiss24),
            Appearance = ControlAppearance.Transparent,
        };
        System.Windows.Automation.AutomationProperties.SetName(remove, $"Remove {app}");
        remove.Click += (_, _) => Change(s => s.ExcludedProcesses.RemoveAll(x => string.Equals(x, app, StringComparison.OrdinalIgnoreCase)));
        Grid.SetColumn(remove, 1);
        row.Children.Add(remove);
        return row;
    }
}
