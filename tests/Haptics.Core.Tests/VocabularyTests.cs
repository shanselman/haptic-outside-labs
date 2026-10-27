using Haptics.Core;

namespace Haptics.Core.Tests;

/// <summary>The v0.2 vocabulary: land vs. pass, role waveforms, group-first, edges, choosiness, density, cooperation.</summary>
public class VocabularyTests
{
    private const string Proc = """{"type":"process","pid":100,"name":"notepad"}""";

    private static string Ptr(double t, int x, int y = 15, bool primary = false) =>
        $$"""{"t":{{t}},"type":"pointer","x":{{x}},"y":{{y}},"fg":100,"primary":{{(primary ? "true" : "false")}}}""";

    private static string El(double t, int x, string type, string rid, int bx, int w = 40, int h = 30, int y = 15, string? parent = null,
        string? parentRid = null, int[]? parentBounds = null, bool? selected = null, double scale = 1, int by = 0) =>
        "{" + $"\"t\":{t},\"type\":\"element\",\"req\":{t - 5},\"x\":{x},\"y\":{y},\"pid\":100,\"controlType\":\"{type}\",\"rid\":\"{rid}\"," +
        $"\"bounds\":[{bx},{by},{w},{h}],\"scale\":{scale}" +
        (parent is null ? "" : $",\"parentType\":\"{parent}\",\"parentRid\":\"{parentRid}\"") +
        (parentBounds is null ? "" : $",\"parentBounds\":[{string.Join(',', parentBounds)}]") +
        (selected is null ? "" : $",\"selected\":{selected.Value.ToString().ToLowerInvariant()}") + "}";

    private static TraceReplay.ReplayOutput Run(PolicyConfig? cfg, params string[] lines) => TraceReplay.Run([Proc, .. lines], cfg);

    private static TraceReplay.ReplayOutput Run(params string[] lines) => Run(null, lines);

    [Fact]
    public void FlyingPastIsSilentStoppingLands()
    {
        // 3000 px/s sweep over A, B; stop on C.
        var o = Run(
            Ptr(0, 0), Ptr(10, 20), El(12, 20, "Button", "A", 0), Ptr(20, 50), El(22, 50, "Button", "B", 40), Ptr(30, 90), El(32, 90, "Button", "C", 80));
        Assert.Single(o.Haptic);
        Assert.Contains(o.Notes, n => n.EndsWith("passed-through", StringComparison.Ordinal));
        Assert.Contains(o.Decisions, d => d.Outcome == PolicyOutcome.Emit && d.T >= 32 + 80 - 10);
    }

    [Fact]
    public void EverythingStyleTicksOnEveryCrossing()
    {
        var cfg = new PolicyConfig { Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions };
        cfg.ApplyProfile(EffectProfile.Everything);
        var o = Run(cfg,
            Ptr(0, 0), Ptr(100, 20), El(102, 20, "Button", "A", 0), Ptr(200, 60), El(202, 60, "Button", "B", 40), Ptr(300, 100), El(302, 100, "Button", "C", 80));
        Assert.Equal(3, o.Emitted);
    }

    [Fact]
    public void WaveformFollowsRole()
    {
        var o = Run(
            Ptr(0, 0), Ptr(300, 20), El(302, 20, "Button", "small", 0),
            Ptr(600, 60, 40), El(602, 60, "Button", "ribbonLarge", 40, w: 44, h: 80, y: 40),
            Ptr(900, 120), El(902, 120, "TabItem", "tab", 100, w: 80),
            Ptr(1200, 220), El(1202, 220, "RadioButton", "radio", 200, w: 80));
        Assert.Equal(["Hover", "Click", "Step", "Step"], o.Haptic.Select(d => d.Waveform));
    }

    [Fact]
    public void LargeIsMeasuredInLogicalPixelsSoDpiDoesNotChangeTheFeel()
    {
        // 64x60 physical at 200% scale is a normal 32x30 button, not a large one.
        var o = Run(Ptr(0, 0), Ptr(300, 20), El(302, 20, "Button", "b", 0, w: 64, h: 60, scale: 2));
        Assert.Equal(["Hover"], o.Haptic.Select(d => d.Waveform));
    }

