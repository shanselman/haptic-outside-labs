using Haptics.Core;

namespace Haptics.Core.Tests;

/// <summary>Golden-trace replay (SPEC §12.3): exact logical decisions, no OS timing.</summary>
public class ReplayTests
{
    [Theory]
    [InlineData(EffectProfile.Quiet)]
    [InlineData(EffectProfile.Focused)]
    [InlineData(EffectProfile.Everything)]
    public void GroupLinkFilterAddsOnlySmallContainedLinksToFocused(EffectProfile style)
    {
        var output = Run("focused-group-link-filter.jsonl", PolicyConfig.Sample(style, 0.8));
        Assert.Equal(style switch
        {
            EffectProfile.Quiet => new double[] { 930 },
            EffectProfile.Focused => [30, 930],
            _ => [30, 350, 650, 930],
        }, output.Haptic.Select(d => d.T));
        Assert.All(output.Haptic, d => Assert.Equal("Hover", d.Waveform));
    }

    [Theory]
    [InlineData(EffectProfile.Quiet)]
    [InlineData(EffectProfile.Focused)]
    [InlineData(EffectProfile.Everything)]
    public void SizeAwareInlineDwellHasExactBoundariesInEveryStyle(EffectProfile style)
    {
        var output = Run("size-aware-inline-dwell.jsonl", PolicyConfig.Sample(style, 0.8));
        Assert.Equal(style switch
        {
            EffectProfile.Quiet => new double[] { 30, 350, 650, 930, 1250 },
            EffectProfile.Focused => [30, 350, 650, 930, 1250, 2130],
            _ => [30, 350, 650, 930, 1250, 1530, 2130],
        }, output.Haptic.Select(d => d.T));
        Assert.All(output.Haptic, d => Assert.Equal("Hover", d.Waveform));
    }

    [Theory]
    [InlineData(EffectProfile.Quiet)]
    [InlineData(EffectProfile.Focused)]
    [InlineData(EffectProfile.Everything)]
    public void EverythingRestoresToggleRailHoverWithoutCheckboxHoverOrDuplicateClickPulses(EffectProfile style)
    {
        var output = Run("everything-toggle-links-options.jsonl", PolicyConfig.Sample(style, 0.8));
        Assert.Equal(style switch
        {
            EffectProfile.Everything => new double[] { 52, 330, 902, 1652, 1952 },
            EffectProfile.Focused => [330, 1652],
            _ => [1652],
        }, output.Haptic.Select(d => d.T));
        Assert.All(output.Haptic, d => Assert.Equal(d.EventKind == "ToggleChanged" ? "Step" : "Hover", d.Waveform));
        Assert.DoesNotContain(output.Haptic, d => d.ControlType == "CheckBox");
        Assert.DoesNotContain(output.Haptic, d => d.T >= 1000 && d.T < 1600);
    }

    [Theory]
    [InlineData(EffectProfile.Quiet)]
    [InlineData(EffectProfile.Focused)]
    [InlineData(EffectProfile.Everything)]
    public void SliderThumbOnlyHoverKeepsDragFeedbackWithoutTrackOrReleasePulses(EffectProfile style)
    {
        var output = Run("slider-thumb-hover.jsonl", PolicyConfig.Sample(style, 0.8));
        if (style == EffectProfile.Quiet)
        {
            Assert.Equal([1002, 1302], output.Haptic.Select(d => d.T));
            Assert.Equal(["Step", "Collide"], output.Haptic.Select(d => d.Waveform));
        }
        else
        {
            Assert.Equal([132, 732, 1002, 1302], output.Haptic.Select(d => d.T));
            Assert.Equal(["Hover", "Hover", "Step", "Collide"], output.Haptic.Select(d => d.Waveform));
        }
    }

