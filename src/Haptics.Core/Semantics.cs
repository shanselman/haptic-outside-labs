using System.Text.Json.Serialization;

namespace Haptics.Core;

[JsonConverter(typeof(JsonStringEnumConverter<InputOrigin>))]
public enum InputOrigin
{
    /// <summary>Raw Input from a real device handle (hDevice != 0).</summary>
    HardwareRaw,

    /// <summary>Raw Input without a device handle (e.g. SendInput) or unknown origin. Never physical proof.</summary>
    SyntheticOrUnknown,

    /// <summary>Authored/recorded trace replayed through the logic. Proves logic only.</summary>
    Replay,
}

public readonly record struct PointerObservation(
    double TimestampMs,
    int X,
    int Y,
    InputOrigin Origin,
    bool PrimaryDown,
    int ForegroundPid,
    long DeviceHandle = 0);

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
    public string? ParentControlType { get; init; }
    public string? ParentRuntimeIdHash { get; init; }
    public RangeInfo? Range { get; init; }
    public string? AutomationId { get; init; }

    /// <summary>Timestamp of the pointer observation this lookup was requested for (t0).</summary>
    public double RequestedAtMs { get; init; }

    public double ReceivedAtMs { get; init; }
    public int QueryX { get; init; }
    public int QueryY { get; init; }
    public double LookupMs { get; init; }
    public string? Error { get; init; }

    public string ElementKey => $"{ProcessId}:{RuntimeIdHash ?? $"{ControlType}@{Bounds?.X},{Bounds?.Y},{Bounds?.Width},{Bounds?.Height}"}";

    public string? GroupKey => ParentRuntimeIdHash is not null && ParentControlType is not null && SemanticVocabulary.GroupTypes.Contains(ParentControlType)
        ? $"{ProcessId}:{ParentRuntimeIdHash}"
        : null;
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
}

[JsonConverter(typeof(JsonStringEnumConverter<SemanticEventKind>))]
public enum SemanticEventKind
{
    EnterControl,
    LeaveControl,
    CrossGroupBoundary,
    RangeStep,
    RangeLimit,
    ForegroundChanged,
    DeviceUnavailable,
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
    ElementSnapshot? Element);

public sealed class InterpreterConfig
{
    public double GeometryHysteresisPx { get; init; } = 5;
    public double MaxSnapshotAgeMs { get; init; } = 250;
    public double SupersededDistancePx { get; init; } = 24;
    public int RangeTicksWhenNoLargeChange { get; init; } = 10;
}

public sealed record InterpretResult(IReadOnlyList<SemanticEvent> Events, IReadOnlyList<string> Notes)
{
    public static readonly InterpretResult Empty = new([], []);
}

/// <summary>
/// Turns pointer observations + element snapshots into semantic transitions (SPEC §5). Pure and single-threaded:
/// callers must serialize access. It never claims outcomes (no Success/drop acceptance).
/// </summary>
public sealed class SemanticInterpreter
{
    private readonly InterpreterConfig _cfg;
    private ElementSnapshot? _current;
    private bool _movedSinceSnapshot;
    private bool _hadSnapshot;
    private PointerObservation? _last;
    private double _speed;
    private int? _lastTick;
    private bool _atMin;
    private bool _atMax;
    private long _transition;

    public SemanticInterpreter(InterpreterConfig? cfg = null) => _cfg = cfg ?? new InterpreterConfig();

    public ElementSnapshot? Current => _current;

    public PointerObservation? LastPointer => _last;

    public double PointerSpeedPxPerSec => _speed;

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

            if (last.ForegroundPid != p.ForegroundPid && last.ForegroundPid != 0)
            {
                events.Add(Make(SemanticEventKind.ForegroundChanged, p, null, 1.0, ["foreground-changed"]));
                if (_current is not null)
                {
                    events.Add(Make(SemanticEventKind.LeaveControl, p, _current, 1.0, ["foreground-changed"]));
                }

                ResetElement();
                notes.Add("state-cleared:foreground-changed");
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
        return new InterpretResult(events, notes);
    }

