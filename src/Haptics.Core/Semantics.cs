using System.Text.Json.Serialization;

namespace Haptics.Core;

[JsonConverter(typeof(JsonStringEnumConverter<InputOrigin>))]
public enum InputOrigin
{
    /// <summary>Raw Input from a real device handle (hDevice != 0).</summary>
    HardwareRaw,

    /// <summary>Raw Input without a device handle (e.g. SendInput or some precision touchpads). Never physical proof.</summary>
    SyntheticOrUnknown,

    /// <summary>Authored/recorded trace replayed through the logic. Proves logic only.</summary>
    Replay,
}

[JsonConverter(typeof(JsonStringEnumConverter<PointerInputClass>))]
public enum PointerInputClass
{
    Handleless,
    Handle,
    Promoted,
    /// <summary>Pen promotion signature; identifies a class, not an individual pen or physical input.</summary>
    Pen,
}

[method: JsonConstructor]
public readonly record struct PointerObservation(
    double TimestampMs,
    int X,
    int Y,
    InputOrigin Origin,
    bool PrimaryDown,
    int ForegroundPid,
    long DeviceHandle = 0)
{
    /// <summary>Attribution metadata only; neither a nonzero handle nor this class proves physical input.</summary>
    public PointerInputClass InputClass { get; init; } =
        DeviceHandle == 0 ? PointerInputClass.Handleless : PointerInputClass.Handle;
}