    [Theory]
    [InlineData(EffectProfile.Focused)]
    [InlineData(EffectProfile.Everything)]
    public void InlineLinksAreSharedButOptionsRemainEverythingOnly(EffectProfile style)
    {
        var output = Run("style-inclusion-checkbox-repeat.jsonl", PolicyConfig.Sample(style, 0.8));
        Assert.Equal(style == EffectProfile.Everything ? [35, 335, 675, 975, 1275, 1575] : new double[] { 35, 675, 975, 1275, 1575 },
            output.Haptic.Select(d => d.T));
        Assert.Equal(style == EffectProfile.Everything ? ["Hover", "Hover", "Step", "Step", "Step", "Step"] :
            new[] { "Hover", "Step", "Step", "Step", "Step" }, output.Haptic.Select(d => d.Waveform));
        Assert.Equal("Hover", Assert.Single(Run("style-inclusion-checkbox-repeat.jsonl",
            PolicyConfig.Sample(EffectProfile.Quiet, 0.8)).Haptic).Waveform);
    }

    [Theory]
    [InlineData(EffectProfile.Focused, "Hover")]
    [InlineData(EffectProfile.Quiet, "Hover")]
    [InlineData(EffectProfile.Everything, "Hover")]
    public void TabsAfterFlybysLandDespiteDelayedRepliesAndSharedEdges(EffectProfile style, string waveform)
    {
        var output = Run("tab-delayed-landing.jsonl", PolicyConfig.Sample(style, 0.8));
        Assert.Equal([70, 370, 690], output.Haptic.Select(d => d.T));
        Assert.All(output.Haptic, d => Assert.Equal(waveform, d.Waveform));
        Assert.DoesNotContain(output.Notes, n => n.EndsWith("stationary-ui-change-silent", StringComparison.Ordinal));
        Assert.Contains(output.Notes, n => n.EndsWith("superseded-snapshot-discarded", StringComparison.Ordinal));
    }

    [Fact]
    public void QuietFeedbackIsASubsetOfFocusedIncludingSliderDrag()
    {
        var focused = Run("quiet-subset-focused.jsonl", PolicyConfig.Sample(EffectProfile.Focused, 0.8));
        var quiet = Run("quiet-subset-focused.jsonl", PolicyConfig.Sample(EffectProfile.Quiet, 0.8));
        Assert.Equal(["Hover", "Hover", "Hover", "Step", "Collide", "Step"], focused.Haptic.Select(d => d.Waveform));
        Assert.Equal(["Hover", "Step", "Collide", "Step"], quiet.Haptic.Select(d => d.Waveform));
        var scenarios = focused.Haptic.Select(d => (d.T, d.EventKind, d.ControlType)).ToHashSet();
        Assert.All(quiet.Haptic, d => Assert.Contains((d.T, d.EventKind, d.ControlType), scenarios));
        Assert.Contains(focused.Notes, n => n.EndsWith("skip:slider-drag-hover", StringComparison.Ordinal));
    }

    [Fact]
    public void MenuAndTabHeaderPartsShareDwellAndNeverCreateASecondLanding()
    {
        var output = Run("focused-header-continuity.jsonl");
        Assert.Equal([32, 650], output.Haptic.Select(d => d.T));
        Assert.All(output.Haptic, d => Assert.Equal("Hover", d.Waveform));
        Assert.DoesNotContain(output.Decisions, d => d.Reason == "reentry-cooldown");
        Assert.DoesNotContain(output.Notes, n => n.EndsWith("passed-through", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("chrome")]
    [InlineData("msedge")]
    [InlineData("firefox")]
    [InlineData("brave")]
    [InlineData("opera")]
    [InlineData("ms-teams")]
    [InlineData("outlook")]
    [InlineData("explorer")]
    [InlineData("devenv")]
    [InlineData("unknown-app")]
    public void EveryAppUsesTheSameDwellWithoutDensityThinning(string browser)
    {
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "traces", "focused-browser-sweep.jsonl"))
            .Select(line => line.Replace("\"chrome\"", $"\"{browser}\"", StringComparison.Ordinal));
        var output = TraceReplay.Run(lines);
        Assert.Equal(Enumerable.Range(0, 12).Select(i => 52d + i * 300).Append(4050), output.Haptic.Select(d => d.T));
        Assert.All(output.Haptic, d => Assert.Equal("Hover", d.Waveform));
        Assert.DoesNotContain(output.Decisions, d => d.Reason == "density-backoff");
    }