    [Fact]
    public void ChoosinessSkipsTinyTargetsInlineLinksAndTheTabYouAreOn()
    {
        var o = Run(
            Ptr(0, 0), Ptr(300, 20), El(302, 20, "Button", "tiny", 15, w: 8, h: 8),
            Ptr(600, 60), El(602, 60, "Hyperlink", "inline", 40, parent: "Text", parentRid: "para"),
            Ptr(900, 100), El(902, 100, "TabItem", "current", 80, selected: true),
            Ptr(1200, 140), El(1202, 140, "Hyperlink", "standalone", 120));
        Assert.Equal(["Hover"], o.Haptic.Select(d => d.Waveform));
        Assert.Contains(o.Notes, n => n.EndsWith("skip:too-small", StringComparison.Ordinal));
        Assert.Contains(o.Notes, n => n.EndsWith("skip:inline-link", StringComparison.Ordinal));
        Assert.Contains(o.Notes, n => n.EndsWith("skip:already-selected", StringComparison.Ordinal));
    }

    [Fact]
    public void GroupIsRememberedAcrossSeparatorsButNotForever()
    {
        int[] tb = [0, 0, 200, 30];
        var o = Run(
            Ptr(0, 0, 100), Ptr(300, 20), El(302, 20, "Button", "a", 0, parent: "ToolBar", parentRid: "tb", parentBounds: tb),
            Ptr(600, 45), El(602, 45, "Pane", "sep", 40, w: 10),
            Ptr(900, 70), El(902, 70, "Button", "b", 50, parent: "ToolBar", parentRid: "tb", parentBounds: tb),
            Ptr(1200, 70, 200), El(1202, 70, "Pane", "document", 0, w: 400, y: 200, by: 100),
            Ptr(4000, 20), El(4002, 20, "Button", "a", 0, parent: "ToolBar", parentRid: "tb", parentBounds: tb));
        Assert.Equal(["Step", "Hover", "Step"], o.Haptic.Select(d => d.Waveform));
    }

    [Fact]
    public void SlidingOffTheEndOfAStripBumpsLeavingSidewaysDoesNot()
    {
        int[] tb = [0, 0, 120, 30];
        var end = Run(
            Ptr(0, 20, 100), Ptr(300, 100), El(302, 100, "Button", "last", 80, parent: "ToolBar", parentRid: "tb", parentBounds: tb),
            Ptr(600, 140), El(602, 140, "Pane", "beyond", 120, w: 400));
        Assert.Equal(["Step", "Collide"], end.Haptic.Select(d => d.Waveform));

        var down = Run(
            Ptr(0, 20, 100), Ptr(300, 100), El(302, 100, "Button", "last", 80, parent: "ToolBar", parentRid: "tb", parentBounds: tb),
            Ptr(600, 100, 60), El(602, 100, "Pane", "below", 0, w: 400, y: 60, by: 40));
        Assert.Equal(["Step"], down.Haptic.Select(d => d.Waveform));
    }

    [Fact]
    public void DenseAppsThinLandsButKeepStructure()
    {
        // Limit 3 lands per 2 s, a land every 200 ms: 3 tick, then lands thin to one per 667 ms instead of stopping.
        var cfg = new PolicyConfig { Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions, MinimumGlobalSpacingMs = 0, GlobalMaxPulsesPerSecond = 100 };
        cfg.AppProfiles["notepad"] = new AppProfile { DensityLandsPer2s = 3 };
        var lines = new List<string> { Ptr(0, 0) };
        for (var i = 0; i < 11; i++)
        {
            var t = 200 + (i * 200);
            lines.Add(Ptr(t, 20 + (i * 40)));
            lines.Add(El(t + 2, 20 + (i * 40), "Button", $"b{i}", i * 40));
        }

        lines.Add(Ptr(2500, 420, 100));
        lines.Add(El(2502, 420, "TabItem", "tab", 400, y: 100, by: 90));
        var o = Run(cfg, [.. lines]);
        Assert.Equal([202, 402, 602, 1402, 2202], o.Haptic.Where(d => d.Waveform == "Hover").Select(d => d.T));
        Assert.Contains(o.Decisions, d => d.Reason == "density-backoff");
        Assert.Equal("Step", o.Haptic.Last().Waveform);
    }

