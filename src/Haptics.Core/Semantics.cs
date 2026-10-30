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
    public string? FrameworkId { get; init; }
    public string? RuntimeIdHash { get; init; }
    public RectD? Bounds { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool IsOffscreen { get; init; }
    public bool IsPassword { get; init; }
    public bool IsControlElement { get; init; } = true;

    /// <summary>Selection state for tabs/radio buttons (read-only SelectionItem property). Null when not exposed.</summary>
    public bool? IsSelected { get; init; }

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
    string? RequestedWaveform = null);

public sealed class InterpreterConfig
{
    public double GeometryHysteresisPx { get; set; } = 5;
    public double MaxSnapshotAgeMs { get; set; } = 250;
    public double SupersededDistancePx { get; set; } = 24;
    public int RangeTicksWhenNoLargeChange { get; set; } = 10;

    /// <summary>Arrivals fire immediately at or below this pointer speed, otherwise after a dwell. 0 = fire on every crossing.</summary>
    public double LandingSpeedPxPerSec { get; set; } = 1200;

    /// <summary>Time inside a target after which an arrival fires even if the pointer was fast (it has stopped there).</summary>
    public double LandingDwellMs { get; set; } = 80;

    /// <summary>Learn the landing speed from how fast this user moves just before clicking (≥ 8 clicks).</summary>
    public bool AdaptiveLanding { get; set; } = true;

    /// <summary>Targets smaller than this (logical px, smaller side) are ignored.</summary>
    public double MinTargetPx { get; set; } = 10;

    public bool SkipInlineLinks { get; set; } = true;
    public bool SkipSelectedChoices { get; set; } = true;

