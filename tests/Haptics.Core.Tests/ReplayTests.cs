using Haptics.Core;

namespace Haptics.Core.Tests;

/// <summary>Golden-trace replay (SPEC §12.3): exact logical decisions, no OS timing.</summary>
public class ReplayTests
{
    [Fact]
    public void GenerationChangesRejectOldAnswersAndCancelPendingArrivals()
    {
        var stale = Run("generation-stale-context.jsonl");
        Assert.Equal(["Hover"], stale.Haptic.Select(d => d.Waveform));
        Assert.Equal(2, stale.Notes.Count(n => n.EndsWith("generation-snapshot-discarded", StringComparison.Ordinal)));
        var switched = Run("device-change-pending.jsonl");
        Assert.Empty(switched.Haptic);
        Assert.Contains(switched.Notes, n => n.EndsWith("state-cleared:device-changed", StringComparison.Ordinal));
    }

    private static TraceReplay.ReplayOutput Run(string name, PolicyConfig? cfg = null) =>
        TraceReplay.Run(Path.Combine(AppContext.BaseDirectory, "traces", name), cfg);

    [Fact]
    public void SlowSweepEntersToolbarWithStepThenLandsOnEachButton()
    {
        var o = Run("toolbar-slow-sweep.jsonl");
        Assert.Equal(["Step", "Hover", "Hover", "Hover", "Hover"], o.Haptic.Select(d => d.Waveform));
    }

    [Fact]
    public void SlowPixelCrawlStillProducesHovers()
    {
        var o = Run("toolbar-pixel-crawl.jsonl");
        Assert.Equal(["Step", "Hover"], o.Haptic.Select(d => d.Waveform));
        Assert.DoesNotContain(o.Notes, n => n.EndsWith("stationary-ui-change-silent", StringComparison.Ordinal));
    }

    [Fact]
    public void ContentRowsAreSilentActionsTick()
    {
        string El(int t, int x, string type, string rid, int bx) =>
            $$"""{"t":{{t}},"type":"element","req":{{t - 5}},"x":{{x}},"y":15,"pid":100,"controlType":"{{type}}","rid":"{{rid}}","bounds":[{{bx}},0,40,30]}""";
        string Ptr(int t, int x) => $$"""{"t":{{t}},"type":"pointer","x":{{x}},"y":15,"fg":100}""";
        var lines = new[]
        {
            """{"type":"process","pid":100,"name":"explorer"}""",
            Ptr(0, 20), El(6, 20, "ListItem", "file1", 0),
            Ptr(300, 60), El(306, 60, "TreeItem", "folder", 40),
            Ptr(600, 100), El(606, 100, "DataItem", "row", 80),
            Ptr(900, 140), El(906, 140, "TabItem", "tab", 120),
            Ptr(1200, 180), El(1206, 180, "SplitButton", "ribbon", 160),
        };
        var o = TraceReplay.Run(lines);
        Assert.Equal(2, o.Emitted);
        Assert.All(o.Haptic, d => Assert.Contains(d.ControlType, new[] { "TabItem", "SplitButton" }));
    }

    [Fact]
    public void FastFlickCollapsesToAtMostOne()
    {
        var o = Run("toolbar-fast-flick.jsonl");
        Assert.InRange(o.Emitted, 0, 1);
        Assert.True(o.Notes.Count(n => n.EndsWith("passed-through", StringComparison.Ordinal)) >= 15);
    }

    [Fact]
    public void JitterAroundAnEdgeDoesNotBuzzSaw()
    {
        var o = Run("boundary-jitter.jsonl");
        Assert.Equal(1, o.Emitted);
        Assert.Contains(o.Notes, n => n.EndsWith("hysteresis-hold", StringComparison.Ordinal));
    }

    [Fact]
    public void UiChangingUnderStationaryPointerIsSilent()
    {
        var o = Run("same-element-stationary.jsonl");
        Assert.Equal(1, o.Emitted);
        Assert.Contains(o.Notes, n => n.EndsWith("stationary-ui-change-silent", StringComparison.Ordinal));
    }