public sealed record RectD(double X, double Y, double Width, double Height)
{
    public bool Contains(double px, double py, double inflate = 0) =>
        px >= X - inflate && py >= Y - inflate && px < X + Width + inflate && py < Y + Height + inflate;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public sealed record RangeInfo(double Value, double Minimum, double Maximum, double SmallChange, double LargeChange);

[JsonConverter(typeof(JsonStringEnumConverter<CaptionButton>))]
public enum CaptionButton
{
    None,
    Close,
    Minimize,
    Maximize,
}

/// <summary>
/// Minimal, privacy-preserving UI metadata at a screen point (SPEC §3.4). Deliberately has no Name, Value
/// text, or document content. <see cref="AutomationId"/> is only populated for our own fixtures.
/// </summary>
public sealed record ElementSnapshot
{
    public string Source { get; init; } = "uia";
    public int ProcessId { get; init; }
    public long Hwnd { get; init; }
    public string ControlType { get; init; } = "Unknown";
    public string? ClassName { get; init; }
    public string? FrameworkId { get; init; }
    public string? RuntimeIdHash { get; init; }
    public RectD? Bounds { get; init; }
    /// <summary>Bounds of a directly hit UIA Thumb, while Bounds and identity remain those of its owning slider.</summary>
    public RectD? SliderThumbBounds { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool IsOffscreen { get; init; }
    public bool IsPassword { get; init; }
    public bool IsControlElement { get; init; } = true;

    /// <summary>Selection state for tabs/radio buttons (read-only SelectionItem property). Null when not exposed.</summary>
    public bool? IsSelected { get; init; }

    public bool IsToggle { get; init; }
    public bool? IsToggledOn { get; init; }
    public bool HasDropdown { get; init; }
    public bool IsBrowserToolbarCommand { get; init; }
    public bool IsButtonOwner { get; init; }
    /// <summary>Resolved from a direct Text/Image child using the sole parent hop; no grandparent was read.</summary>
    public bool IsHeaderOwner { get; init; }
    public CaptionButton CaptionButton { get; init; }

    public string? ParentControlType { get; init; }
    public string? ParentRuntimeIdHash { get; init; }
    public RectD? ParentBounds { get; init; }
    public RangeInfo? Range { get; init; }
    public string? AutomationId { get; init; }

    /// <summary>Monitor scale (physical px per logical px, 1.0 = 96 DPI) so size rules mean the same at any DPI.</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>Timestamp of the pointer observation this lookup was requested for (t0).</summary>
    public double RequestedAtMs { get; init; }

    /// <summary>Interaction generation captured by the caller when requesting this lookup.</summary>
    [JsonIgnore]
    public long Generation { get; init; }

    public double ReceivedAtMs { get; init; }
    public int QueryX { get; init; }
    public int QueryY { get; init; }
    public double LookupMs { get; init; }
    public string? Error { get; init; }

    public string ElementKey => $"{ProcessId}:{RuntimeIdHash ?? $"{ControlType}@{Bounds?.X},{Bounds?.Y},{Bounds?.Width},{Bounds?.Height}"}";

    public string? GroupKey => ParentRuntimeIdHash is not null && ParentControlType is not null && SemanticVocabulary.GroupTypes.Contains(ParentControlType)
        ? $"{ProcessId}:{ParentRuntimeIdHash}"
        : null;

    /// <summary>Smaller side in logical (96-DPI) pixels; null if bounds are unknown.</summary>
    public double? LogicalMinSide => Bounds is { } b ? Math.Min(b.Width, b.Height) / Math.Max(0.5, Scale) : null;

    public double? LogicalHeight => Bounds is { } b ? b.Height / Math.Max(0.5, Scale) : null;

    public double? LogicalWidth => Bounds is { } b ? b.Width / Math.Max(0.5, Scale) : null;

    public bool IsDropdownOption => ControlType == "ListItem" && ParentControlType == "ComboBox";

    public bool IsInlineHyperlink => ControlType == "Hyperlink" &&
        ParentControlType is { } parent && SemanticVocabulary.TextContainers.Contains(parent);

    /// <summary>A conservative geometry heuristic, not proof of text styling or link purpose.</summary>
    public bool IsTextSizedGroupHyperlink => ControlType == "Hyperlink" && ParentControlType == "Group" &&
        Bounds is { IsEmpty: false } b && ParentBounds is { IsEmpty: false } parent &&
        double.IsFinite(Scale) && Scale > 0 &&
        double.IsFinite(b.X) && double.IsFinite(b.Y) && double.IsFinite(b.Width) && double.IsFinite(b.Height) &&
        double.IsFinite(parent.X) && double.IsFinite(parent.Y) && double.IsFinite(parent.Width) && double.IsFinite(parent.Height) &&
        LogicalHeight <= 24 && parent.Height / b.Height >= 2 &&
        b.X >= parent.X && b.Y >= parent.Y &&
        b.X + b.Width <= parent.X + parent.Width && b.Y + b.Height <= parent.Y + parent.Height;

    public bool IsCheckBoxOrToggleButton => ControlType == "CheckBox" || (ControlType == "Button" && IsToggle);

    public bool IsSliderThumb => ControlType == "Slider" &&
        SliderThumbBounds is { IsEmpty: false } thumb && thumb.Contains(QueryX, QueryY) &&
        Bounds is { } slider && slider.Contains(QueryX, QueryY);

    public RectD? LandingBounds => ControlType == "Slider" ? SliderThumbBounds : Bounds;

    public bool IsToggleControl => IsToggle && ControlType is "CheckBox" or "Button" &&
        !IsBrowserToolbarCommand && CaptionButton == CaptionButton.None;

    public bool IsNavigationTarget => FocusedNavigationReason() is not null;

    public string? FocusedNavigationReason() => this switch
    {
        { IsBrowserToolbarCommand: true } => null,
        { IsInlineHyperlink: true } => "inline-hyperlink",
        { IsTextSizedGroupHyperlink: true } => "text-sized-group-hyperlink",
        { ControlType: "TabItem", IsHeaderOwner: true } => "navigation-tab",
        { ControlType: "MenuItem", IsHeaderOwner: true, HasDropdown: true } => "menu-launcher",
        { ControlType: "TabItem", ParentControlType: "Tab" } => "navigation-tab",
        { ControlType: "MenuItem", ParentControlType: "MenuBar" } => "menu-launcher",
        { ControlType: "MenuItem", HasDropdown: true, ParentControlType: "Menu" or "MenuItem" } => "menu-launcher",
        { ControlType: "ComboBox" } => "picker",
        { ControlType: "SplitButton", ParentControlType: "ToolBar" or "Menu" or "MenuBar" or "MenuItem" } => "menu-launcher",
        // Expand/collapse capability alone also describes content cards and tree items.
        _ => null,
    };

    public bool IsFocusedLanding =>
        !IsCheckBoxOrToggleButton &&
        CaptionButton == CaptionButton.None && ParentControlType != "TitleBar" &&
        (IsSliderThumb || IsNavigationTarget);
}

public static class SemanticVocabulary
{
    /// <summary>
    /// Things you can *do*: buttons, tabs, menu/ribbon commands, toggles, links, pickers, sliders. Content rows
    /// (ListItem, TreeItem, DataItem, HeaderItem — files, emails, grid cells) are deliberately excluded as noise.
    /// </summary>
    public static readonly IReadOnlySet<string> InteractiveTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Button", "SplitButton", "MenuItem", "TabItem", "CheckBox", "RadioButton", "Hyperlink", "ComboBox", "Slider",
    };

    public static readonly IReadOnlySet<string> GroupTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "ToolBar", "Group", "Tab", "MenuBar", "Menu", "List", "Tree", "StatusBar", "TitleBar", "Header", "DataGrid",
    };

    /// <summary>Choices where moving between options is a "detent" (one-of-many selection).</summary>
    public static readonly IReadOnlySet<string> DetentTypes = new HashSet<string>(StringComparer.Ordinal) { "TabItem", "RadioButton" };