    [Theory]
    [InlineData("dwell-lookup-40ms.jsonl", 50)]
    [InlineData("dwell-lookup-150ms.jsonl", 160)]
    public void ProviderLatencyPreservesExistingDwellTiming(string trace, double landedAt)
    {
        var output = Run(trace);
        Assert.Equal(["Hover"], output.Haptic.Select(d => d.Waveform));
        Assert.Equal(landedAt, Assert.Single(output.Haptic).T);
    }

    [Fact]
    public void DestinationAndCooperativeContextStayProtected()
    {
        var edges = Run("strip-edge-destination-safety.jsonl",
            new PolicyConfig { Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions, Profile = EffectProfile.Everything });
        Assert.Equal(6, edges.Emitted);
        Assert.All(edges.Haptic, d => Assert.Equal("Hover", d.Waveform));
        Assert.DoesNotContain(edges.Decisions, d => d.EventKind == "GroupEdge");
        var cooperative = Run("cooperative-context-safety.jsonl");
        Assert.Equal(["Align"], cooperative.Haptic.Select(d => d.Waveform));
        Assert.Equal(1600, Assert.Single(cooperative.Haptic).T);
        Assert.Contains(cooperative.Decisions, d => d.Reason == "unknown-context");
    }

    [Fact]
    public void PendingArrivalsUseFreshSafetyMetadataAndExpireAfterSchedulerStalls()
    {
        var refreshed = Run("pending-metadata-safety.jsonl");
        Assert.Equal(["Hover"], refreshed.Haptic.Select(d => d.Waveform));
        Assert.All(refreshed.Haptic, d => Assert.True(d.T >= 1500));
        var late = Run("pending-late-tick.jsonl");
        Assert.Empty(late.Haptic);
        Assert.Contains(late.Notes, n => n.EndsWith("arrival-expired", StringComparison.Ordinal));
    }

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

    private static TraceReplay.ReplayOutput RunEverything(string name) => Run(name, new PolicyConfig
    {
        Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions,
        Profile = EffectProfile.Everything, GroupBoundaryEnabled = false,
    });

    [Theory]
    [InlineData(EffectProfile.Focused, 2)]
    [InlineData(EffectProfile.Everything, 2)]
    [InlineData(EffectProfile.Quiet, 0)]
    public void UniversalDwellAndSameTargetReentryReplay(EffectProfile style, int count)
    {
        var output = Run("universal-dwell-reentry.jsonl", PolicyConfig.Sample(style, 0.8));
        Assert.Equal(count, output.Emitted);
        if (count > 0)
        {
            Assert.Equal([32, 361], output.Haptic.Select(d => d.T));
            Assert.All(output.Haptic, d => Assert.Equal("Hover", d.Waveform));
        }
        Assert.Contains(output.Notes, n => n.EndsWith("passed-through", StringComparison.Ordinal));
    }

    [Fact]
    public void NativeCaptionWithoutBoundsCannotLandAfterLeavingTheConfirmedPoint()
    {
        var o = Run("focused-native-caption.jsonl");
        Assert.Empty(o.Haptic);
        Assert.Empty(Run("focused-native-caption.jsonl", new PolicyConfig
        {
            Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions, CloseButtonHoverEnabled = false,
        }).Haptic);
    }

    [Fact]
    public void BrowserCommandsAreSilentInFocusedButStillAvailableInEverything()
    {
        var o = Run("focused-browser-links.jsonl");
        Assert.Equal([952, 1252, 1552], o.Haptic.Select(d => d.T));
        Assert.DoesNotContain(o.Decisions, d => d.EventKind == "ToggleChanged");
        var everything = RunEverything("focused-browser-links.jsonl");
        Assert.Equal([52, 352, 652], everything.Haptic.Take(3).Select(d => d.T));
        Assert.Contains(everything.Decisions, d => d.Reason == "process-excluded");
    }

    [Fact]
    public void ToolbarSlowSweepIsReservedForEverything()
    {
        Assert.Empty(Run("toolbar-slow-sweep.jsonl").Haptic);
        var o = RunEverything("toolbar-slow-sweep.jsonl");
        Assert.Equal(["Hover", "Hover", "Hover", "Hover", "Hover"], o.Haptic.Select(d => d.Waveform));
    }

