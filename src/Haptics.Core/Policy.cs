using System.Text.Json.Serialization;

namespace Haptics.Core;

[JsonConverter(typeof(JsonStringEnumConverter<HapticsMode>))]
public enum HapticsMode
{
    Off,
    ObserveOnly,
    On,
}

[JsonConverter(typeof(JsonStringEnumConverter<AppPolicy>))]
public enum AppPolicy
{
    OptInAllowlist,
    AllExceptExclusions,
}

[JsonConverter(typeof(JsonStringEnumConverter<EffectProfile>))]
public enum EffectProfile
{
    /// <summary>Structural boundaries and slider detents only.</summary>
    Quiet,

    /// <summary>Quiet + a light hover when entering an enabled interactive control (rate-limited).</summary>
    Balanced,

    /// <summary>Balanced with looser rate limits. Expect more density and possible native duplicates.</summary>
    Experimental,
}

[JsonConverter(typeof(JsonStringEnumConverter<IntentKind>))]
public enum IntentKind
{
    Hover,
    Step,
    Collide,
    Align,
    Click,
    Press,
    Release,
    Success,
    Error,
    Grow,
}

[JsonConverter(typeof(JsonStringEnumConverter<PolicyOutcome>))]
public enum PolicyOutcome
{
    /// <summary>Send to the haptic output route.</summary>
    Emit,

    /// <summary>Would have been emitted, but output is observe-only or the route is unverified.</summary>
    DryRun,

    Suppressed,
}

public sealed class NativeSuppression
{
    public string Id { get; init; } = "";
    public string? ProcessName { get; init; }
    public string? ControlType { get; init; }
    public bool OnlyWhenPrimaryDown { get; init; } = true;
}

/// <summary>Editable prototype defaults (SPEC §8.3). These are starting values to test, not proven UX values.</summary>
public sealed class PolicyConfig
{
    public HapticsMode Mode { get; set; } = HapticsMode.ObserveOnly;
    public EffectProfile Profile { get; set; } = EffectProfile.Balanced;
    public double IntensityScale { get; set; } = 0.25;
    public int GlobalMaxPulsesPerSecond { get; set; } = 4;
    public int MaxPulsesPerMinute { get; set; } = 120;
    public double MinimumGlobalSpacingMs { get; set; } = 120;
    public double SameElementReentryCooldownMs { get; set; } = 500;
    public double SemanticIntentExpiryMs { get; set; } = 45;
    public double HoverMaxPointerSpeedPxPerSec { get; set; } = 4000;
    public double MinimumConfidence { get; set; } = 0.5;
    public bool IncludeDisabledControls { get; set; }
    public bool GenericSuccessErrorEnabled { get; set; }
    public bool ScrollTextureEnabled { get; set; }
    public AppPolicy AppPolicy { get; set; } = AppPolicy.OptInAllowlist;
    public List<string> AllowedProcesses { get; set; } = new();

    public List<string> ExcludedProcesses { get; set; } = new()
    {
        "LogonUI", "consent", "CredentialUIBroker", "LockApp", "KeePass", "KeePassXC", "1Password", "Bitwarden",
    };

    public List<NativeSuppression> NativeSuppressions { get; set; } = new()
    {
        new NativeSuppression { Id = "powerpoint-alignment", ProcessName = "POWERPNT", OnlyWhenPrimaryDown = true },
        new NativeSuppression { Id = "windows-snap-titlebar-drag", ControlType = "TitleBar", OnlyWhenPrimaryDown = true },
    };

    public bool HoverEnabled => Profile != EffectProfile.Quiet;
    public bool GroupBoundaryEnabled { get; set; } = true;
    public bool RangeStepsEnabled { get; set; } = true;
    public bool RangeLimitsEnabled { get; set; } = true;

    public PolicyConfig Clone() => (PolicyConfig)MemberwiseClone();

    public void ApplyProfile(EffectProfile profile)
    {
        Profile = profile;
        switch (profile)
        {
            case EffectProfile.Experimental:
                GlobalMaxPulsesPerSecond = 8;
                MinimumGlobalSpacingMs = 60;
                SameElementReentryCooldownMs = 250;
                break;
            default:
                GlobalMaxPulsesPerSecond = 4;
                MinimumGlobalSpacingMs = 120;
                SameElementReentryCooldownMs = 500;
                break;
        }
    }
}

public sealed record HapticIntent(
    string Id,
    IntentKind Kind,
    string Waveform,
    double Intensity,
    double DeadlineMs,
    double Confidence,
    string SourceRule,
    int ProcessId,
    double PointerTimestampMs,
    string? ElementKey);

public sealed record PolicyDecision(
    PolicyOutcome Outcome,
    string Reason,
    HapticIntent? Intent,
    SemanticEvent Event,
    double DecidedAtMs);

/// <summary>
/// The single global arbitration point (SPEC §8.2). Every source (generic rules, future adapters) must pass
/// through here; nothing reaches an actuator otherwise. Not thread-safe: callers serialize.
/// </summary>
public sealed class HapticPolicy
{
    private readonly Func<int, string?> _processName;
    private readonly Dictionary<string, double> _lastByElement = new();
    private readonly Queue<double> _recent = new();
    private double _lastEmitMs = double.NegativeInfinity;
    private long _lastTransitionEmitted = -1;
    private long _intentSeq;

    public HapticPolicy(PolicyConfig config, Func<int, string?> processName)
    {
        Config = config;
        _processName = processName;
    }

    public PolicyConfig Config { get; set; }

    public bool ConsentGiven { get; set; }

    /// <summary>Set only from physically confirmed evidence for the selected output route.</summary>
    public bool OutputRouteVerified { get; set; }

    public bool DeviceAvailable { get; set; } = true;