    /// <summary>Parents that mean a hyperlink is inline in running text, not a standalone command.</summary>
    public static readonly IReadOnlySet<string> TextContainers = new HashSet<string>(StringComparer.Ordinal) { "Document", "Text", "Edit" };

    /// <summary>
    /// How far (logical px) Close's right edge may sit from its TitleBar's outer right edge. A WinForms caption at 150%
    /// measured a 1 px overhang for Close and a full button width (~47 logical px) for Maximize.
    /// </summary>
    public const double CaptionCloseEdgeTolerancePx = 2;

    /// <summary>
    /// A window's Close button, from geometry only: a Button whose one-hop parent is a TitleBar and whose bounds touch
    /// that TitleBar's outer right edge. No layout direction is read, so a mirrored (RTL) Close stays generic.
    /// </summary>
    public static bool IsCaptionClose(ElementSnapshot? s) =>
        s is { CaptionButton: CaptionButton.Close } ||
        (s is { CaptionButton: CaptionButton.None, ControlType: "Button", ParentControlType: "TitleBar", Bounds: { IsEmpty: false } b, ParentBounds: { IsEmpty: false } p } &&
         Math.Abs((p.X + p.Width) - (b.X + b.Width)) / Math.Max(0.5, s.Scale) <= CaptionCloseEdgeTolerancePx);
}

[JsonConverter(typeof(JsonStringEnumConverter<SemanticEventKind>))]
public enum SemanticEventKind
{
    EnterControl,
    LeaveControl,

    /// <summary>Arrived in a group (toolbar, ribbon group, menu, tab strip) different from the last one.</summary>
    CrossGroupBoundary,

    /// <summary>Slid off the end of a strip along its long axis ("end of the line").</summary>
    GroupEdge,
    RangeStep,
    RangeLimit,
    ToggleChanged,
    ForegroundChanged,
    DeviceUnavailable,

    /// <summary>An app told us directly (local pipe) what happened; carries <see cref="SemanticEvent.RequestedWaveform"/>.</summary>
    Cooperative,
}

public sealed record SemanticEvent(
    SemanticEventKind Kind,
    long TransitionId,
    double PointerTimestampMs,
    double DecidedAtMs,
    int ProcessId,
    string ControlType,
    string? ElementKey,
    string? GroupKey,
    double Confidence,
    IReadOnlyList<string> Evidence,
    bool PrimaryDown,
    double PointerSpeedPxPerSec,
    ElementSnapshot? Element,
    string? RequestedWaveform = null,
    long Generation = 0,
    ElementSnapshot? Destination = null);

public sealed class InterpreterConfig
{
    public EffectProfile Profile { get; set; } = EffectProfile.Focused;
    public double GeometryHysteresisPx { get; set; } = 5;
    public double MaxSnapshotAgeMs { get; set; } = 250;
    public double SupersededDistancePx { get; set; } = 24;
    public int RangeTicksWhenNoLargeChange { get; set; } = 10;

    /// <summary>Speed threshold for strip-edge interpretation; hover arrivals always require dwell.</summary>
    public double LandingSpeedPxPerSec { get; set; } = 1200;

    /// <summary>Dwell for tabs, large targets and targets without known bounds; speed never bypasses dwell.</summary>
    public double LandingDwellMs { get; set; } = 50;

    public double SmallTargetDwellMs { get; set; } = 30;
    public double LargeTargetLogicalHeight { get; set; } = 56;
    public double LargeTargetLogicalSide { get; set; } = 48;

    /// <summary>Maximum timer scheduling delay after the dwell deadline; later arrivals expire silently.</summary>
    public double LandingTickToleranceMs { get; set; } = 45;

    /// <summary>Learn the landing speed from how fast this user moves just before clicking (≥ 8 clicks).</summary>
    public bool AdaptiveLanding { get; set; } = true;

    /// <summary>Targets smaller than this (logical px, smaller side) are ignored.</summary>
    public double MinTargetPx { get; set; } = 10;

    public bool SkipSelectedChoices { get; set; } = true;

    /// <summary>How long the last group is remembered across gaps (separators, padding) between its controls.</summary>
    public double GroupMemoryMs { get; set; } = 1500;

    public double ToggleConfirmationMs { get; set; } = 150;
}

public sealed record InterpretResult(IReadOnlyList<SemanticEvent> Events, IReadOnlyList<string> Notes)
{
    public static readonly InterpretResult Empty = new([], []);
}