    [Fact]
    public void SlowPixelCrawlStillProducesHovers()
    {
        var o = RunEverything("toolbar-pixel-crawl.jsonl");
        Assert.Equal(["Hover", "Hover"], o.Haptic.Select(d => d.Waveform));
        Assert.DoesNotContain(o.Notes, n => n.EndsWith("stationary-ui-change-silent", StringComparison.Ordinal));
    }

    [Fact]
    public void ContentRowsAreSilentActionsTick()
    {
        string El(int t, int x, string type, string rid, int bx, string parent) =>
            $$"""{"t":{{t}},"type":"element","req":{{t - 5}},"x":{{x}},"y":15,"pid":100,"controlType":"{{type}}","rid":"{{rid}}","bounds":[{{bx}},0,40,30],"parentType":"{{parent}}"}""";
        string Ptr(int t, int x) => $$"""{"t":{{t}},"type":"pointer","x":{{x}},"y":15,"fg":100}""";
        var lines = new[]
        {
            """{"type":"process","pid":100,"name":"explorer"}""",
            Ptr(0, 20), El(6, 20, "ListItem", "file1", 0, "List"),
            Ptr(300, 60), El(306, 60, "TreeItem", "folder", 40, "Tree"),
            Ptr(600, 100), El(606, 100, "DataItem", "row", 80, "DataGrid"),
            Ptr(900, 140), El(906, 140, "TabItem", "tab", 120, "Tab"),
            Ptr(1200, 180), El(1206, 180, "SplitButton", "ribbon", 160, "ToolBar"),
        };
        var o = TraceReplay.Run(lines);
        Assert.Equal(2, o.Emitted);
        Assert.All(o.Haptic, d => Assert.Contains(d.ControlType, new[] { "TabItem", "SplitButton" }));
    }

    [Fact]
    public void FastFlickCollapsesToAtMostOne()
    {
        var o = RunEverything("toolbar-fast-flick.jsonl");
        Assert.InRange(o.Emitted, 0, 1);
        Assert.True(o.Notes.Count(n => n.EndsWith("passed-through", StringComparison.Ordinal)) >= 15);
    }

    [Fact]
    public void JitterAroundAnEdgeDoesNotBuzzSaw()
    {
        var o = RunEverything("boundary-jitter.jsonl");
        Assert.Equal(1, o.Emitted);
        Assert.Contains(o.Notes, n => n.EndsWith("hysteresis-hold", StringComparison.Ordinal));
    }

    [Fact]
    public void UiChangingUnderStationaryPointerIsSilent()
    {
        var o = RunEverything("same-element-stationary.jsonl");
        Assert.Equal(1, o.Emitted);
        Assert.Contains(o.Notes, n => n.EndsWith("stationary-ui-change-silent", StringComparison.Ordinal));
    }

    [Fact]
    public void FocusedCloseAndOtherCaptionButtonsStaySilent()
    {
        var o = Run("caption-close-hover.jsonl");
        Assert.Empty(o.Haptic);
        Assert.DoesNotContain(o.Decisions, d => d.Reason == "coalesced-with-group-boundary");
        var off = Run("caption-close-hover.jsonl", new PolicyConfig { Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions, CloseButtonHoverEnabled = false });
        Assert.Empty(off.Haptic);
    }

    [Fact]
    public void FocusedMenusLandOnlyOnTheLauncher()
    {
        var o = Run("menu-nested.jsonl");
        Assert.Equal(["Hover"], o.Haptic.Select(d => d.Waveform));
        Assert.Equal(136, Assert.Single(o.Haptic).T);
        Assert.Contains(o.Decisions, d => d.Reason == "rule-disabled:group-entry");
    }

    [Fact]
    public void QuietProfileDropsHoverAndDoesNotRestoreMenuGroupPulses()
    {
        var cfg = new PolicyConfig { Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions };
        cfg.ApplyProfile(EffectProfile.Quiet);
        var o = Run("menu-nested.jsonl", cfg);
        Assert.Empty(o.Haptic);
    }