    [Fact]
    public void MenuGroupBoundaryEmitsStepAndCoalescesHover()
    {
        var o = Run("menu-nested.jsonl");
        Assert.Equal(["Step", "Step", "Hover"], o.Haptic.Select(d => d.Waveform));
        Assert.Contains(o.Decisions, d => d.Reason == "coalesced-with-group-boundary");
    }

    [Fact]
    public void QuietProfileDropsHoverButKeepsBoundary()
    {
        var cfg = new PolicyConfig { Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions };
        cfg.ApplyProfile(EffectProfile.Quiet);
        var o = Run("menu-nested.jsonl", cfg);
        Assert.Equal(["Step", "Step"], o.Haptic.Select(d => d.Waveform));
    }

    [Fact]
    public void SliderDetentsStepAndCollideOnlyDuringPhysicalDrag()
    {
        var o = Run("slider-discrete.jsonl");
        var waves = o.Haptic.Select(d => d.Waveform).ToList();
        Assert.Equal("Hover", waves[0]);
        Assert.Equal(8, waves.Count(w => w == "Step"));
        Assert.Equal(1, waves.Count(w => w == "Collide"));
        Assert.Equal("Collide", waves[^1]);
    }

    [Fact]
    public void WheelAtLimitIsSilent()
    {
        var o = Run("scroll-at-limit.jsonl");
        Assert.Equal(0, o.Emitted);
        Assert.Contains(o.Notes, n => n.Contains("wheel-ignored", StringComparison.Ordinal));
    }

    [Fact]
    public void LateUiaNeverProducesLatePulses()
    {
        var o = Run("uia-stale-late.jsonl");
        Assert.Equal(0, o.Emitted);
        Assert.Contains(o.Notes, n => n.EndsWith("stale-snapshot-discarded", StringComparison.Ordinal));
        Assert.Contains(o.Decisions, d => d.Reason == "stale-intent");
    }

    [Fact]
    public void DeviceDisconnectFailsClosed()
    {
        var o = Run("device-disconnect.jsonl");
        Assert.Equal(1, o.Emitted);
        Assert.Contains(o.Decisions, d => d.Reason == "device-unavailable");
    }

    [Fact]
    public void NativeHapticZonesAreSuppressed()
    {
        var o = Run("native-duplicate-suppression.jsonl");
        Assert.Equal(0, o.Emitted);
        Assert.Contains(o.Decisions, d => d.Reason == "native-suppression:powerpoint-alignment");
    }

    [Fact]
    public void PasswordContextsAreSilent()
    {
        var o = Run("security-password.jsonl");
        Assert.Equal(0, o.Emitted);
        Assert.Contains(o.Decisions, d => d.Reason == "protected-context");
    }

    [Fact]
    public void ExcludedAppsAreSilent()
    {
        var o = Run("excluded-app.jsonl");
        Assert.Equal(0, o.Emitted);
        Assert.Contains(o.Decisions, d => d.Reason == "process-excluded");
    }

    [Fact]
    public void ObserveOnlyNeverEmits()
    {
        var cfg = new PolicyConfig { Mode = HapticsMode.ObserveOnly, AppPolicy = AppPolicy.AllExceptExclusions };
        var o = Run("toolbar-slow-sweep.jsonl", cfg);
        Assert.Equal(0, o.Emitted);
        Assert.Equal(5, o.Decisions.Count(d => d.Outcome == PolicyOutcome.DryRun));
    }

    [Fact]
    public void AllowlistDefaultIsSilentForUnlistedApps()
    {
        var cfg = new PolicyConfig { Mode = HapticsMode.On };
        var o = Run("toolbar-slow-sweep.jsonl", cfg);
        Assert.Equal(0, o.Emitted);
        Assert.Contains(o.Decisions, d => d.Reason == "app-not-allowlisted");

        cfg.AllowedProcesses.Add("winword");
        Assert.Equal(5, Run("toolbar-slow-sweep.jsonl", cfg).Emitted);
    }
}