/// <summary>
/// Turns pointer observations + element snapshots into semantic transitions (SPEC §5). Pure and single-threaded:
/// callers must serialize access, and call <see cref="OnTick"/> periodically (a stopped pointer sends no input).
/// It never claims outcomes (no Success/drop acceptance).
/// </summary>
public sealed class SemanticInterpreter
{
    private readonly Queue<double> _clickSpeeds = new();
    private ElementSnapshot? _current;
    private ElementSnapshot? _context;
    private Arrival? _pending;
    private ToggleActivation? _toggle;
    private (string Key, double At)? _lastGroup;
    private bool _movedSinceSnapshot;
    private bool _hadSnapshot;
    private bool _leftCurrent;
    private bool _sliderThumbOccupied;
    private PointerObservation? _last;
    private double _speed;
    private int? _lastTick;
    private bool _atMin;
    private bool _atMax;
    private long _transition;

    public SemanticInterpreter(InterpreterConfig? cfg = null) => Config = cfg ?? new InterpreterConfig();

    public InterpreterConfig Config { get; }

    public ElementSnapshot? Current => _current;

    public PointerObservation? LastPointer => _last;

    public double PointerSpeedPxPerSec => _speed;

    public bool HasPendingArrival => _pending is not null;

    public long Generation { get; private set; }

    /// <summary>Revoke pending work and cached context. Callers must serialize this with all observations.</summary>
    public InterpretResult Invalidate(string reason)
    {
        Generation++;
        ResetElement();
        _context = null;
        _lastGroup = null;
        _last = null;
        _speed = 0;
        _hadSnapshot = false;
        _movedSinceSnapshot = false;
        return new InterpretResult([], [$"state-cleared:{reason}"]);
    }

    private bool ToggleFeedbackEnabled => Config.Profile != EffectProfile.Quiet;

    public bool ObservingToggle => ToggleFeedbackEnabled &&
        _current is { IsToggleControl: true, IsEnabled: true, IsPassword: false, IsOffscreen: false };

    public bool NeedsToggleRefresh(double nowMs) =>
        ToggleFeedbackEnabled && _toggle?.ReleasedAt is { } at &&
        nowMs - at <= Config.ToggleConfirmationMs;

    /// <summary>Speed threshold for strip edges, configured or learned; hover always requires dwell.</summary>
    public double EffectiveLandingSpeed
    {
        get
        {
            if (Config.LandingSpeedPxPerSec <= 0)
            {
                return double.PositiveInfinity;
            }

            if (!Config.AdaptiveLanding || _clickSpeeds.Count < 8)
            {
                return Config.LandingSpeedPxPerSec;
            }

            var sorted = _clickSpeeds.Order().ToArray();
            return Math.Clamp(2.5 * sorted[sorted.Length / 2], 400, 2500);
        }
    }

