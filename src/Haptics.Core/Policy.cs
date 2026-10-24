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
    /// <summary>Structure only: group entries, tab/radio detents, strip edges, slider detents. No per-button ticks.</summary>
    Quiet,

    /// <summary>The default: Quiet + a tick when you *land* on something you can do (not while flying past).</summary>
    Focused,

    /// <summary>Every crossing ticks (no landing filter), looser limits. Dense; useful for demos and tuning.</summary>
    Everything,
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

/// <summary>Per-app tuning. Starting points, meant to be edited.</summary>
public sealed class AppProfile
{
    public double IntensityMultiplier { get; set; } = 1.0;

    /// <summary>Scales only "land" ticks, so structure (groups, detents) stands out in dense apps.</summary>
    public double LandIntensityMultiplier { get; set; } = 1.0;

    public int? DensityLandsPer2s { get; set; }

    public static Dictionary<string, AppProfile> Defaults()
    {
        var d = new Dictionary<string, AppProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var browser in new[] { "msedge", "chrome", "firefox", "brave", "opera" })
        {
            d[browser] = new AppProfile { DensityLandsPer2s = 6 }; // Web pages are dense with controls.
        }

        foreach (var chat in new[] { "ms-teams", "Teams", "slack", "Discord" })
        {
            d[chat] = new AppProfile { DensityLandsPer2s = 6 };
        }

        foreach (var dev in new[] { "Code", "devenv", "rider64" })
        {
            d[dev] = new AppProfile { DensityLandsPer2s = 6 };
        }

        foreach (var office in new[] { "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "olk", "ONENOTE" })
        {
            d[office] = new AppProfile { LandIntensityMultiplier = 0.8 }; // Ribbon group entries stand out.
        }

        return d;
    }
}

/// <summary>Editable defaults (SPEC §8.3). Starting values to tune by feel, not proven UX values.</summary>
public sealed class PolicyConfig
{
    public HapticsMode Mode { get; set; } = HapticsMode.ObserveOnly;
    public EffectProfile Profile { get; set; } = EffectProfile.Focused;
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

    public Dictionary<string, AppProfile> AppProfiles { get; set; } = AppProfile.Defaults();

    /// <summary>Copied into the interpreter: arrivals fire only when the pointer slows/stops. 0 = every crossing.</summary>
    public double LandingSpeedPxPerSec { get; set; } = 1200;

    public bool HoverEnabled => Profile != EffectProfile.Quiet;
    public bool DetentsEnabled { get; set; } = true;
    public bool GroupBoundaryEnabled { get; set; } = true;
    public bool EdgeBumpsEnabled { get; set; } = true;
    public bool RangeStepsEnabled { get; set; } = true;
    public bool RangeLimitsEnabled { get; set; } = true;
    public bool CooperativeEnabled { get; set; } = true;

    /// <summary>Per app: more than this many "land" ticks in 2 s → land ticks pause (structure still ticks). 0 = off.</summary>
    public int DensityLandsPer2s { get; set; } = 10;

    public double DensityBackoffMs { get; set; } = 4000;

    /// <summary>A target this tall (logical px), or at least this big on both sides, lands with a firmer Click.</summary>
    public double LargeTargetLogicalHeight { get; set; } = 56;

    public double LargeTargetLogicalSide { get; set; } = 48;

    public double LandIntensity { get; set; } = 0.85;
    public double LargeIntensity { get; set; } = 1.0;
    public double DetentIntensity { get; set; } = 1.1;
    public double GroupIntensity { get; set; } = 1.2;
    public double EdgeIntensity { get; set; } = 0.9;

    public PolicyConfig Clone() => (PolicyConfig)MemberwiseClone();

    /// <summary>
    /// The policy used by the tray and the Showcase: every app, the chosen style, loose global limits, and a 150 ms
    /// freshness window (real apps' UIA is slower than the lab fixture). Password fields and exclusions stay silent.
    /// </summary>
    public static PolicyConfig Sample(EffectProfile style, double intensity)
    {
        var p = new PolicyConfig { Mode = HapticsMode.On, IntensityScale = intensity, AppPolicy = AppPolicy.AllExceptExclusions };
        p.ApplyProfile(style);
        p.GlobalMaxPulsesPerSecond = 12;
        p.MaxPulsesPerMinute = 600;
        p.MinimumGlobalSpacingMs = 40;
        p.SameElementReentryCooldownMs = 200;
        p.SemanticIntentExpiryMs = 150;
        p.HoverMaxPointerSpeedPxPerSec = 1_000_000;
        return p;
    }