    public InterpretResult OnDeviceUnavailable(double nowMs)
    {
        var p = _last ?? new PointerObservation(nowMs, 0, 0, InputOrigin.SyntheticOrUnknown, false, 0);
        var events = new List<SemanticEvent> { Make(SemanticEventKind.DeviceUnavailable, p with { TimestampMs = nowMs }, null, 1.0, ["device-removed"]) };
        ResetElement();
        return new InterpretResult(events, ["state-cleared:device-unavailable"]);
    }

    public InterpretResult OnSnapshot(ElementSnapshot s, double nowMs)
    {
        var notes = new List<string>();
        var events = new List<SemanticEvent>();
        if (_last is not { } p)
        {
            return new InterpretResult(events, ["no-pointer-yet"]);
        }

        if (nowMs - s.RequestedAtMs > _cfg.MaxSnapshotAgeMs)
        {
            return new InterpretResult(events, ["stale-snapshot-discarded"]);
        }

        if (Distance(p.X, p.Y, s.QueryX, s.QueryY) > _cfg.SupersededDistancePx)
        {
            return new InterpretResult(events, ["superseded-snapshot-discarded"]);
        }

        if (s.Error is not null)
        {
            if (_current?.Bounds is { } cb && !cb.Contains(p.X, p.Y, _cfg.GeometryHysteresisPx))
            {
                events.Add(Make(SemanticEventKind.LeaveControl, p, _current, 0.6, ["lookup-error", s.Error]));
                ResetElement();
            }

            notes.Add($"lookup-error:{s.Error}");
            return new InterpretResult(events, notes);
        }

        var interactive = IsInteractive(s);
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
        if (_current?.Bounds is { } held && held.Contains(s.QueryX, s.QueryY, _cfg.GeometryHysteresisPx) && !held.Contains(s.QueryX, s.QueryY))
        {
            notes.Add("hysteresis-hold");
            return new InterpretResult(events, notes);
        }

        // UI changed underneath a pointer that did not move since the last snapshot: never emit a hover for that.
        if (!moved)
        {
            _current = interactive ? s : null;
            InitRange(s);
            notes.Add("stationary-ui-change-silent");
            return new InterpretResult(events, notes);
        }

        var previous = _current;
        if (previous is not null && IsInteractive(previous))
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
        InitRange(s);
        var evidence = new List<string> { $"uia:{s.ControlType}" };
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
        var newGroup = s.GroupKey;
        if (newGroup is not null && previous?.GroupKey is { } oldGroup && oldGroup != newGroup)
        {
            events.Add(Make(SemanticEventKind.CrossGroupBoundary, p, s, Math.Min(1, confidence), [.. evidence, $"group:{s.ParentControlType}"]));
        }

        events.Add(Make(SemanticEventKind.EnterControl, p, s, Math.Min(1, confidence), evidence));
        return new InterpretResult(events, notes);
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

        var tick = r.LargeChange > 0 && r.LargeChange < span ? r.LargeChange : span / _cfg.RangeTicksWhenNoLargeChange;
        return (int)Math.Floor((r.Value - r.Minimum) / tick + 1e-9);
    }

    private static bool IsInteractive(ElementSnapshot s) =>
        SemanticVocabulary.InteractiveTypes.Contains(s.ControlType) && s.IsControlElement && !s.IsOffscreen;

    private void ResetElement()
    {
        _current = null;
        _lastTick = null;
        _atMin = _atMax = false;
    }

    private SemanticEvent Make(SemanticEventKind kind, PointerObservation p, ElementSnapshot? s, double confidence, IReadOnlyList<string> evidence) =>
        new(kind, _transition, p.TimestampMs, p.TimestampMs, s?.ProcessId ?? p.ForegroundPid, s?.ControlType ?? "None",
            s?.ElementKey, s?.GroupKey, confidence, evidence, p.PrimaryDown, _speed, s);

    private static double Distance(double x1, double y1, double x2, double y2) => Math.Sqrt(Math.Pow(x1 - x2, 2) + Math.Pow(y1 - y2, 2));
}