    public InterpretResult OnPointer(PointerObservation p)
    {
        var events = new List<SemanticEvent>();
        var notes = new List<string>();
        var wasPrimaryDown = _last?.PrimaryDown ?? false;
        if (_last is { } last)
        {
            var dt = p.TimestampMs - last.TimestampMs;
            if (dt > 0)
            {
                var d = Math.Sqrt(Math.Pow(p.X - last.X, 2) + Math.Pow(p.Y - last.Y, 2));
                var inst = d / dt * 1000.0;
                _speed = _speed <= 0 ? inst : (_speed * 0.6) + (inst * 0.4);
            }

            if (last.ForegroundPid != p.ForegroundPid)
            {
                events.Add(Make(SemanticEventKind.ForegroundChanged, p, null, 1.0, ["foreground-changed"]));
                if (_current is not null)
                {
                    events.Add(Make(SemanticEventKind.LeaveControl, p, _current, 1.0, ["foreground-changed"]));
                }

                Generation++;
                ResetElement();
                _lastGroup = null;
                notes.Add("state-cleared:foreground-changed");
            }
            else if (last.DeviceHandle != p.DeviceHandle || last.InputClass != p.InputClass)
            {
                Generation++;
                _pending = null;
                _toggle = null;
                _speed = 0;
                notes.Add("state-cleared:device-changed");
            }

            if (!last.PrimaryDown && p.PrimaryDown && _speed > 0)
            {
                _clickSpeeds.Enqueue(_speed);
                while (_clickSpeeds.Count > 30)
                {
                    _clickSpeeds.Dequeue();
                }
            }

            if (!last.PrimaryDown && p.PrimaryDown)
            {
                _toggle = ToggleFeedbackEnabled &&
                    _current is { IsToggleControl: true, IsToggledOn: not null, IsEnabled: true, IsPassword: false, IsOffscreen: false,
                        RuntimeIdHash: not null, Bounds: { } bounds } target &&
                    target.Generation == Generation && bounds.Contains(p.X, p.Y) && target.RequestedAtMs <= p.TimestampMs &&
                    p.TimestampMs - target.RequestedAtMs <= Config.MaxSnapshotAgeMs
                    ? new ToggleActivation(target, p.DeviceHandle, null, Generation)
                    : null;
            }

            if (last.PrimaryDown && !p.PrimaryDown)
            {
                notes.Add("primary-released");
                if (_toggle is { } toggle)
                {
                    _toggle = toggle with { ReleasedAt = p.TimestampMs };
                }
            }

            if (p.X != last.X || p.Y != last.Y)
            {
                _movedSinceSnapshot = true;
            }
        }
        else
        {
            _movedSinceSnapshot = true;
        }

        if (_toggle is { } armed &&
            (!ToggleFeedbackEnabled || armed.Generation != Generation || armed.DeviceHandle != p.DeviceHandle ||
             armed.Element.Bounds is not { } targetBounds || !targetBounds.Contains(p.X, p.Y)))
        {
            _toggle = null;
        }

        _last = p;
        if (_current?.ControlType == "Slider")
        {
            if (p.PrimaryDown)
            {
                _sliderThumbOccupied = true;
            }
            else if (!wasPrimaryDown && _current.SliderThumbBounds is { } thumb && !thumb.Contains(p.X, p.Y))
            {
                _sliderThumbOccupied = false;
            }
        }
        if (_current?.Bounds is { } currentBounds &&
            !currentBounds.Contains(p.X, p.Y, TargetHysteresis(_current)))
        {
            _leftCurrent = true;
        }

        if (_current?.GroupKey is { } g && _lastGroup?.Key == g)
        {
            _lastGroup = (g, p.TimestampMs);
        }

        if (_pending is { } a)
        {
            if (a.Element.ControlType == "Slider" && p.PrimaryDown)
            {
                _pending = null;
                notes.Add("skip:slider-drag-hover");
            }
            else if (a.Element.IsCheckBoxOrToggleButton && p.PrimaryDown)
            {
                _pending = null;
                notes.Add("skip:toggle-press-hover");
            }
            else if (p.TimestampMs > ArrivalDue(a) + Config.LandingTickToleranceMs)
            {
                _pending = null;
                notes.Add("arrival-expired");
            }
            else if (a.Element.LandingBounds is { } b && !b.Contains(p.X, p.Y, TargetHysteresis(a.Element)))
            {
                _pending = null;
                notes.Add("passed-through");
            }
        }

        return new InterpretResult(events, notes);
    }

    /// <summary>Resolve a pending arrival once the pointer has rested inside it for the dwell time.</summary>
    public InterpretResult OnTick(double nowMs)
    {
        if (_toggle is { } toggle &&
            (!ToggleFeedbackEnabled ||
             toggle.ReleasedAt is { } released && nowMs - released > Config.ToggleConfirmationMs))
        {
            _toggle = null;
        }

        if (_pending is not { } a || _last is not { } p || nowMs < ArrivalDue(a))
        {
            return InterpretResult.Empty;
        }

        var deadline = ArrivalDue(a);
        if (nowMs > deadline + Config.LandingTickToleranceMs)
        {
            _pending = null;
            return new InterpretResult([], ["arrival-expired"]);
        }

        if ((a.Element.LandingBounds is { } b && !b.Contains(p.X, p.Y, TargetHysteresis(a.Element))) ||
            (a.Element.LandingBounds is null && (p.X != a.Element.QueryX || p.Y != a.Element.QueryY)))
        {
            _pending = null;
            _leftCurrent = true;
            return new InterpretResult([], ["passed-through"]);
        }

        var events = new List<SemanticEvent>();

        Arrive(a, p, deadline, 0, "landed:dwell", events, nowMs);
        return new InterpretResult(events, []);
    }

    public InterpretResult OnDeviceUnavailable(double nowMs)
    {
        var p = _last ?? new PointerObservation(nowMs, 0, 0, InputOrigin.SyntheticOrUnknown, false, 0);
        var events = new List<SemanticEvent> { Make(SemanticEventKind.DeviceUnavailable, p with { TimestampMs = nowMs }, null, 1.0, ["device-removed"]) };
        Generation++;
        ResetElement();
        return new InterpretResult(events, ["state-cleared:device-unavailable"]);
    }

    /// <summary>An app-reported event (cooperative integration). The app knows the outcome; we only arbitrate.</summary>
    public SemanticEvent Cooperative(string waveform, int processId, double nowMs) =>
        new(SemanticEventKind.Cooperative, ++_transition, nowMs, nowMs, processId, "App", _context?.ElementKey, _context?.GroupKey, 1.0,
            ["cooperative-app"], _last?.PrimaryDown ?? false, 0, _context, waveform, Generation);