    public void ApplyProfile(EffectProfile profile)
    {
        Profile = profile;
        switch (profile)
        {
            case EffectProfile.Everything:
                GlobalMaxPulsesPerSecond = 12;
                MinimumGlobalSpacingMs = 40;
                SameElementReentryCooldownMs = 200;
                HoverMaxPointerSpeedPxPerSec = 1_000_000;
                LandingSpeedPxPerSec = 0;
                DensityLandsPer2s = 0;
                break;
            default:
                GlobalMaxPulsesPerSecond = 4;
                MinimumGlobalSpacingMs = 120;
                SameElementReentryCooldownMs = 500;
                HoverMaxPointerSpeedPxPerSec = 4000;
                LandingSpeedPxPerSec = 1200;
                DensityLandsPer2s = 10;
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
/// The single global arbitration point (SPEC §8.2). Every source (generic rules, cooperating apps) must pass
/// through here; nothing reaches an actuator otherwise. Not thread-safe: callers serialize.
/// </summary>
public sealed class HapticPolicy
{
    private readonly Func<int, string?> _processName;
    private readonly Dictionary<string, double> _lastByElement = new();
    private readonly Queue<double> _recent = new();
    private readonly Dictionary<int, Queue<double>> _landsByPid = new();
    private readonly Dictionary<int, double> _backoffUntil = new();
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

        if (RuleFor(e) is not { } rule)
        {
            return Suppress("no-haptic-for-event");
        }

        if (!RuleEnabled(rule.Name))
        {
            return Suppress($"rule-disabled:{rule.Name}");
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

        var app = name is not null && Config.AppProfiles.TryGetValue(name, out var ap) ? ap : null;
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

        if (e.Kind != SemanticEventKind.Cooperative)
        {
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
        }

        if (rule.IsLand && e.PointerSpeedPxPerSec > Config.HoverMaxPointerSpeedPxPerSec)
        {
            return Suppress("fast-flick");
        }

        if (e.TransitionId == _lastTransitionEmitted && e.Kind == SemanticEventKind.EnterControl)
        {
            return Suppress("coalesced-with-group-boundary");
        }

        if (e.ElementKey is not null && rule.IsLand &&
            _lastByElement.TryGetValue(e.ElementKey, out var lastEl) && nowMs - lastEl < Config.SameElementReentryCooldownMs)
        {
            return Suppress("reentry-cooldown");
        }

        var densityLimit = app?.DensityLandsPer2s ?? Config.DensityLandsPer2s;
        if (rule.IsLand && densityLimit > 0 && _backoffUntil.TryGetValue(e.ProcessId, out var until) && nowMs < until)
        {
            return Suppress("density-backoff");
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

        var multiplier = rule.Multiplier * (app?.IntensityMultiplier ?? 1.0) * (rule.IsLand ? app?.LandIntensityMultiplier ?? 1.0 : 1.0);
        var intent = new HapticIntent(
            $"i{++_intentSeq}",
            rule.Kind,
            rule.Kind.ToString(),
            Math.Clamp(Config.IntensityScale * multiplier, 0, 1),
            e.PointerTimestampMs + Config.SemanticIntentExpiryMs,
            e.Confidence,
            rule.Name,
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

        if (rule.IsLand && densityLimit > 0)
        {
            if (!_landsByPid.TryGetValue(e.ProcessId, out var lands))
            {
                _landsByPid[e.ProcessId] = lands = new Queue<double>();
            }

            lands.Enqueue(nowMs);
            while (lands.Count > 0 && nowMs - lands.Peek() > 2000)
            {
                lands.Dequeue();
            }

            if (lands.Count >= densityLimit)
            {
                _backoffUntil[e.ProcessId] = nowMs + Config.DensityBackoffMs;
                lands.Clear();
            }
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
        "land" or "land-large" => Config.HoverEnabled,
        "detent" => Config.DetentsEnabled,
        "group-entry" => Config.GroupBoundaryEnabled,
        "strip-edge" => Config.EdgeBumpsEnabled,
        "range-step" => Config.RangeStepsEnabled,
        "range-limit" => Config.RangeLimitsEnabled,
        "cooperative" => Config.CooperativeEnabled,
        _ => false,
    };

    /// <summary>The haptic vocabulary: Land (Hover/Click), Detent (Step), Edge (Collide). One meaning per sensation.</summary>
    private Rule? RuleFor(SemanticEvent e)
    {
        switch (e.Kind)
        {
            case SemanticEventKind.EnterControl:
                if (SemanticVocabulary.DetentTypes.Contains(e.ControlType))
                {
                    return new Rule("detent", IntentKind.Step, Config.DetentIntensity, false);
                }

                return IsLarge(e.Element)
                    ? new Rule("land-large", IntentKind.Click, Config.LargeIntensity, true)
                    : new Rule("land", IntentKind.Hover, Config.LandIntensity, true);
            case SemanticEventKind.CrossGroupBoundary:
                return new Rule("group-entry", IntentKind.Step, Config.GroupIntensity, false);
            case SemanticEventKind.GroupEdge:
                return new Rule("strip-edge", IntentKind.Collide, Config.EdgeIntensity, false);
            case SemanticEventKind.RangeStep:
                return new Rule("range-step", IntentKind.Step, 1.2, false);
            case SemanticEventKind.RangeLimit:
                return new Rule("range-limit", IntentKind.Collide, 1.6, false);
            case SemanticEventKind.Cooperative when Enum.TryParse<IntentKind>(e.RequestedWaveform, true, out var kind):
                return new Rule("cooperative", kind, 1.0, false);
            default:
                return null;
        }
    }

    private bool IsLarge(ElementSnapshot? s) =>
        s is { LogicalHeight: { } h, LogicalWidth: { } w } &&
        (h >= Config.LargeTargetLogicalHeight || (h >= Config.LargeTargetLogicalSide && w >= Config.LargeTargetLogicalSide));

    private readonly record struct Rule(string Name, IntentKind Kind, double Multiplier, bool IsLand);
}