    public PolicyDecision Evaluate(SemanticEvent e, double nowMs)
    {
        PolicyDecision Suppress(string reason) => new(PolicyOutcome.Suppressed, reason, null, e, nowMs);

        if (Config.Mode == HapticsMode.Off)
        {
            return Suppress("mode-off");
        }

        if (!ConsentGiven)
        {
            return Suppress("no-consent");
        }

        var rule = RuleFor(e.Kind);
        if (rule is null)
        {
            return Suppress("no-haptic-for-event");
        }

        if (!RuleEnabled(rule.Value.Rule))
        {
            return Suppress($"rule-disabled:{rule.Value.Rule}");
        }

        var name = _processName(e.ProcessId);
        if (name is not null && Config.ExcludedProcesses.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)))
        {
            return Suppress("process-excluded");
        }

        if (Config.AppPolicy == AppPolicy.OptInAllowlist &&
            (name is null || !Config.AllowedProcesses.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))))
        {
            return Suppress("app-not-allowlisted");
        }

        if (e.Element is { IsPassword: true })
        {
            return Suppress("protected-context");
        }

        if (e.Element is { IsEnabled: false } && !Config.IncludeDisabledControls)
        {
            return Suppress("disabled-control");
        }

        if (e.Element is { IsOffscreen: true })
        {
            return Suppress("offscreen");
        }

        if (!DeviceAvailable)
        {
            return Suppress("device-unavailable");
        }

        if (e.Confidence < Config.MinimumConfidence)
        {
            return Suppress("low-confidence");
        }

        if (nowMs - e.PointerTimestampMs > Config.SemanticIntentExpiryMs)
        {
            return Suppress("stale-intent");
        }

        foreach (var ns in Config.NativeSuppressions)
        {
            var procMatch = ns.ProcessName is null || string.Equals(ns.ProcessName, name, StringComparison.OrdinalIgnoreCase);
            var typeMatch = ns.ControlType is null || string.Equals(ns.ControlType, e.ControlType, StringComparison.Ordinal) ||
                            string.Equals(ns.ControlType, e.Element?.ParentControlType, StringComparison.Ordinal);
            var buttonMatch = !ns.OnlyWhenPrimaryDown || e.PrimaryDown;
            if (procMatch && typeMatch && buttonMatch && (ns.ProcessName is not null || ns.ControlType is not null))
            {
                return Suppress($"native-suppression:{ns.Id}");
            }
        }

        if (rule.Value.Kind == IntentKind.Hover && e.PointerSpeedPxPerSec > Config.HoverMaxPointerSpeedPxPerSec)
        {
            return Suppress("fast-flick");
        }

        if (e.TransitionId == _lastTransitionEmitted && e.Kind == SemanticEventKind.EnterControl)
        {
            return Suppress("coalesced-with-group-boundary");
        }

        if (e.ElementKey is not null && rule.Value.Kind == IntentKind.Hover &&
            _lastByElement.TryGetValue(e.ElementKey, out var lastEl) && nowMs - lastEl < Config.SameElementReentryCooldownMs)
        {
            return Suppress("reentry-cooldown");
        }

        if (nowMs - _lastEmitMs < Config.MinimumGlobalSpacingMs)
        {
            return Suppress("rate-limit:spacing");
        }

        while (_recent.Count > 0 && nowMs - _recent.Peek() > 60_000)
        {
            _recent.Dequeue();
        }

        if (_recent.Count(t => nowMs - t < 1000) >= Config.GlobalMaxPulsesPerSecond)
        {
            return Suppress("rate-limit:per-second");
        }

        if (_recent.Count >= Config.MaxPulsesPerMinute)
        {
            return Suppress("rate-limit:per-minute");
        }

        var intent = new HapticIntent(
            $"i{++_intentSeq}",
            rule.Value.Kind,
            rule.Value.Kind.ToString(),
            Math.Clamp(Config.IntensityScale * rule.Value.IntensityMultiplier, 0, 1),
            e.PointerTimestampMs + Config.SemanticIntentExpiryMs,
            e.Confidence,
            rule.Value.Rule,
            e.ProcessId,
            e.PointerTimestampMs,
            e.ElementKey);

        _lastEmitMs = nowMs;
        _recent.Enqueue(nowMs);
        _lastTransitionEmitted = e.TransitionId;
        if (e.ElementKey is not null)
        {
            _lastByElement[e.ElementKey] = nowMs;
        }

        if (Config.Mode == HapticsMode.ObserveOnly)
        {
            return new PolicyDecision(PolicyOutcome.DryRun, "observe-only", intent, e, nowMs);
        }

        if (!OutputRouteVerified)
        {
            return new PolicyDecision(PolicyOutcome.DryRun, "output-route-unverified", intent, e, nowMs);
        }

        return new PolicyDecision(PolicyOutcome.Emit, "accepted", intent, e, nowMs);
    }

    private bool RuleEnabled(string rule) => rule switch
    {
        "hover-enter-control" => Config.HoverEnabled,
        "group-boundary" => Config.GroupBoundaryEnabled,
        "range-step" => Config.RangeStepsEnabled,
        "range-limit" => Config.RangeLimitsEnabled,
        _ => false,
    };

    private static (string Rule, IntentKind Kind, double IntensityMultiplier)? RuleFor(SemanticEventKind kind) => kind switch
    {
        SemanticEventKind.EnterControl => ("hover-enter-control", IntentKind.Hover, 1.0),
        SemanticEventKind.CrossGroupBoundary => ("group-boundary", IntentKind.Step, 1.2),
        SemanticEventKind.RangeStep => ("range-step", IntentKind.Step, 1.2),
        SemanticEventKind.RangeLimit => ("range-limit", IntentKind.Collide, 1.6),
        _ => null,
    };
}