    public InterpretResult OnSnapshot(ElementSnapshot s, double nowMs)
    {
        var notes = new List<string>();
        var events = new List<SemanticEvent>();
        if (_last is not { } p)
        {
            return new InterpretResult(events, ["no-pointer-yet"]);
        }

        if (s.Generation != Generation)
        {
            return new InterpretResult(events, ["generation-snapshot-discarded"]);
        }

        if (nowMs - s.RequestedAtMs > Config.MaxSnapshotAgeMs)
        {
            return new InterpretResult(events, ["stale-snapshot-discarded"]);
        }

        if (Distance(p.X, p.Y, s.QueryX, s.QueryY) > Config.SupersededDistancePx)
        {
            return new InterpretResult(events, ["superseded-snapshot-discarded"]);
        }

        if ((_context is not null && s.ElementKey == _context.ElementKey && s.RequestedAtMs < _context.RequestedAtMs) ||
            (_current is not null && s.ElementKey == _current.ElementKey && s.RequestedAtMs < _current.RequestedAtMs))
        {
            return new InterpretResult(events, ["older-snapshot-discarded"]);
        }

        // A nearby reply can still belong to a crossed target. It must not consume the final pointer movement.
        if (s.Error is null && (s.QueryX != p.X || s.QueryY != p.Y) &&
            (s.Bounds is not { } observedBounds || !observedBounds.Contains(p.X, p.Y)))
        {
            return new InterpretResult(events, ["superseded-snapshot-discarded"]);
        }

        _context = s;

        if (_toggle is { } toggle)
        {
            if (s.Error is not null || s.ElementKey != toggle.Element.ElementKey ||
                !s.IsToggleControl || s.IsToggledOn is null || !s.IsEnabled || s.IsPassword || s.IsOffscreen)
            {
                _toggle = null;
            }
            else if (!p.PrimaryDown && toggle.ReleasedAt is { } released && s.RequestedAtMs >= released)
            {
                if (ToggleFeedbackEnabled && nowMs - released <= Config.ToggleConfirmationMs &&
                    s.IsToggledOn != toggle.Element.IsToggledOn)
                {
                    _transition++;
                    events.Add(Make(SemanticEventKind.ToggleChanged, p with { TimestampMs = released }, s, 0.9,
                        [s.IsToggledOn == true ? "toggle-state:off-to-on" : "toggle-state:on-to-off", "primary-click"]));
                    _toggle = null;
                }
            }
        }

        if (s.Error is not null)
        {
            if (_current?.Bounds is { } cb && !cb.Contains(p.X, p.Y, Config.GeometryHysteresisPx))
            {
                events.Add(Make(SemanticEventKind.LeaveControl, p, _current, 0.6, ["lookup-error", s.Error]));
                ResetElement();
            }
            else
            {
                _pending = null;
            }
            notes.Add($"lookup-error:{s.Error}");
            return new InterpretResult(events, notes);
        }

        var interactive = IsTarget(s, notes);
        var moved = _movedSinceSnapshot || !_hadSnapshot;
        if (s.QueryX == p.X && s.QueryY == p.Y)
        {
            _movedSinceSnapshot = false;
        }
        _hadSnapshot = true;
        if (_current?.ControlType == "Slider" && s.ControlType == "Slider" && s.ElementKey == _current.ElementKey)
        {
            // Track and thumb share range history, but only a new thumb visit can arm hover.
            _current = s;
            _leftCurrent = false;
            if (s.Range is { } range)
            {
                RangeTransitions(p, s, range, events);
            }
            var onThumb = s.IsSliderThumb && s.SliderThumbBounds is { } thumb && thumb.Contains(p.X, p.Y);
            var enteredThumb = onThumb && !_sliderThumbOccupied;
            _sliderThumbOccupied = onThumb || p.PrimaryDown;
            if (!interactive || !CanLand(s, p) || !onThumb || !s.IsEnabled || s.IsPassword)
            {
                _pending = null;
            }
            else if (_pending is { } pending)
            {
                _pending = pending with { Element = s, DwellMs = Math.Max(pending.DwellMs, DwellFor(s)) };
            }
            else if (enteredThumb && moved)
            {
                ArmArrival(s, p, nowMs, events, notes);
            }
            return new InterpretResult(events, notes);
        }
        if (_current is not null && s.ElementKey == _current.ElementKey &&
            s.ControlType == _current.ControlType && !_leftCurrent)
        {
            _current = s;
            if (_pending is { } pending)
            {
                _pending = !interactive || s.IsPassword || !s.IsEnabled || !CanLand(s, p)
                    ? null
                    : pending with { Element = s, DwellMs = Math.Max(pending.DwellMs, DwellFor(s)) };
            }

            return new InterpretResult(events, notes);
        }

        // Jitter guard: a different element reported for a point that is only just outside the current one.
        if (!(_current?.ControlType == "TabItem" && s.ControlType == "TabItem") &&
            _current?.Bounds is { } held && held.Contains(s.QueryX, s.QueryY, Config.GeometryHysteresisPx) && !held.Contains(s.QueryX, s.QueryY))
        {
            notes.Add("hysteresis-hold");
            return new InterpretResult(events, notes);
        }

        // UI changed underneath a pointer that did not move since the last snapshot: never emit a hover for that.
        if (!moved)
        {
            _current = interactive ? s : null;
            _leftCurrent = false;
            _sliderThumbOccupied = s.ControlType == "Slider" && (s.IsSliderThumb || p.PrimaryDown);
            _pending = null;
            InitRange(s);
            notes.Add("stationary-ui-change-silent");
            return new InterpretResult(events, notes);
        }

        var previous = _current;
        _leftCurrent = false;
        _pending = null;
        if (previous is not null)
        {
            events.Add(Make(SemanticEventKind.LeaveControl, p, previous, 0.9, ["identity-changed"]));
        }

        if (!interactive)
        {
            _current = null;
            notes.Add($"non-interactive:{s.ControlType}");
            return new InterpretResult(events, notes);
        }

        _current = s;
        _sliderThumbOccupied = s.ControlType == "Slider" && (s.IsSliderThumb || p.PrimaryDown);
        InitRange(s);
        if (!CanLand(s, p))
        {
            notes.Add(s.IsCheckBoxOrToggleButton ? "skip:toggle-hover-disabled" :
                s.ControlType == "Slider" ? p.PrimaryDown ? "skip:slider-drag-hover" : "skip:slider-track-hover" :
                "skip:focused-target-not-enabled");
            return new InterpretResult(events, notes);
        }

        ArmArrival(s, p, nowMs, events, notes);
        return new InterpretResult(events, notes);
    }