    [Fact]
    public void SliderDetentsStepAndCollideOnlyDuringPhysicalDrag()
    {
        var o = Run("slider-discrete.jsonl");
        var waves = o.Haptic.Select(d => d.Waveform).ToList();
        Assert.Equal("Hover", waves[0]);
        Assert.Equal(1, waves.Count(w => w == "Hover"));
        Assert.Equal(8, waves.Count(w => w == "Step"));
        Assert.Equal(1, waves.Count(w => w == "Collide"));
        Assert.Equal("Collide", waves[^1]);
    }

    [Fact]
    public void FocusedToggleChangesNeedAConfirmedMouseClickInEitherDirection()
    {
        var o = Run("focused-toggle-changes.jsonl");
        Assert.Equal([135, 325, 1135, 1425], o.Haptic.Select(d => d.T));
        Assert.Equal(["CheckBox", "CheckBox", "Button", "Button"], o.Haptic.Select(d => d.ControlType));
        Assert.All(o.Haptic, pulse =>
        {
            Assert.Equal("ToggleChanged", pulse.EventKind);
            Assert.Equal("Step", pulse.Waveform);
        });
    }

    [Fact]
    public void FocusedNavigationLandsWithHoverRegardlessOfSize()
    {
        var o = Run("focused-navigation-hover.jsonl");
        Assert.Equal([652, 952], o.Haptic.Select(d => d.T));
        Assert.All(o.Haptic, pulse => Assert.Equal("Hover", pulse.Waveform));
        Assert.Contains(o.Notes, n => n.EndsWith("skip:already-selected", StringComparison.Ordinal));
        Assert.DoesNotContain(o.Haptic, d => d.T >= 2100);
    }

    [Fact]
    public void FocusedContentSweepOnlyEnablesExplicitNavigationRoles()
    {
        var o = Run("focused-content-filter.jsonl");
        Assert.Equal([2752, 3652], o.Haptic.Select(d => d.T));
        Assert.All(o.Haptic, d => Assert.Equal(("Hover", "EnterControl"), (d.Waveform, d.EventKind)));
        Assert.Contains(o.Notes, n => n == "302:skip:focused-target-not-enabled");
        Assert.Contains(o.Notes, n => n == "602:skip:focused-target-not-enabled");
        Assert.Contains(o.Notes, n => n == "902:skip:focused-target-not-enabled");
        Assert.Contains(o.Notes, n => n == "3002:skip:focused-target-not-enabled");
    }

    [Fact]
    public void WheelAtLimitIsSilent()
    {
        var o = Run("scroll-at-limit.jsonl");
        Assert.Equal(0, o.Emitted);
        Assert.Contains(o.Notes, n => n.Contains("wheel-ignored", StringComparison.Ordinal));
    }

    [Fact]
    public void StaleUiaIsRejectedButValidProviderLatencyCanCompleteDwell()
    {
        var o = Run("uia-stale-late.jsonl");
        Assert.Equal(570, Assert.Single(o.Haptic).T);
        Assert.Contains(o.Notes, n => n.EndsWith("stale-snapshot-discarded", StringComparison.Ordinal));
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
        var cfg = new PolicyConfig { Mode = HapticsMode.ObserveOnly, AppPolicy = AppPolicy.AllExceptExclusions,
            Profile = EffectProfile.Everything, GroupBoundaryEnabled = false };
        var o = Run("toolbar-slow-sweep.jsonl", cfg);
        Assert.Equal(0, o.Emitted);
        Assert.Equal(5, o.Decisions.Count(d => d.Outcome == PolicyOutcome.DryRun));
    }

    [Fact]
    public void AllowlistDefaultIsSilentForUnlistedApps()
    {
        var cfg = new PolicyConfig { Mode = HapticsMode.On, Profile = EffectProfile.Everything, GroupBoundaryEnabled = false };
        var o = Run("toolbar-slow-sweep.jsonl", cfg);
        Assert.Equal(0, o.Emitted);
        Assert.Contains(o.Decisions, d => d.Reason == "app-not-allowlisted");

        cfg.AllowedProcesses.Add("winword");
        Assert.Equal(5, Run("toolbar-slow-sweep.jsonl", cfg).Emitted);
    }
}