    /// <summary>How long the last group is remembered across gaps (separators, padding) between its controls.</summary>
    public double GroupMemoryMs { get; set; } = 1500;
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
    private Arrival? _pending;
    private (string Key, double At)? _lastGroup;
    private bool _movedSinceSnapshot;
    private bool _hadSnapshot;
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
        _lastGroup = null;
        _last = null;
        _speed = 0;
        _hadSnapshot = false;
        _movedSinceSnapshot = false;
        return new InterpretResult([], [$"state-cleared:{reason}"]);
    }

    /// <summary>Landing speed in effect: configured, or learned from this user's pre-click speeds.</summary>
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

                notes.AddRange(Invalidate("foreground-changed").Notes);
            }
            else if (last.DeviceHandle != p.DeviceHandle || last.InputClass != p.InputClass)
            {
                Generation++;
                _pending = null;
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

            if (last.PrimaryDown && !p.PrimaryDown)
            {
                notes.Add("primary-released");
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

        _last = p;
        if (_current?.GroupKey is { } g && _lastGroup?.Key == g)
        {
            _lastGroup = (g, p.TimestampMs);
        }

        if (_pending is { } a)
        {
            if (a.Element.Bounds is { } b && !b.Contains(p.X, p.Y, Config.GeometryHysteresisPx))
            {
                _pending = null;
                notes.Add("passed-through");
            }
            else if (_speed <= EffectiveLandingSpeed)
            {
                Arrive(a, p, p.TimestampMs, _speed, "landed:slowed", events);
            }
        }

        return new InterpretResult(events, notes);
    }

    /// <summary>Resolve a pending arrival once the pointer has rested inside it for the dwell time.</summary>
    public InterpretResult OnTick(double nowMs)
    {
        if (_pending is not { } a || _last is not { } p || nowMs - a.Since < Config.LandingDwellMs)
        {
            return InterpretResult.Empty;
        }

        if (a.Element.Bounds is { } b && !b.Contains(p.X, p.Y, Config.GeometryHysteresisPx))
        {
            _pending = null;
            return new InterpretResult([], ["passed-through"]);
        }

        var events = new List<SemanticEvent>();

        // The cause is "the pointer has stopped here", so the event is timed now and the pointer counts as still.
        Arrive(a, p, nowMs, 0, "landed:dwell", events);
        return new InterpretResult(events, []);
    }

    public InterpretResult OnDeviceUnavailable(double nowMs)
    {
        var p = _last ?? new PointerObservation(nowMs, 0, 0, InputOrigin.SyntheticOrUnknown, false, 0);
        var events = new List<SemanticEvent> { Make(SemanticEventKind.DeviceUnavailable, p with { TimestampMs = nowMs }, null, 1.0, ["device-removed"]) };
        Invalidate("device-unavailable");
        return new InterpretResult(events, ["state-cleared:device-unavailable"]);
    }

    /// <summary>An app-reported event (cooperative integration). The app knows the outcome; we only arbitrate.</summary>
    public SemanticEvent Cooperative(string waveform, int processId, double nowMs) =>
        new(SemanticEventKind.Cooperative, ++_transition, nowMs, nowMs, processId, "App", null, null, 1.0,
            ["cooperative-app"], _last?.PrimaryDown ?? false, 0, null, waveform);

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

        if (s.Error is not null)
        {
            if (_current?.Bounds is { } cb && !cb.Contains(p.X, p.Y, Config.GeometryHysteresisPx))
            {
                events.Add(Make(SemanticEventKind.LeaveControl, p, _current, 0.6, ["lookup-error", s.Error]));
                ResetElement();
            }

            notes.Add($"lookup-error:{s.Error}");
            return new InterpretResult(events, notes);
        }

        var interactive = IsTarget(s, notes);
        var moved = _movedSinceSnapshot || !_hadSnapshot;
        _movedSinceSnapshot = false;
        _hadSnapshot = true;
        if (_current is not null && s.ElementKey == _current.ElementKey)
        {
            _current = s;
            if (s.ControlType == "Slider" && s.Range is { } r)
            {
                RangeTransitions(p, s, r, events);
            }

            return new InterpretResult(events, notes);
        }

        // Jitter guard: a different element reported for a point that is only just outside the current one.
        if (_current?.Bounds is { } held && held.Contains(s.QueryX, s.QueryY, Config.GeometryHysteresisPx) && !held.Contains(s.QueryX, s.QueryY))
        {
            notes.Add("hysteresis-hold");
            return new InterpretResult(events, notes);
        }

        // UI changed underneath a pointer that did not move since the last snapshot: never emit a hover for that.
        if (!moved)
        {
            _current = interactive ? s : null;
            _pending = null;
            InitRange(s);
            notes.Add("stationary-ui-change-silent");
            return new InterpretResult(events, notes);
        }

        var previous = _current;
        _pending = null;
        if (previous is not null)
        {
            events.Add(Make(SemanticEventKind.LeaveControl, p, previous, 0.9, ["identity-changed"]));
            if (previous.ParentBounds is { } pb && previous.GroupKey is not null && s.GroupKey != previous.GroupKey &&
                ExitsAlongLongAxis(pb, s.QueryX, s.QueryY) && _speed <= EffectiveLandingSpeed * 1.5)
            {
                _transition++;
                events.Add(Make(SemanticEventKind.GroupEdge, p, previous, 0.8, ["left-strip-end", $"group:{previous.ParentControlType}"]));
            }
        }

        if (!interactive)
        {
            _current = null;
            notes.Add($"non-interactive:{s.ControlType}");
            return new InterpretResult(events, notes);
        }

        _current = s;
        InitRange(s);
        var arrival = new Arrival(s, p.TimestampMs);
        if (_speed <= EffectiveLandingSpeed)
        {
            Arrive(arrival, p, p.TimestampMs, _speed, "landed:slow-entry", events);
        }
        else
        {
            _pending = arrival;
            notes.Add("arrival-pending");
        }

        return new InterpretResult(events, notes);
    }

    private void Arrive(Arrival a, PointerObservation p, double at, double speed, string how, List<SemanticEvent> events)
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

        _transition++;
        var when = p with { TimestampMs = at };
        if (s.GroupKey is { } g)
        {
            var known = _lastGroup is { } lg && lg.Key == g && at - lg.At <= Config.GroupMemoryMs;
            if (!known)
            {
                events.Add(Make(SemanticEventKind.CrossGroupBoundary, when, s, Math.Min(1, confidence), [.. evidence, $"group:{s.ParentControlType}"], speed));
            }

            _lastGroup = (g, at);
        }

        events.Add(Make(SemanticEventKind.EnterControl, when, s, Math.Min(1, confidence), evidence, speed));
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
        if (!SemanticVocabulary.InteractiveTypes.Contains(s.ControlType) || !s.IsControlElement || s.IsOffscreen)
        {
            return false;
        }

        if (s.LogicalMinSide is { } side && side < Config.MinTargetPx)
        {
            notes.Add("skip:too-small");
            return false;
        }

        if (Config.SkipInlineLinks && s.ControlType == "Hyperlink" && s.ParentControlType is { } pt && SemanticVocabulary.TextContainers.Contains(pt))
        {
            notes.Add("skip:inline-link");
            return false;
        }

        if (Config.SkipSelectedChoices && s.IsSelected == true && SemanticVocabulary.DetentTypes.Contains(s.ControlType))
        {
            notes.Add("skip:already-selected");
            return false;
        }

        return true;
    }

    private static bool ExitsAlongLongAxis(RectD strip, double x, double y)
    {
        if (strip.Contains(x, y))
        {
            return false;
        }

        var inRows = y >= strip.Y && y < strip.Y + strip.Height;
        var inCols = x >= strip.X && x < strip.X + strip.Width;
        return strip.Width >= strip.Height ? inRows && !inCols : inCols && !inRows;
    }

    private void ResetElement()
    {
        _current = null;
        _pending = null;
        _lastTick = null;
        _atMin = _atMax = false;
    }

    private SemanticEvent Make(SemanticEventKind kind, PointerObservation p, ElementSnapshot? s, double confidence, IReadOnlyList<string> evidence, double? speed = null) =>
        new(kind, _transition, p.TimestampMs, p.TimestampMs, s?.ProcessId ?? p.ForegroundPid, s?.ControlType ?? "None",
            s?.ElementKey, s?.GroupKey, confidence, evidence, p.PrimaryDown, speed ?? _speed, s);

    private static double Distance(double x1, double y1, double x2, double y2) => Math.Sqrt(Math.Pow(x1 - x2, 2) + Math.Pow(y1 - y2, 2));

    private sealed record Arrival(ElementSnapshot Element, double Since);
}