    private bool CanLand(ElementSnapshot s, PointerObservation p) =>
        s.ControlType != "CheckBox" &&
        !(s.IsCheckBoxOrToggleButton && p.PrimaryDown) &&
        (Config.Profile == EffectProfile.Everything || s.IsFocusedLanding) &&
        (s.ControlType != "Slider" || (s.IsSliderThumb && !p.PrimaryDown &&
            s.SliderThumbBounds is { } thumb && thumb.Contains(p.X, p.Y) &&
            Math.Min(thumb.Width, thumb.Height) / Math.Max(0.5, s.Scale) >= Config.MinTargetPx));

    private void ArmArrival(ElementSnapshot s, PointerObservation p, double nowMs, List<SemanticEvent> events, List<string> notes)
    {
        var arrival = new Arrival(s, p.TimestampMs, nowMs, DwellFor(s));
        _pending = arrival;
        if (nowMs >= ArrivalDue(arrival))
        {
            var landed = OnTick(nowMs);
            events.AddRange(landed.Events);
            notes.AddRange(landed.Notes);
        }
        else
        {
            notes.Add("arrival-pending");
        }
    }

    // Tab identity plus dwell handles edge crossings; extending tab bounds can attribute a landing to its neighbor.
    private double TargetHysteresis(ElementSnapshot target) => target.ControlType is "TabItem" or "Slider" ? 0 : Config.GeometryHysteresisPx;

    private void Arrive(Arrival a, PointerObservation p, double at, double speed, string how, List<SemanticEvent> events, double? decidedAt = null)
    {
        _pending = null;
        var s = a.Element;
        var evidence = new List<string> { $"uia:{s.ControlType}", how };
        var confidence = 0.6;
        if (s.Bounds is { } b && b.Contains(s.QueryX, s.QueryY))
        {
            evidence.Add("bounds-contains-point");
            confidence += 0.3;
        }

        if (s.RuntimeIdHash is not null)
        {
            evidence.Add("runtime-id");
            confidence += 0.1;
        }

        if (SemanticVocabulary.IsCaptionClose(s))
        {
            evidence.Add("caption-close");
        }

        if (Config.Profile == EffectProfile.Focused && s.IsFocusedLanding && s.FocusedNavigationReason() is { } reason)
        {
            evidence.Add($"focused:{reason}");
        }
        else if (Config.Profile == EffectProfile.Focused && s.ControlType == "Slider")
        {
            evidence.Add("focused:slider-hover");
        }

        _transition++;
        var when = p with { TimestampMs = at };
        if (s.GroupKey is { } g)
        {
            var known = _lastGroup is { } lg && lg.Key == g && at - lg.At <= Config.GroupMemoryMs;
            if (!known && !SemanticVocabulary.IsCaptionClose(s))
            {
                events.Add(Make(SemanticEventKind.CrossGroupBoundary, when, s, Math.Min(1, confidence), [.. evidence, $"group:{s.ParentControlType}"], speed) with { DecidedAtMs = decidedAt ?? at });
            }

            _lastGroup = (g, at);
        }

        events.Add(Make(SemanticEventKind.EnterControl, when, s, Math.Min(1, confidence), evidence, speed) with { DecidedAtMs = decidedAt ?? at });
    }