    [Fact]
    public void SweepingTheTaskbarThinsTicksButNeverGoesSilent()
    {
        // Back and forth over the taskbar at 600 px/s with the tray's own policy. Taskbar buttons are all lands and no
        // structure, so muting lands in a busy app used to mean 4 s of nothing: it felt broken. Geometry is from a live
        // UIA probe at 125%: 55x60 px Buttons under a Pane, looked up in 1-2 ms, so every move gets a snapshot.
        const int left = 620, width = 55, count = 16, top = 1400, y = 1430;
        var lines = new List<string> { """{"type":"process","pid":100,"name":"explorer"}""" };
        var (x, dir) = (left + 5, 1);
        for (var t = 0; t <= 8000; t += 10)
        {
            var i = (x - left) / width;
            lines.Add(Ptr(t, x, y));
            lines.Add(El(t + 2, x, "Button", $"task{i}", left + (i * width), w: width, h: 60, y: y, parent: "Pane",
                parentRid: "taskbar", scale: 1.25, by: top));
            x += dir * 6;
            if (x >= left + (count * width) - 5 || x <= left + 5)
            {
                dir = -dir;
            }
        }

        var o = TraceReplay.Run(lines, PolicyConfig.Sample(EffectProfile.Focused, 0.8));
        var ticks = o.Haptic.Select(d => d.T).ToList();
        Assert.Contains(o.Decisions, d => d.Reason == "density-backoff");
        Assert.True(ticks.Count > 25, $"only {ticks.Count} ticks in 8 s of sweeping");
        var longestSilence = ticks.Zip(ticks.Skip(1), (a, b) => b - a).Max();
        Assert.True(longestSilence < 600, $"went silent for {longestSilence} ms while sweeping");
    }

    [Fact]
    public void CooperatingAppsCanSendOutcomesButStillGoThroughPolicy()
    {
        var o = Run(
            Ptr(0, 0),
            """{"t":100,"type":"coop","waveform":"Align","pid":100}""",
            """{"t":110,"type":"coop","waveform":"Success","pid":100}""",
            """{"t":400,"type":"coop","waveform":"NotAWaveform","pid":100}""",
            """{"type":"process","pid":300,"name":"KeePass"}""",
            """{"t":700,"type":"coop","waveform":"Align","pid":300}""");
        Assert.Equal(["Align"], o.Haptic.Select(d => d.Waveform));
        Assert.Contains(o.Decisions, d => d.Reason == "rate-limit:spacing");
        Assert.Contains(o.Decisions, d => d.Reason == "no-haptic-for-event");
        Assert.Contains(o.Decisions, d => d.Reason == "process-excluded");
    }

    [Fact]
    public void LandingSpeedAdaptsToHowThisUserClicks()
    {
        var i = new SemanticInterpreter();
        Assert.Equal(1200, i.EffectiveLandingSpeed);
        var t = 0.0;
        for (var k = 0; k < 10; k++)
        {
            // Approach at ~100 px/s, then press.
            i.OnPointer(new PointerObservation(t += 100, k * 10, 0, InputOrigin.Replay, false, 1));
            i.OnPointer(new PointerObservation(t += 100, (k * 10) + 10, 0, InputOrigin.Replay, true, 1));
            i.OnPointer(new PointerObservation(t += 100, (k * 10) + 10, 0, InputOrigin.Replay, false, 1));
        }

        Assert.InRange(i.EffectiveLandingSpeed, 400, 600);
    }
}
