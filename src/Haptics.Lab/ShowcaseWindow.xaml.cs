using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using Haptics.Core;
using Haptics.Lab.Experiments;
using Haptics.Windows;
using Application = System.Windows.Forms.Application;
using WpfControls = System.Windows.Controls;

namespace Haptics.Lab;

/// <summary>The Fluent Showcase. The engine, optional research surfaces and physical-rating protocol remain local.</summary>
public partial class ShowcaseWindow : Wpf.Ui.Controls.FluentWindow, IExperimentForm, IMessageFilter, IDisposable
{
    private static readonly (string Name, string Waveform, string Meaning, bool AppsOnly)[] Vocabulary =
    [
        ("Land", "Hover", "Land on an eligible navigation or menu control.", false),
        ("Click preview", "Click", "Explicit waveform preview only. Automatic landings use Hover, regardless of size.", false),
        ("Detent", "Step", "Turn a toggle on or off, or cross a slider tick.", false),
        ("Edge", "Collide", "Reach a slider's limit while dragging. Strip exits stay silent.", false),
        ("Align", "Align", "Apps only: a snap guide appeared.", true),
        ("Success", "Success", "Apps only: saved, dropped or sent — the app knows.", true),
        ("Error", "Error", "Apps only: rejected or failed — the app knows.", true),
    ];