    private void RangeTransitions(PointerObservation p, ElementSnapshot s, RangeInfo r, List<SemanticEvent> events)
    {
        var tick = TickIndex(r);
        var atMin = r.Value <= r.Minimum;
        var atMax = r.Value >= r.Maximum;
        if (!p.PrimaryDown)
        {
            // Value changed without a physical drag (keyboard/programmatic): resync silently.
            _lastTick = tick;
            _atMin = atMin;
            _atMax = atMax;
            return;
        }

        if ((atMin && !_atMin) || (atMax && !_atMax))
        {
            _transition++;
            events.Add(Make(SemanticEventKind.RangeLimit, p, s, 0.9, ["range-value-pattern", atMin ? "at-minimum" : "at-maximum", "primary-down"]));
        }
        else if (_lastTick is { } lt && tick != lt)
        {
            _transition++;
            events.Add(Make(SemanticEventKind.RangeStep, p, s, 0.9, ["range-value-pattern", $"tick:{lt}->{tick}", "primary-down"]));
        }

        _lastTick = tick;
        _atMin = atMin;
        _atMax = atMax;
    }

    private void InitRange(ElementSnapshot s)
    {
        if (s.Range is { } r)
        {
            _lastTick = TickIndex(r);
            _atMin = r.Value <= r.Minimum;
            _atMax = r.Value >= r.Maximum;
        }
        else
        {
            _lastTick = null;
            _atMin = _atMax = false;
        }
    }

    private int TickIndex(RangeInfo r)
    {
        var span = r.Maximum - r.Minimum;
        if (span <= 0)
        {
            return 0;
        }

        var tick = r.LargeChange > 0 && r.LargeChange < span ? r.LargeChange : span / Config.RangeTicksWhenNoLargeChange;
        return (int)Math.Floor((r.Value - r.Minimum) / tick + 1e-9);
    }

    /// <summary>Is this something you can *do*, worth a tick? Records why not in <paramref name="notes"/>.</summary>
    private bool IsTarget(ElementSnapshot s, List<string> notes)
    {
        if ((!SemanticVocabulary.InteractiveTypes.Contains(s.ControlType) &&
             !(Config.Profile == EffectProfile.Everything && s.IsDropdownOption)) || !s.IsControlElement || s.IsOffscreen)
        {
            return false;
        }

        if (s.LogicalMinSide is { } side && side < Config.MinTargetPx)
        {
            notes.Add("skip:too-small");
            return false;
        }

        if (Config.SkipSelectedChoices && s.IsSelected == true && SemanticVocabulary.DetentTypes.Contains(s.ControlType))
        {
            notes.Add("skip:already-selected");
            return false;
        }

        return true;
    }

    private void ResetElement()
    {
        _current = null;
        _leftCurrent = false;
        _sliderThumbOccupied = false;
        _pending = null;
        _toggle = null;
        _lastTick = null;
        _atMin = _atMax = false;
    }

    private SemanticEvent Make(SemanticEventKind kind, PointerObservation p, ElementSnapshot? s, double confidence, IReadOnlyList<string> evidence, double? speed = null) =>
        new(kind, _transition, p.TimestampMs, p.TimestampMs, s?.ProcessId ?? p.ForegroundPid, s?.ControlType ?? "None",
            s?.ElementKey, s?.GroupKey, confidence, evidence, p.PrimaryDown, speed ?? _speed, s, Generation: Generation);

    private static double Distance(double x1, double y1, double x2, double y2) => Math.Sqrt(Math.Pow(x1 - x2, 2) + Math.Pow(y1 - y2, 2));

    private double DwellFor(ElementSnapshot target)
    {
        if (target.ControlType == "TabItem" || target.LandingBounds is not { IsEmpty: false } bounds)
        {
            return Config.LandingDwellMs;
        }

        var scale = Math.Max(0.5, target.Scale);
        return bounds.Height / scale >= Config.LargeTargetLogicalHeight ||
            Math.Min(bounds.Width, bounds.Height) / scale >= Config.LargeTargetLogicalSide
            ? Config.LandingDwellMs : Config.SmallTargetDwellMs;
    }

    private static double ArrivalDue(Arrival arrival) => Math.Max(arrival.Since + arrival.DwellMs, arrival.KnownAt);

    private sealed record Arrival(ElementSnapshot Element, double Since, double KnownAt, double DwellMs);

    private sealed record ToggleActivation(ElementSnapshot Element, long DeviceHandle, double? ReleasedAt, long Generation);
}