    private readonly string _fp = EnvironmentProbe.CurrentFingerprint();
    private readonly ControllerBank _bank = new();
    private readonly ConcurrentQueue<PipelineEvent> _events = new();
    private readonly ObservableCollection<string> _activity = [];
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 100 };
    private readonly bool _startCompare;
    private ObservationPipeline? _pipeline;
    private CooperativeServer? _cooperation;
    private System.Windows.Forms.Timer? _blockTimer;
    private RatingPanel? _ratingPanel;
    private CompareRun? _compareRun;
    private CursorRippleOverlay? _rippleOverlay;
    private bool _rippleUnavailable;
    private double _lastPrime;
    private double _startMs;
    private bool _started;
    private bool _disposed;
    private bool _syncing;
    private bool _pausedForOtherOwner;
    private bool _visualDemo;
    private OutputOwnership? _outputOwnership;
    private bool? _compact;
    private EffectProfile _selectedStyle = EffectProfile.Focused;

    public ShowcaseWindow(bool startCompare = false)
    {
        _startCompare = startCompare;
        InitializeComponent();
        ReportOperationError = (title, message) =>
            System.Windows.MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 32);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);
        TitleIcon.Source = IconFrame(32);
        HeroImage.Source = IconFrame(256);
        Icon = TitleIcon.Source;
        StyleBox.ItemsSource = new[] { "Quiet", "Focused", "Everything" };
        StyleBox.SelectedIndex = 1;
        StyleBox.SelectionChanged += (_, _) =>
        {
            if (StyleBox.SelectedIndex >= 0)
            {
                _selectedStyle = (EffectProfile)StyleBox.SelectedIndex;
                StyleHint.Text = StyleBox.SelectedIndex switch { 0 => "Tab/inline-link Hover and slider detents/limits", 1 => "Tabs, text links, pickers, menu launchers, slider thumb and toggles", _ => "Adds other links, options, toggle-button Hover and groups" };
                ApplySettings();
            }
        };
        IntensitySlider.ValueChanged += (_, _) =>
        {
            IntensityLabel.Text = $"{IntensitySlider.Value * 10:0}%";
            ApplySettings();
        };
        OnToggled(BoostSwitch, _ => ApplySettings());
        StageLandButton.Click += (_, _) => PreviewVisualCue("Hover");
        StageBigLandButton.Click += (_, _) => PreviewVisualCue("Click");
        StageDetentButton.Click += (_, _) => PreviewVisualCue("Step");
        StageEdgeButton.Click += (_, _) => PreviewVisualCue("Collide");
        OnToggled(EngineSwitch, on =>
        {
            if (_started)
            {
                SetEngine(on);
            }
        });
        OnToggled(VisualDemoSwitch, SetVisualDemoMode);
        CompareButton.Click += (_, _) =>
        {
            if (_compareRun is null)
            {
                StartCompare();
            }
            else
            {
                StopCompare();
            }
        };
        BuildVocabulary();
        DemoContent.Content = new ShowcaseDemo();
        ActivityList.ItemsSource = _activity;
        RunExperimentButton.Click += (_, _) =>
        {
            if (ExperimentsList.SelectedItem is GridRow row)
            {
                StartChild(["run", row.Cells[0]], row.Cells[0]);
            }
        };
        OpenFixtureButton.Click += (_, _) => StartChild(["fixture"], null);
        WriteReportButton.Click += (_, _) => Process.Start(new ProcessStartInfo(Report.Write().Html) { UseShellExecute = true });
        RunsList.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source &&
                System.Windows.Controls.ItemsControl.ContainerFromElement(RunsList, source) is System.Windows.Controls.ListViewItem { DataContext: GridRow { Directory: { } dir } })
            {
                var summary = Path.Combine(dir, "summary.md");
                OpenRunPath(File.Exists(summary) ? summary : dir);
            }
        };
        RefreshLab();
        SizeChanged += (_, _) => UpdateLayoutForWidth();
        ActivityCard.SizeChanged += (_, _) => UpdateActivityHeight();
        PreviewMouseMove += (_, _) => PrimeFromPointer();
        _uiTimer.Tick += (_, _) => Drain();
        Loaded += (_, _) => OnShownCore();
        Closing += (_, _) =>
        {
            if (IsLoaded)
            {
                Wpf.Ui.Appearance.SystemThemeWatcher.UnWatch(this);
            }
        };
        Closed += (_, _) => Dispose();
        UpdateLayoutForWidth();
    }

    public string? ResultPath { get; private set; }

    internal Action<string, string> ReportOperationError { get; set; }

    internal Action<string> OpenRunPath { get; set; } =
        path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private double Intensity => IntensitySlider.Value / 10.0;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Application.RemoveMessageFilter(this);
        _uiTimer.Dispose();
        _blockTimer?.Dispose();
        _ratingPanel?.Close();
        _compareRun?.Session.Dispose();
        _pipeline?.Dispose();
        _cooperation?.Dispose();
        _rippleOverlay?.Dispose();
        _rippleOverlay = null;
        _outputOwnership?.Dispose();
        _outputOwnership = null;
        GC.SuppressFinalize(this);
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg == 0x0200) // WM_MOUSEMOVE, including the embedded WinForms surfaces and rating panels.
        {
            PrimeFromPointer();
        }

        return false;
    }

    private void PrimeFromPointer()
    {
        if (!_started || _disposed || _visualDemo)
        {
            return;
        }

        var now = MonotonicClock.Instance.NowMs;
        if (now - _lastPrime > 200)
        {
            _lastPrime = now;
            if (_pipeline is { } pipeline &&
                pipeline.LatestInput is { } input &&
                _bank.TryCapture(input.DeviceHandle, input.InputClass, input.TimestampMs, now))
            {
                _bank.ApplyTo(pipeline);
                UpdateStatus();
            }
        }
    }

    private void OnToggled(ToggleButton toggle, Action<bool> apply)
    {
        // Accessibility toggle actions raise Checked/Unchecked, not necessarily Click.
        RoutedEventHandler handler = (_, _) =>
        {
            if (!_syncing)
            {
                apply(toggle.IsChecked == true);
            }
        };
        toggle.Checked += handler;
        toggle.Unchecked += handler;
    }

    private static BitmapSource IconFrame(int pixels)
    {
        using var stream = typeof(ShowcaseWindow).Assembly.GetManifestResourceStream("app.ico")!;
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return decoder.Frames.Where(f => f.PixelWidth >= pixels).MinBy(f => f.PixelWidth)!;
    }

    private void BuildVocabulary()
    {
        foreach (var (name, waveform, meaning, appsOnly) in Vocabulary)
        {
            var card = new WpfControls.Border { Padding = new Thickness(0, 12, 0, 12), BorderThickness = new Thickness(0, 0, 0, 1) };
            card.SetResourceReference(WpfControls.Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            var grid = new WpfControls.Grid();
            grid.ColumnDefinitions.Add(new WpfControls.ColumnDefinition());
            grid.ColumnDefinitions.Add(new WpfControls.ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new WpfControls.RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new WpfControls.RowDefinition { Height = GridLength.Auto });
            var title = new WpfControls.StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            title.Children.Add(new WpfControls.TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
            var subtitle = new WpfControls.TextBlock { Text = appsOnly ? $"{waveform} · Apps only" : waveform, FontSize = 11 };
            subtitle.SetResourceReference(WpfControls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            title.Children.Add(subtitle);
            grid.Children.Add(title);
            var feel = new Wpf.Ui.Controls.Button { Content = "Feel it", Padding = new Thickness(10, 5, 10, 5), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(feel, $"Feel {name}");
            feel.Click += (_, _) => FeelIt(waveform, appsOnly);
            WpfControls.Grid.SetColumn(feel, 1);
            grid.Children.Add(feel);
            var text = new WpfControls.TextBlock { Text = meaning, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
            text.SetResourceReference(WpfControls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            WpfControls.Grid.SetRow(text, 1);
            WpfControls.Grid.SetColumnSpan(text, 2);
            grid.Children.Add(text);
            card.Child = grid;
            VocabularyPanel.Children.Add(card);
        }
    }

    internal void UpdateLayoutForWidth()
    {
        var compact = (ActualWidth > 0 ? ActualWidth : Width) < 1200;
        ActivityColumn.Width = new GridLength(compact ? 0 : 256);
        ActivityRow.Height = compact ? GridLength.Auto : new GridLength(0);
        WpfControls.Grid.SetColumn(ActivityCard, compact ? 0 : 2);
        WpfControls.Grid.SetRow(ActivityCard, compact ? 1 : 0);
        WpfControls.Grid.SetColumnSpan(ActivityCard, compact ? 2 : 1);
        ActivityCard.Margin = compact ? new Thickness(0, 12, 0, 0) : new Thickness(12, 0, 0, 0);
        ActivityCard.Padding = compact ? new Thickness(12, 4, 12, 4) : new Thickness(16);
        if (_compact != compact)
        {
            ActivityExpander.IsExpanded = !compact;
            _compact = compact;
        }

        UpdateActivityHeight();
    }

    private void UpdateActivityHeight() =>
        ActivityBody.MaxHeight = _compact == true ? 110 : Math.Max(100, ActivityCard.ActualHeight - 112);

    private void OnShownCore()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        Application.AddMessageFilter(this);
        _uiTimer.Start();
        TryAcquireOutputOwnership();
        _syncing = true;
        EngineSwitch.IsChecked = !_pausedForOtherOwner;
        _syncing = false;
        if (!_pausedForOtherOwner)
        {
            SetEngine(true);
        }

        UpdateStatus();
        if (_startCompare)
        {
            StartCompare();
        }
    }

    private void SetEngine(bool on)
    {
        if (on && _visualDemo)
        {
            return;
        }

        if (on && _pipeline is null)
        {
            TryStartEngine(pipeline => pipeline.Start());
        }
        else if (!on)
        {
            StopCompare();
            _pipeline?.Dispose();
            _pipeline = null;
            _cooperation?.Dispose();
            _cooperation = null;
            InspectorStats.Text = "The Showcase engine is not running.";
            _outputOwnership?.Dispose();
            _outputOwnership = null;
            _pausedForOtherOwner = false;
        }

        UpdateStatus();
    }

    private void SetVisualDemoMode(bool on) => ConfigureVisualDemoMode(on, null);

    internal void ConfigureVisualDemoMode(bool on, Action<ObservationPipeline>? startPipeline)
    {
        if (_visualDemo == on)
        {
            return;
        }

        if (!on)
        {
            StopVisualDemo();
            return;
        }

        _visualDemo = true;
        _syncing = true;
        VisualDemoSwitch.IsChecked = true;
        EngineSwitch.IsChecked = false;
        EngineSwitch.IsEnabled = false;
        _syncing = false;
        SetEngine(false);
        if (!TryStartVisualDemo(startPipeline ?? (pipeline => pipeline.Start())))
        {
            _visualDemo = false;
            EngineSwitch.IsEnabled = true;
            _syncing = true;
            VisualDemoSwitch.IsChecked = false;
            _syncing = false;
            UpdateStatus();
        }
    }

    internal bool TryStartVisualDemo(Action<ObservationPipeline> startPipeline)
    {
        ArgumentNullException.ThrowIfNull(startPipeline);
        if (!_visualDemo || _pipeline is not null)
        {
            return false;
        }

        ObservationPipeline? pipeline = null;
        Exception? failure = null;
        try
        {
            var config = CreatePolicy();
            config.Mode = HapticsMode.ObserveOnly;
            pipeline = new ObservationPipeline(config, new PipelineOptions { Route = OutputRoutes.DryRun });
            pipeline.Policy.ConsentGiven = true;
            pipeline.Policy.OutputRouteVerified = false;
            pipeline.Event += e => _events.Enqueue(e);
            _startMs = MonotonicClock.Instance.NowMs;
            startPipeline(pipeline);
            _pipeline = pipeline;
        }
        catch (Exception ex) when (IsFileFailure(ex) || ex is System.ComponentModel.Win32Exception or TimeoutException
            or InvalidOperationException { InnerException: System.ComponentModel.Win32Exception })
        {
            failure = ex;
            pipeline?.Dispose();
        }

        if (failure is not null)
        {
            ReportOperationError("Couldn't start Visual Demo mode",
                $"Visual Demo mode is off. No haptic output was sent.\n\n{failure.GetBaseException().Message}");
            return false;
        }

        UpdateStatus();
        return true;
    }

    private void StopVisualDemo()
    {
        _pipeline?.Dispose();
        _pipeline = null;
        _visualDemo = false;
        _syncing = true;
        VisualDemoSwitch.IsChecked = false;
        _syncing = false;
        EngineSwitch.IsEnabled = true;
        _rippleOverlay?.Hide();
        InspectorStats.Text = "The Showcase engine is not running.";
        UpdateStatus();
    }

    internal bool TryStartEngine(Action<ObservationPipeline> startPipeline, OutputOwnership? ownership = null)
    {
        if (ownership is not null)
        {
            _outputOwnership?.Dispose();
            _outputOwnership = ownership;
        }

        if (!TryAcquireOutputOwnership())
        {
            _syncing = true;
            EngineSwitch.IsChecked = false;
            _syncing = false;
            UpdateStatus();
            return false;
        }

        ObservationPipeline? pipeline = null;
        CooperativeServer? cooperation = null;
        var committed = false;
        Exception? failure = null;
        try
        {
            pipeline = new ObservationPipeline(CreatePolicy(),
                new PipelineOptions { Route = OutputRoutes.CachedController, OutputOwnership = _outputOwnership });
            pipeline.Policy.ConsentGiven = true;
            pipeline.Policy.OutputRouteVerified = true;
            pipeline.TouchpadBoost = BoostSwitch.IsChecked == true ? 1.3 : 1.0;
            pipeline.Event += e => _events.Enqueue(e);
            _bank.ApplyTo(pipeline);
            _startMs = MonotonicClock.Instance.NowMs;
            startPipeline(pipeline);
            cooperation = new CooperativeServer((w, pid) => _pipeline?.SubmitCooperative(w, pid));
            cooperation.Start();
            _pipeline = pipeline;
            _cooperation = cooperation;
            committed = true;
        }
        catch (Exception ex) when (IsFileFailure(ex) || ex is System.ComponentModel.Win32Exception or TimeoutException
            or InvalidOperationException { InnerException: System.ComponentModel.Win32Exception })
        {
            failure = ex;
        }
        finally
        {
            if (!committed)
            {
                // Publish no half-started engine. Deauthorize it before disposing any workers it acquired.
                if (pipeline is not null)
                {
                    pipeline.Policy.ConsentGiven = false;
                    pipeline.Policy.OutputRouteVerified = false;
                }

                cooperation?.Dispose();
                pipeline?.Dispose();
                _outputOwnership?.Dispose();
                _outputOwnership = null;
                _syncing = true;
                EngineSwitch.IsChecked = false;
                _syncing = false;
                InspectorStats.Text = "The Showcase engine is not running.";
                UpdateStatus();
            }

        }

        if (failure is not null)
        {
            ReportOperationError("Couldn't start the Showcase engine",
                $"The engine is off. You can try turning it on again.\n\n{failure.GetBaseException().Message}");
        }

        return committed;
    }

    private bool TryAcquireOutputOwnership()
    {
        _outputOwnership ??= OutputOwnership.Create("Showcase");
        _pausedForOtherOwner = !_outputOwnership.TryAcquire();
        return !_pausedForOtherOwner;
    }

    private static bool IsFileFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private PolicyConfig CreatePolicy(EffectProfile? overrideStyle = null)
    {
        var style = overrideStyle ?? _selectedStyle;
        var config = PolicyConfig.Sample(style, Intensity);
        config.CloseButtonHoverEnabled = false;
        if (_visualDemo)
        {
            config.Mode = HapticsMode.ObserveOnly;
        }
        return config;
    }

    private void ApplySettings(EffectProfile? overrideStyle = null)
    {
        if (_pipeline is null)
        {
            return;
        }

        _pipeline.Policy.Config = CreatePolicy(overrideStyle);
        _pipeline.TouchpadBoost = BoostSwitch.IsChecked == true ? 1.3 : 1.0;
    }

    private void UpdateStatus()
    {
        if (_compareRun is not null)
        {
            return;
        }

        var (brush, title, hint) = _visualDemo
            ? ("SystemAccentColorPrimaryBrush", "Visual demo active — no haptics sent",
                "Move slowly across real controls here or in any app. A ripple at the pointer shows where a haptic cue would play; nothing is sent, even if a haptic controller is present.")
            : _pipeline is null
            ? ("SystemFillColorNeutralBrush", _pausedForOtherOwner ? "Output paused — another engine owns haptics" : "Showcase engine off",
                _pausedForOtherOwner
                    ? "Another Haptics Everywhere engine owns output. Turn on Visual demo to see where cues would play without haptics."
                    : "Turn on the Showcase engine to explore background feedback, or Visual demo to see cues without haptics.")
            : _bank.Any
                ? ("SystemFillColorSuccessBrush", $"Ready: {_bank.Describe()}", "Try Feel it, then explore here or in another app. Only you can confirm what you feel.")
                : ("SystemFillColorCautionBrush", "Waiting for your haptic device", "Move each haptic mouse or touchpad over this window. No haptic device? Turn on Visual demo to see where cues would play.");
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        StatusText.Text = title;
        StatusHint.Text = hint;
    }

    private void FeelIt(string waveform, bool appsOnly)
    {
        PresentVisualCue(waveform, null, null, "Feel it preview requested; device output status is separate.");
        if (_visualDemo)
        {
            return;
        }

        SendPreview(waveform, appsOnly, w => CooperativeServer.Send(w), preview =>
        {
            var attempt = HapticsApi.SendViaThreadManager(OutputRoutes.ForegroundManager, preview, Intensity);
            if (!attempt.Accepted)
            {
                Felt($"Couldn't play {preview}: {attempt.Error ?? "no haptic device accepted it"}. Try Visual demo to see it instead.");
            }
        });
    }

    internal static bool TryCooperativePreview(bool appsOnly, string waveform, Func<string, bool> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        return appsOnly && send(waveform);
    }

    internal static void SendPreview(string waveform, bool appsOnly, Func<string, bool> sendCooperative, Action<string> sendOneShot)
    {
        ArgumentNullException.ThrowIfNull(sendOneShot);
        if (!TryCooperativePreview(appsOnly, waveform, sendCooperative))
        {
            RunOneShotPreview(() => sendOneShot(waveform));
        }
    }

    internal static void RunOneShotPreview(Action output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output();
    }

    private void Drain()
    {
        if (_pipeline is not null)
        {
            var s = _pipeline.Stats();
            InspectorStats.Text = $"raw {s.RawEvents} (bg {s.BackgroundRawEvents})  uia p50 {s.LookupP50:0.0}/p95 {s.LookupP95:0.0}ms  emit {s.Emits} supp {s.Suppressed}  sent {s.ApiAccepted} rejected {s.ApiRejected} late {s.Expired}  dwell small {_pipeline.Interpreter.Config.SmallTargetDwellMs:0}ms / tabs+large {_pipeline.Interpreter.Config.LandingDwellMs:0}ms";
        }

        var n = 0;
        while (n++ < 300 && _events.TryDequeue(out var e))
        {
            switch (e)
            {
                case DecisionEvent d when d.Decision.Reason != "no-haptic-for-event":
                    Row(e.AtMs, d.Decision.Event.Kind.ToString(), d.Decision.Event.ControlType, d.Decision.Outcome.ToString(), d.Decision.Reason, d.Decision.Intent?.Waveform,
                        $"conf {d.Decision.Event.Confidence:0.00} speed {d.Decision.Event.PointerSpeedPxPerSec:0}px/s");
                    if (_compareRun is null &&
                        (d.Decision.Outcome == PolicyOutcome.Emit ||
                         (_visualDemo && d.Decision.Outcome == PolicyOutcome.DryRun)) &&
                        d.Decision.Intent is { } intent)
                    {
                        PresentVisualCue(intent.Waveform, d.PointerX, d.PointerY,
                            _visualDemo
                                ? "Visual Demo is observe-only; no physical output is attempted."
                                : "Policy-approved intent; the animation is not output or felt evidence.");
                    }

                    if (Friendly(d.Decision) is { } text)
                    {
                        Felt(text);
                    }

                    break;
                case NoteEvent note:
                    Row(e.AtMs, "Note", note.Element?.ControlType ?? "", "", note.Note, null, "");
                    Felt(note.Note switch
                    {
                        "skip:already-selected" => "silent — you're already on this one",
                        "skip:too-small" => "silent — too small to be a real target",
                        "skip:inline-link" => "silent — a link inside text",
                        "passed-through" => "silent — left before dwell completed",
                        _ => $"silent — {note.Note}",
                    }, quiet: true);
                    break;
                case OutputEvent o when o.Attempt is { Accepted: false } a:
                    Row(e.AtMs, "Output", "", o.Status, a.Route, o.Intent.Waveform, a.Error ?? "");
                    break;
                case DiagnosticEvent g:
                    Row(e.AtMs, "Diagnostic", "", "", g.Message, null, "");
                    break;
                case DeviceChangeEvent { Arrived: false } device when _bank.RemoveDevice(device.Handle):
                    _bank.ApplyTo(_pipeline);
                    UpdateStatus();
                    break;
            }
        }

        while (InspectorList.Items.Count > 500)
        {
            InspectorList.Items.RemoveAt(InspectorList.Items.Count - 1);
        }
    }

    private void Row(double at, string a, string b, string c, string d, string? w, string detail) =>
        InspectorList.Items.Insert(0, new GridRow([$"{at - _startMs:0}", a, b, c, d, w ?? "", detail]));

    private void Felt(string text, bool quiet = false)
    {
        if (_compareRun is not null || (quiet && _activity.Count > 0 && _activity[0] == text))
        {
            return;
        }

        _activity.Insert(0, text);
        ActivityEmpty.Visibility = Visibility.Collapsed;
        while (_activity.Count > 40)
        {
            _activity.RemoveAt(_activity.Count - 1);
        }
    }

    internal void PreviewVisualCue(string waveform) =>
        PresentVisualCue(waveform, null, null, "Visual-only preview; no device output was requested.");

    private void PresentVisualCue(string waveform, int? pointerX, int? pointerY, string status)
    {
        var cue = HapticVisualCue.ForWaveform(waveform);
        StageCueTitle.Text = cue.Label;
        StageCueWaveform.Text = $"{cue.Waveform} · visual cue";
        StageCueStatus.Text = _visualDemo
            ? $"{status} Visual Demo is observe-only; no physical output is attempted."
            : _bank.Any
                ? $"{status} A controller is captured; the visual still does not prove a pulse was felt."
                : $"{status} No haptic controller is captured; this shows where policy would allow a cue, but no physical output was sent.";

        var width = Math.Max(1, StageCanvas.ActualWidth);
        var height = Math.Max(1, StageCanvas.ActualHeight);
        HapticVisualRenderer.Render(StageCanvas, cue,
            new System.Windows.Point(width / 2, height / 2), Math.Min(width, height));

        if (!_visualDemo || _rippleUnavailable)
        {
            return;
        }

        var point = pointerX is { } x && pointerY is { } y ? (X: x, Y: y) : DesktopContext.CursorPosition();
        if (point.X == int.MinValue || point.Y == int.MinValue)
        {
            return;
        }

        try
        {
            _rippleOverlay ??= new CursorRippleOverlay();
            _rippleOverlay.Play(cue, point.X, point.Y);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _rippleOverlay?.Dispose();
            _rippleOverlay = null;
            _rippleUnavailable = true;
            ReportOperationError("Pointer ripple unavailable",
                $"The Stage visualizer is still available, but the pointer ripple has been turned off.\n\n{ex.Message}");
        }
    }

    internal static string? Friendly(PolicyDecision d)
    {
        var e = d.Event;
        var what = e.Kind switch
        {
            SemanticEventKind.CrossGroupBoundary => $"entered a {e.Element?.ParentControlType?.ToLowerInvariant() ?? "group"}",
            SemanticEventKind.GroupEdge => $"end of the {e.Element?.ParentControlType?.ToLowerInvariant() ?? "strip"}",
            SemanticEventKind.EnterControl when e.Element is { IsSliderThumb: true } => "landed on a slider thumb",
            SemanticEventKind.EnterControl => $"landed on a {Human(e.ControlType)}",
            SemanticEventKind.RangeStep => "slider tick",
            SemanticEventKind.RangeLimit => "slider end",
            SemanticEventKind.ToggleChanged => $"{Human(e.ControlType)} turned {(e.Element?.IsToggledOn == true ? "on" : "off")}",
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
                IntentKind.Click => "Click",
                IntentKind.Step => "Detent",
                IntentKind.Collide => "Edge",
                _ => i.Waveform,
            };
            return $"{feel} — {what}";
        }

        var why = d.Reason switch
        {
            "coalesced-with-group-boundary" => null,
            "reentry-cooldown" => "just felt this one",
            "density-backoff" => "busy app: thinning ticks",
            "disabled-control" => "it's disabled",
            "protected-context" => "password field",
            "process-excluded" => "excluded app",
            "stale-intent" => "answer came too late",
            "focused-target-not-enabled" => "not a Focused landing target",
            "rule-disabled:group-entry" => "Quiet/Focused skip group cues",
            "rule-disabled:strip-edge" => "strip exits are disabled",
            "toggle-hover-disabled" => "checkbox hover or pressed-toggle hover is disabled",
            "slider-track-hover" => "only the slider thumb hovers",
            "slider-drag-hover" => "slider drag uses detents, not hover",
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
        if (_visualDemo)
        {
            System.Windows.MessageBox.Show(this, "Turn Visual Demo mode off before comparing physical haptic styles.", "Compare styles");
            return;
        }

        if (_pipeline is null)
        {
            System.Windows.MessageBox.Show(this, "Turn the Showcase engine on after the other output owner releases haptics.", "Compare styles");
            return;
        }

        if (System.Windows.MessageBox.Show(this,
                "Blind comparison (EXP-090): three 60-second blocks, one per style, in random order. Use your PC normally — any apps. " +
                "After each block a small panel asks how helpful and how annoying it felt. This window won't show which style is on.\n\nStart?",
                "Compare styles", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
        {
            return;
        }

        TryStartComparison(() => ExperimentSession.Start("EXP-090", "physical", null));
        if (_compareRun is not null)
        {
            _compareRun.Session.Result.Method["outputOwner"] = _outputOwnership?.Owner;
            _compareRun.Session.Result.Method["outputOwnershipGranted"] = _outputOwnership?.IsOwner == true;
        }
    }

    internal bool TryStartComparison(Func<ExperimentSession> startSession)
    {
        ResultPath = null;
        try
        {
            var order = new[] { EffectProfile.Quiet, EffectProfile.Focused, EffectProfile.Everything }.OrderBy(_ => Random.Shared.Next()).ToList();
            // Create the artifacts before hiding controls or changing the active style.
            _compareRun = new CompareRun(order, startSession());
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            ReportOperationError("Couldn't start the comparison",
                $"The comparison did not start and no result was reported. Your engine settings are unchanged.\n\n{ex.Message}");
            return false;
        }

        SetComparisonPresentation(true);
        NextBlock();
        return true;
    }

    internal void SetComparisonPresentation(bool comparing)
    {
        FeelSettings.Visibility = comparing ? Visibility.Hidden : Visibility.Visible;
        VocabularyPanel.IsEnabled = !comparing;
        StageTab.IsEnabled = !comparing;
        VisualDemoSwitch.IsEnabled = !comparing;
        CompareButton.IsEnabled = !comparing && !_visualDemo;
        InspectorTab.IsEnabled = !comparing;
        LabTab.IsEnabled = !comparing;
        ActivityList.Visibility = comparing ? Visibility.Hidden : Visibility.Visible;
        ActivityEmpty.Text = comparing
            ? "Activity is hidden during the blind comparison. Use your PC normally; rate each block when asked."
            : "Your next landing starts here. With the Showcase engine on, decisions appear as you explore.";
        ActivityEmpty.Visibility = comparing || _activity.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CompareButton.Content = comparing ? "Stop comparison" : "Compare styles";
        if (comparing)
        {
            _rippleOverlay?.Hide();
            Pages.SelectedIndex = 0;
        }
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
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SystemAccentColorPrimaryBrush");
        StatusText.Text = $"Blind comparison · Block {run.Index + 1} of {run.Order.Count}";
        StatusHint.Text = $"Use your PC normally. Rating in {remaining} s.";
        _blockTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _blockTimer.Tick += (_, _) =>
        {
            remaining--;
            StatusHint.Text = $"Use your PC normally. Rating in {remaining} s.";
            if (remaining > 0)
            {
                return;
            }

            _blockTimer.Dispose();
            _blockTimer = null;
            StatusHint.Text = "Rate this block in the small panel. The style stays hidden.";
            _ratingPanel = new RatingPanel($"Block {run.Index + 1} of {run.Order.Count}: how did the haptics feel?", (helpful, annoying) =>
            {
                run.Ratings.Add((style, helpful, annoying));
                run.Session.Artifacts.Trace(new { type = "rating", block = run.Index + 1, style = style.ToString(), helpful, annoying });
                _ratingPanel!.Close();
                _ratingPanel = null;
                run.Index++;
                NextBlock();
            });
            _ratingPanel.Show();
        };
        _blockTimer.Start();
    }

    private void StopCompare()
    {
        if (_compareRun is null)
        {
            return;
        }

        _blockTimer?.Dispose();
        _blockTimer = null;
        _ratingPanel?.Close();
        _ratingPanel = null;
        var session = _compareRun.Session;
        _compareRun = null;
        Exception? failure = null;
        try
        {
            session.Dispose();
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            failure = ex;
        }
        finally
        {
            SetComparisonPresentation(false);
            ApplySettings();
            UpdateStatus();
        }

        if (failure is not null)
        {
            ReportOperationError("Couldn't close the comparison artifacts",
                $"The comparison is stopped and your engine settings are restored. Its files may be incomplete.\n\n{failure.Message}");
        }
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
        StopCompare();
        RefreshLab();
        System.Windows.MessageBox.Show(this, r.Explanation + $"\n\nSaved {ResultPath}", "Compare styles — result");
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
        p.Exited += (_, _) =>
        {
            if (!_disposed)
            {
                Dispatcher.BeginInvoke(() => { if (!_disposed) { RefreshLab(); } });
            }
        };
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
        GatesList.Items.Clear();
        foreach (var g in EvidenceLedger.Gates(results, _fp))
        {
            GatesList.Items.Add(new GridRow([g.Gate, g.State, g.Detail]));
        }

        ExperimentsList.Items.Clear();
        foreach (var e in ExperimentCatalog.All)
        {
            var latest = EvidenceLedger.Latest(results, e.Id, _fp)?.Result.Status.ToString() ?? "NOT_RUN";
            ExperimentsList.Items.Add(new GridRow([e.Id, e.Title, e.Mode, e.Implemented ? "yes" : "no", latest, e.Summary]));
        }

        RunsList.Items.Clear();
        foreach (var r in results.AsEnumerable().Reverse())
        {
            RunsList.Items.Add(new GridRow([$"{r.Result.EndedAtUtc:yyyy-MM-dd HH:mm:ss}", r.Result.ExperimentId, r.Result.Status.ToString(), r.Result.EvidenceClass.ToString(), r.Result.Explanation], r.Directory));
        }
    }

    internal sealed record GridRow(string[] Cells, string? Directory = null)
    {
        public override string ToString() => string.Join(" ", Cells);
    }

    private sealed class CompareRun(List<EffectProfile> order, ExperimentSession session)
    {
        public List<EffectProfile> Order { get; } = order;

        public ExperimentSession Session { get; } = session;

        public List<(EffectProfile Style, int Helpful, int Annoying)> Ratings { get; } = new();

        public int Index { get; set; }
    }
}
