using Haptics.Core;

namespace Haptics.Core.Tests;

/// <summary>The v0.2 vocabulary: land vs. pass, role waveforms, group-first, edges, choosiness, density, cooperation.</summary>
public class VocabularyTests
{
    [Theory]
    [InlineData(25, 139, 1.5, true)]
    [InlineData(24, 48, 1, true)]
    [InlineData(36, 72, 1.5, true)]
    [InlineData(48, 96, 2, true)]
    [InlineData(24.01, 100, 1, false)]
    [InlineData(36.01, 100, 1.5, false)]
    [InlineData(48.01, 120, 2, false)]
    [InlineData(24, 47.99, 1, false)]
    [InlineData(20, 20, 1, false)]
    [InlineData(95, 95, 1.5, false)]
    public void TextSizedGroupHyperlinksUseDpiAndExactGeometryBoundaries(double height, double parentHeight, double scale, bool eligible)
    {
        var link = new ElementSnapshot
        {
            ControlType = "Hyperlink", ParentControlType = "Group", Scale = scale,
            Bounds = new RectD(0, 0, 249, height), ParentBounds = new RectD(0, 0, 1078, parentHeight),
        };
        Assert.False(link.IsInlineHyperlink);
        Assert.Equal(eligible, link.IsTextSizedGroupHyperlink);
        Assert.Equal(eligible, link.IsFocusedLanding);
    }

    [Theory]
    [InlineData("missing-link")]
    [InlineData("missing-parent")]
    [InlineData("empty-link")]
    [InlineData("outside-left")]
    [InlineData("outside-top")]
    [InlineData("outside-right")]
    [InlineData("outside-bottom")]
    [InlineData("nonfinite-link")]
    [InlineData("nonfinite-parent")]
    [InlineData("nonfinite-scale")]
    [InlineData("zero-scale")]
    [InlineData("list-item")]
    [InlineData("toolbar")]
    [InlineData("button")]
    [InlineData("toggle-button")]
    public void TextSizedGroupHyperlinksDoNotGuessMissingGeometryOrOtherRoles(string scenario)
    {
        var link = new ElementSnapshot
        {
            ControlType = "Hyperlink", ParentControlType = "Group",
            Bounds = new RectD(10, 10, 100, 20), ParentBounds = new RectD(0, 0, 200, 80),
        };
        link = scenario switch
        {
            "missing-link" => link with { Bounds = null },
            "missing-parent" => link with { ParentBounds = null },
            "empty-link" => link with { Bounds = new RectD(10, 10, 0, 20) },
            "outside-left" => link with { Bounds = new RectD(-1, 10, 100, 20) },
            "outside-top" => link with { Bounds = new RectD(10, -1, 100, 20) },
            "outside-right" => link with { Bounds = new RectD(101, 10, 100, 20) },
            "outside-bottom" => link with { Bounds = new RectD(10, 61, 100, 20) },
            "nonfinite-link" => link with { Bounds = new RectD(10, 10, double.NaN, 20) },
            "nonfinite-parent" => link with { ParentBounds = new RectD(0, 0, double.PositiveInfinity, 80) },
            "nonfinite-scale" => link with { Scale = double.PositiveInfinity },
            "zero-scale" => link with { Scale = 0 },
            "list-item" => link with { ParentControlType = "ListItem" },
            "toolbar" => link with { ParentControlType = "ToolBar" },
            "button" => link with { ControlType = "Button" },
            "toggle-button" => link with { ControlType = "Button", IsToggle = true },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        Assert.False(link.IsTextSizedGroupHyperlink);
        Assert.False(link.IsFocusedLanding);
    }

    [Theory]
    [InlineData(EffectProfile.Quiet)]
    [InlineData(EffectProfile.Focused)]
    [InlineData(EffectProfile.Everything)]
    public void TextSizedGroupHyperlinkPolicyKeepsQuietSilentAndHonorsSafety(EffectProfile profile)
    {
        var cfg = PolicyConfig.Sample(profile, 0.8);
        cfg.GroupBoundaryEnabled = false;
        var link = """{"type":"element","t":1,"req":0,"x":20,"y":15,"pid":100,"controlType":"Hyperlink","rid":"group-link","bounds":[10,10,100,20],"parentType":"Group","parentBounds":[0,0,200,80]}""";
        var output = Run(cfg, Ptr(0, 20), link);
        Assert.Equal(profile == EffectProfile.Quiet ? 0 : 1, output.Emitted);
        Assert.All(output.Haptic, d => Assert.Equal("Hover", d.Waveform));
        foreach (var flag in new[] { "\"password\":true", "\"enabled\":false", "\"offscreen\":true" })
        {
            Assert.Empty(Run(cfg, Ptr(0, 20), link[..^1] + "," + flag + "}").Haptic);
        }
    }

    [Theory]
    [InlineData(EffectProfile.Quiet)]
    [InlineData(EffectProfile.Focused)]
    [InlineData(EffectProfile.Everything)]
    public void CheckboxHoverStaysSilentButEverythingToggleButtonsHoverInEitherState(EffectProfile style)
    {
        foreach (var type in new[] { "CheckBox", "Button" })
        foreach (var on in new bool?[] { null, false, true })
        {
            var interpreter = new SemanticInterpreter(new InterpreterConfig { Profile = style });
            interpreter.OnPointer(TogglePointer(0));
            interpreter.OnSnapshot(ToggleSnapshot(1, on, type) with
            {
                IsToggle = type == "Button" || on.HasValue, ParentControlType = "Group", ParentRuntimeIdHash = "group",
            }, 1);
            var events = interpreter.OnTick(50).Events;
            if (style == EffectProfile.Everything && type == "Button")
            {
                Assert.Single(events, e => e.Kind == SemanticEventKind.EnterControl);
            }
            else
            {
                Assert.Empty(events);
            }
            Assert.Empty(interpreter.OnTick(500).Events);
        }
    }

    [Theory]
    [InlineData("Button", "ToolBar", false)]
    [InlineData("Button", "Pane", true)]
    [InlineData("MenuItem", "Menu", false)]
    public void RemovedFocusedCommandsRemainAvailableInEverything(string type, string parent, bool browser)
    {
        var target = new ElementSnapshot
        {
            ProcessId = 100, ControlType = type, ParentControlType = parent,
            IsBrowserToolbarCommand = browser, Bounds = new RectD(0, 0, 40, 30),
        };
        foreach (var style in new[] { EffectProfile.Focused, EffectProfile.Everything })
        {
            var policy = new HapticPolicy(PolicyConfig.Sample(style, 0.8), _ => "app")
            {
                ConsentGiven = true, OutputRouteVerified = true,
            };
            var arrival = new SemanticEvent(SemanticEventKind.EnterControl, 1, 50, 50, 100, type,
                target.ElementKey, null, 1, ["landed:dwell"], false, 0, target);
            Assert.Equal(style == EffectProfile.Focused ? PolicyOutcome.Suppressed : PolicyOutcome.Emit,
                policy.Evaluate(arrival, 50).Outcome);
        }
    }

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

    private static string Caption(double t, int x, string rid, int bx, int w, int[] tb, bool primary = false, string extra = "") =>
        Ptr(t, x, 140, primary) + "\n" +
        "{" + $"\"t\":{t + 2},\"type\":\"element\",\"req\":{t},\"x\":{x},\"y\":140,\"pid\":100,\"controlType\":\"Button\",\"rid\":\"{rid}\"," +
        $"\"bounds\":[{bx},121,{w},44],\"scale\":1.5,\"parentType\":\"TitleBar\",\"parentRid\":\"caption\",\"parentBounds\":[{string.Join(',', tb)}]{extra}" + "}";

    private static TraceReplay.ReplayOutput RunCaption(PolicyConfig? cfg, params string[] lines) =>
        Run(cfg ?? new PolicyConfig { Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions, Profile = EffectProfile.Everything },
            [Ptr(0, 300, 250), .. lines.SelectMany(l => l.Split('\n')), Ptr(2000, 300, 250)]);

    // WinForms caption at 150%, measured by CaptionCloseUiaTests: TitleBar [131,131,398,34].
    private static readonly int[] MeasuredTitleBar = [131, 131, 398, 34];

    [Fact]
    public void CloseLandsWithOneLightHoverNeverStepOrClick()
    {
        var o = RunCaption(null, Caption(300, 494, "close", 459, 71, MeasuredTitleBar));
        var d = Assert.Single(o.Haptic);
        Assert.Equal(("Hover", "EnterControl"), (d.Waveform, d.EventKind));
        Assert.DoesNotContain(o.Decisions, x => x.EventKind == "CrossGroupBoundary");

        // Even a Close big enough to count as a large target stays a light Hover.
        var large = RunCaption(null, Ptr(300, 300, 40) + "\n" +
            El(302, 300, "Button", "close", 276, w: 44, h: 80, y: 40, parent: "TitleBar", parentRid: "tb", parentBounds: [0, 0, 320, 80]));
        Assert.Equal(["Hover"], large.Haptic.Select(x => x.Waveform));
    }

    [Fact]
    public void CloseHoverOffMakesCloseSilentOnly()
    {
        var cfg = new PolicyConfig { Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions, Profile = EffectProfile.Everything, CloseButtonHoverEnabled = false };
        var o = RunCaption(cfg, Caption(300, 494, "close", 459, 71, MeasuredTitleBar), Caption(600, 424, "max", 389, 70, MeasuredTitleBar));
        Assert.Equal(["Hover"], o.Haptic.Select(x => x.Waveform));
        Assert.Contains(o.Decisions, x => x.Reason == "rule-disabled:close-hover");
        Assert.True(Assert.Single(o.Haptic).T >= 600);
    }

    [Theory]
    [InlineData("ltr-min", 318, 70, false)]
    [InlineData("ltr-max", 389, 70, false)]
    [InlineData("rtl-close", 130, 71, false)]
    [InlineData("rtl-max", 201, 70, false)]
    [InlineData("rtl-min", 271, 71, false)]
    [InlineData("ltr-close", 459, 71, true)]
    public void OnlyTheButtonTouchingTheTitleBarRightEdgeIsClose(string rid, int bx, int w, bool close)
    {
        // Measured fixture geometry. No direction field is trusted, so mirrored (RTL) captions keep generic behavior.
        var o = RunCaption(null, Caption(300, bx + 10, rid, bx, w, MeasuredTitleBar));
        Assert.Equal("Hover", Assert.Single(o.Haptic).Waveform);
        Assert.Equal(close ? "EnterControl" : "CrossGroupBoundary", Assert.Single(o.Haptic).EventKind);
    }

    [Fact]
    public void RightEdgeButtonsOutsideTheTitleBarOrWithoutParentBoundsStayGeneric()
    {
        var toolbar = RunCaption(null, Ptr(300, 300) + "\n" +
            El(302, 300, "Button", "tbEnd", 280, parent: "ToolBar", parentRid: "tb", parentBounds: [0, 0, 320, 30]));
        Assert.Equal(["Hover"], toolbar.Haptic.Select(x => x.Waveform));
        var noParentBounds = RunCaption(null, Ptr(300, 300) + "\n" + El(302, 300, "Button", "close", 280, parent: "TitleBar", parentRid: "tb"));
        Assert.Equal(["Hover"], noParentBounds.Haptic.Select(x => x.Waveform));
        Assert.False(SemanticVocabulary.IsCaptionClose(null));
    }

    [Theory]
    [InlineData(",\"enabled\":false", false, "notepad")]
    [InlineData(",\"password\":true", false, "notepad")]
    [InlineData("", true, "notepad")]
    [InlineData("", false, "KeePass")]
    public void CloseKeepsEverySafetySilence(string extra, bool primary, string process)
    {
        var o = TraceReplay.Run([$$"""{"type":"process","pid":100,"name":"{{process}}"}""",
            Ptr(0, 300, 250), .. Caption(300, 494, "close", 459, 71, MeasuredTitleBar, primary, extra).Split('\n'), Ptr(1000, 494, 141, primary)],
            new PolicyConfig { Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions, Profile = EffectProfile.Everything });
        Assert.Empty(o.Haptic);
    }

    [Fact]
    public void CloseKeepsDwellAndReentryCooldown()
    {
        // Flying across Close does not land; coming straight back inside the 500 ms cooldown is silent.
        var flyBy = RunCaption(null, Ptr(290, 420, 140), Caption(300, 494, "close", 459, 71, MeasuredTitleBar), Ptr(310, 540, 140),
            "{\"t\":312,\"type\":\"element\",\"req\":310,\"x\":540,\"y\":140,\"pid\":100,\"controlType\":\"Pane\",\"rid\":\"desktop\",\"bounds\":[0,0,2000,2000],\"scale\":1.5}");
        Assert.Empty(flyBy.Haptic);
        Assert.Contains(flyBy.Notes, n => n.EndsWith("passed-through", StringComparison.Ordinal));
        var back = RunCaption(null, Caption(300, 494, "close", 459, 71, MeasuredTitleBar), Caption(500, 424, "max", 389, 70, MeasuredTitleBar),
            Caption(700, 494, "close", 459, 71, MeasuredTitleBar));
        Assert.Equal(["Hover", "Hover"], back.Haptic.Select(x => x.Waveform));
        Assert.Contains(back.Decisions, x => x.Reason == "reentry-cooldown");
    }

    [Fact]
    public void FlyingPastIsSilentStoppingLands()
    {
        // 3000 px/s sweep over A, B; stop on C.
        var o = Run(
            Ptr(0, 0), Ptr(10, 20), El(12, 20, "ComboBox", "A", 0),
            Ptr(20, 50), El(22, 50, "ComboBox", "B", 40),
            Ptr(30, 90), El(32, 90, "ComboBox", "C", 80));
        Assert.Single(o.Haptic);
        Assert.Contains(o.Notes, n => n.EndsWith("passed-through", StringComparison.Ordinal));
        Assert.Contains(o.Decisions, d => d.Outcome == PolicyOutcome.Emit && d.T >= 30 + 30);
    }

    [Fact]
    public void EverythingStyleLandsOnEachDwelledControl()
    {
        var cfg = new PolicyConfig { Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions };
        cfg.ApplyProfile(EffectProfile.Everything);
        var o = Run(cfg,
            Ptr(0, 0), Ptr(100, 20), El(102, 20, "Button", "A", 0), Ptr(200, 60), El(202, 60, "Button", "B", 40), Ptr(300, 100), El(302, 100, "Button", "C", 80));
        Assert.Equal(3, o.Emitted);
    }

    [Theory]
    [InlineData(EffectProfile.Focused)]
    [InlineData(EffectProfile.Quiet)]
    [InlineData(EffectProfile.Everything)]
    public void WaveformFollowsRoleAndStyle(EffectProfile profile)
    {
        var cfg = new PolicyConfig { Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions };
        cfg.ApplyProfile(profile);
        cfg.GroupBoundaryEnabled = false;
        var o = Run(cfg,
            Ptr(0, 0), Ptr(300, 20), El(302, 20, "Button", "small", 0, parent: "ToolBar"),
            Ptr(600, 60, 40), El(602, 60, "Button", "ribbonLarge", 40, w: 44, h: 80, y: 40, parent: "ToolBar"),
            Ptr(900, 120), El(902, 120, "TabItem", "tab", 100, w: 80, parent: "Tab"),
            Ptr(1200, 220), El(1202, 220, "RadioButton", "radio", 200, w: 80));
        string[] expected = profile switch
        {
            EffectProfile.Focused => ["Hover"],
            EffectProfile.Quiet => ["Hover"],
            _ => ["Hover", "Hover", "Hover", "Hover"],
        };
        Assert.Equal(expected, o.Haptic.Select(d => d.Waveform));
        if (profile == EffectProfile.Focused)
        {
            Assert.Contains(o.Notes, n => n.EndsWith("skip:focused-target-not-enabled", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(40, 30, 1)]
    [InlineData(64, 60, 2)]
    [InlineData(200, 100, 1)]
    [InlineData(400, 200, 2)]
    public void TargetSizeAndDpiNeverChangeALandingToClick(int width, int height, double scale)
    {
        var o = Run(new PolicyConfig { Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions, Profile = EffectProfile.Everything },
            Ptr(0, 0), Ptr(300, 20), El(302, 20, "Button", "b", 0, w: width, h: height, scale: scale));
        Assert.Equal(["Hover"], o.Haptic.Select(d => d.Waveform));
    }

    [Fact]
    public void FocusedIncludesInlineLinksButSkipsStandaloneLinksTinyTargetsAndTheTabYouAreOn()
    {
        var o = Run(
            Ptr(0, 0), Ptr(300, 20), El(302, 20, "Button", "tiny", 15, w: 8, h: 8),
            Ptr(600, 60), El(602, 60, "Hyperlink", "inline", 40, parent: "Text", parentRid: "para"),
            Ptr(900, 100), El(902, 100, "TabItem", "current", 80, selected: true),
            Ptr(1200, 140), El(1202, 140, "Hyperlink", "standalone", 120, parent: "ToolBar"));
        Assert.Equal(["Hover"], o.Haptic.Select(d => d.Waveform));
        Assert.Contains(o.Notes, n => n.EndsWith("skip:too-small", StringComparison.Ordinal));
        Assert.DoesNotContain(o.Notes, n => n.EndsWith("skip:inline-link", StringComparison.Ordinal));
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
        Assert.Empty(o.Haptic);
        var everything = Run(new PolicyConfig { Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions, Profile = EffectProfile.Everything },
            Ptr(0, 0, 100), Ptr(300, 20), El(302, 20, "Button", "a", 0, parent: "ToolBar", parentRid: "tb", parentBounds: tb),
            Ptr(600, 45), El(602, 45, "Pane", "sep", 40, w: 10),
            Ptr(900, 70), El(902, 70, "Button", "b", 50, parent: "ToolBar", parentRid: "tb", parentBounds: tb),
            Ptr(1200, 70, 200), El(1202, 70, "Pane", "document", 0, w: 400, y: 200, by: 100),
            Ptr(4000, 20), El(4002, 20, "Button", "a", 0, parent: "ToolBar", parentRid: "tb", parentBounds: tb));
        Assert.Equal(2, everything.Haptic.Count(d => d.EventKind == "CrossGroupBoundary"));
        Assert.All(everything.Haptic, d => Assert.Equal("Hover", d.Waveform));
    }

    [Fact]
    public void QuietDoesNotRestoreFocusedGroupOrStripEdgeSilence()
    {
        int[] tb = [0, 0, 120, 30];
        var cfg = new PolicyConfig { Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions, Profile = EffectProfile.Quiet };
        var end = Run(cfg,
            Ptr(0, 20, 100), Ptr(300, 100), El(302, 100, "Button", "last", 80, parent: "ToolBar", parentRid: "tb", parentBounds: tb),
            Ptr(600, 140), El(602, 140, "Pane", "beyond", 120, w: 400));
        Assert.Empty(end.Haptic);

        var down = Run(cfg,
            Ptr(0, 20, 100), Ptr(300, 100), El(302, 100, "Button", "last", 80, parent: "ToolBar", parentRid: "tb", parentBounds: tb),
            Ptr(600, 100, 60), El(602, 100, "Pane", "below", 0, w: 400, y: 60, by: 40));
        Assert.Empty(down.Haptic);
    }

    [Fact]
    public void ExplicitDensityLimitsApplyToTabHoverInFocusedAndEverything()
    {
        // Limit 3 lands per 2 s, a land every 200 ms: 3 tick, then lands thin to one per 667 ms instead of stopping.
        var cfg = new PolicyConfig { Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions, MinimumGlobalSpacingMs = 0, GlobalMaxPulsesPerSecond = 100, GroupBoundaryEnabled = false, DensityLandsPer2s = 3 };
        cfg.AppProfiles["notepad"] = new AppProfile { DensityLandsPer2s = 3 };
        var lines = new List<string> { Ptr(0, 0) };
        for (var i = 0; i < 11; i++)
        {
            var t = 200 + (i * 200);
            lines.Add(Ptr(t, 20 + (i * 40)));
            lines.Add(El(t + 2, 20 + (i * 40), "ComboBox", $"b{i}", i * 40));
        }

        lines.Add(Ptr(2500, 420, 100));
        lines.Add(El(2502, 420, "TabItem", "tab", 400, y: 100, by: 90, parent: "Tab"));
        var focused = Run(cfg, [.. lines]);
        Assert.DoesNotContain(focused.Haptic, d => d.ControlType == "TabItem");
        Assert.Contains(focused.Decisions, d => d.ControlType == "TabItem" && d.Reason == "density-backoff");
        cfg.Profile = EffectProfile.Everything;
        var o = Run(cfg, [.. lines]);
        Assert.Equal([232, 432, 632, 1432, 2232], o.Haptic.Where(d => d.Waveform == "Hover").Select(d => d.T));
        Assert.Contains(o.Decisions, d => d.Reason == "density-backoff");
        Assert.DoesNotContain(o.Haptic, d => d.ControlType == "TabItem");
        Assert.Contains(o.Decisions, d => d.ControlType == "TabItem" && d.Reason == "density-backoff");
    }

    [Fact]
    public void DefaultSampleDoesNotApplyAppDensityThinning()
    {
        // Back and forth over ordinary commands at 600 px/s, with no default density thinning.
        const int left = 620, width = 55, count = 16, top = 1400, y = 1430;
        var lines = new List<string> { """{"type":"process","pid":100,"name":"explorer"}""" };
        var (x, dir) = (left + 5, 1);
        for (var t = 0; t <= 8000; t += 10)
        {
            var i = (x - left) / width;
            lines.Add(Ptr(t, x, y));
            lines.Add(El(t + 2, x, "Button", $"command{i}", left + (i * width), w: width, h: 60, y: y, parent: "Pane",
                parentRid: "commands", scale: 1.25, by: top));
            x += dir * 6;
            if (x >= left + (count * width) - 5 || x <= left + 5)
            {
                dir = -dir;
            }
        }

        var o = TraceReplay.Run(lines, PolicyConfig.Sample(EffectProfile.Everything, 0.8));
        var ticks = o.Haptic.Select(d => d.T).ToList();
        Assert.DoesNotContain(o.Decisions, d => d.Reason == "density-backoff");
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

    [Theory]
    [InlineData("Button", "TitleBar", CaptionButton.Close, false, false, false)]
    [InlineData("Button", "TitleBar", CaptionButton.Minimize, false, false, false)]
    [InlineData("Button", "TitleBar", CaptionButton.Maximize, false, false, false)]
    [InlineData("Button", "TitleBar", CaptionButton.None, false, false, false)]
    [InlineData("Button", "ToolBar", CaptionButton.None, false, false, false)]
    [InlineData("Hyperlink", "ToolBar", CaptionButton.None, false, false, false)]
    [InlineData("TabItem", "Tab", CaptionButton.None, false, false, true)]
    [InlineData("ComboBox", "Pane", CaptionButton.None, false, true, true)]
    [InlineData("ListItem", "ComboBox", CaptionButton.None, false, false, false)]
    [InlineData("MenuItem", "Menu", CaptionButton.None, false, true, true)]
    [InlineData("SplitButton", "Pane", CaptionButton.None, false, true, false)]
    [InlineData("Button", "Pane", CaptionButton.None, false, true, false)]
    [InlineData("Button", "ToolBar", CaptionButton.None, true, false, false)]
    [InlineData("CheckBox", "ToolBar", CaptionButton.None, false, false, false)]
    [InlineData("RadioButton", "Group", CaptionButton.None, false, false, false)]
    [InlineData("Slider", "Pane", CaptionButton.None, false, false, true)]
    [InlineData("Button", "Pane", CaptionButton.None, false, false, false)]
    [InlineData("Hyperlink", "Pane", CaptionButton.None, false, false, false)]
    [InlineData("Hyperlink", "Document", CaptionButton.None, false, false, true)]
    [InlineData("Hyperlink", "Pane", CaptionButton.None, false, true, false)]
    [InlineData("ListItem", "List", CaptionButton.None, false, false, false)]
    public void FocusedLandingsAreExplicit(string type, string parent, CaptionButton caption, bool toggle, bool dropdown, bool allowed)
    {
        var element = new ElementSnapshot
        {
            ProcessId = 100, ControlType = type, ParentControlType = parent, CaptionButton = caption,
            IsToggle = toggle, HasDropdown = dropdown, Bounds = new RectD(0, 0, 40, 30),
            SliderThumbBounds = type == "Slider" ? new RectD(10, 5, 20, 20) : null, QueryX = 20, QueryY = 15,
        };
        var policy = new HapticPolicy(PolicyConfig.Sample(EffectProfile.Focused, 0.55), _ => "app")
        {
            ConsentGiven = true, OutputRouteVerified = true,
        };
        var e = new SemanticEvent(SemanticEventKind.EnterControl, 1, 0, 0, 100, type, element.ElementKey, null, 1, [], false, 0, element);
        var result = policy.Evaluate(e, 0);
        Assert.Equal(allowed ? PolicyOutcome.Emit : PolicyOutcome.Suppressed, result.Outcome);
        if (allowed)
        {
            Assert.Equal("Hover", result.Intent?.Waveform);
        }
    }

    [Theory]
    [InlineData("Button", "ToolBar", true)]
    [InlineData("TabItem", "Tab", false)]
    [InlineData("ComboBox", "Pane", false)]
    [InlineData("MenuItem", "MenuBar", false)]
    [InlineData("SplitButton", "ToolBar", false)]
    [InlineData("Button", "MenuBar", true)]
    public void FocusedNavigationAlwaysUsesHoverIncludingLargeTargets(string type, string parent, bool dropdown)
    {
        foreach (var style in new[] { EffectProfile.Focused, EffectProfile.Everything })
        foreach (var (width, height) in new[] { (40, 30), (44, 80), (48, 48) })
        {
            var element = new ElementSnapshot
            {
                ProcessId = 100, ControlType = type, ParentControlType = parent, HasDropdown = dropdown,
                Bounds = new RectD(0, 0, width, height),
            };
            var config = PolicyConfig.Sample(style, 0.55);
            config.DetentsEnabled = false;
            var policy = new HapticPolicy(config, _ => "app") { ConsentGiven = true, OutputRouteVerified = true };
            var e = new SemanticEvent(SemanticEventKind.EnterControl, 1, 0, 0, 100, type, element.ElementKey,
                null, 1, [], false, 0, element);
            var decision = policy.Evaluate(e, 0);
            Assert.Equal(PolicyOutcome.Emit, decision.Outcome);
            Assert.Equal("Hover", decision.Intent?.Waveform);
            Assert.Equal(type == "TabItem" ? "tab-land" : "land", decision.Intent?.SourceRule);
        }
    }

    [Theory]
    [InlineData("TabItem", "Tab", false, "navigation-tab")]
    [InlineData("Button", "ToolBar", true, "menu-launcher")]
    [InlineData("SplitButton", "ToolBar", false, "menu-launcher")]
    [InlineData("MenuItem", "Menu", true, "menu-launcher")]
    [InlineData("MenuItem", "MenuBar", true, "menu-launcher")]
    [InlineData("MenuItem", "MenuItem", true, "menu-launcher")]
    [InlineData("Button", "Menu", true, "menu-launcher")]
    [InlineData("Button", "MenuBar", true, "menu-launcher")]
    [InlineData("Button", "MenuItem", true, "menu-launcher")]
    [InlineData("SplitButton", "Menu", false, "menu-launcher")]
    [InlineData("ComboBox", "Pane", true, "picker")]
    public void FocusedNavigationRecordsStructuralEvidence(string type, string parent, bool dropdown, string reason)
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(TogglePointer(0));
        var element = ToggleSnapshot(1, null, type) with
        {
            IsToggle = false, ParentControlType = parent, HasDropdown = dropdown,
        };
        Assert.Equal(reason, element.FocusedNavigationReason());
        Assert.Empty(interpreter.OnSnapshot(element, 1).Events);
        var arrival = Assert.Single(interpreter.OnTick(50).Events, e => e.Kind == SemanticEventKind.EnterControl);
        Assert.Contains($"focused:{reason}", arrival.Evidence);
    }

    [Theory]
    [InlineData("ms-teams")]
    [InlineData("outlook")]
    [InlineData("chrome")]
    [InlineData("unknown-app")]
    public void FocusedContentCardsCannotOptIntoHoverByExpanding(string process)
    {
        foreach (var type in new[] { "Button", "SplitButton", "TabItem", "MenuItem", "Pane", "Group", "ListItem", "TreeItem", "DataItem" })
        foreach (var parent in new string?[] { null, "Pane", "Group", "List", "ListItem", "Tree", "DataItem", "Document" })
        foreach (var dropdown in new[] { false, true })
        {
            var element = new ElementSnapshot
            {
                ProcessId = 100, ControlType = type, ParentControlType = parent, HasDropdown = dropdown,
                Bounds = new RectD(0, 0, 200, 200),
            };
            Assert.Null(element.FocusedNavigationReason());
            Assert.False(element.IsFocusedLanding, $"{type}/{parent}, dropdown={dropdown}");
            var policy = new HapticPolicy(PolicyConfig.Sample(EffectProfile.Focused, 0.8), _ => process)
            {
                ConsentGiven = true, OutputRouteVerified = true,
            };
            var arrival = new SemanticEvent(SemanticEventKind.EnterControl, 1, 0, 0, 100, type,
                element.ElementKey, null, 1, [], false, 0, element);
            Assert.Equal("focused-target-not-enabled", policy.Evaluate(arrival, 0).Reason);
        }
    }

    [Theory]
    [InlineData("TabItem", false, true)]
    [InlineData("MenuItem", true, true)]
    [InlineData("MenuItem", false, false)]
    [InlineData("Button", true, false)]
    [InlineData("Pane", true, false)]
    [InlineData("Text", true, false)]
    public void HeaderOwnershipOnlyQualifiesTabsAndExpandableMenuItems(string type, bool dropdown, bool eligible)
    {
        var element = new ElementSnapshot { ControlType = type, IsHeaderOwner = true, HasDropdown = dropdown };
        Assert.Equal(eligible, element.IsFocusedLanding);
    }

    [Fact]
    public void FocusedAmbiguousMenuLaunchersStaySilentButEverythingKeepsTheirLandings()
    {
        foreach (var type in new[] { "Button", "SplitButton", "MenuItem", "TabItem" })
        {
            var snapshot = El(2, 20, type, "content", 0, parent: "Pane");
            snapshot = snapshot[..^1] + ",\"dropdown\":true}";
            Assert.Empty(Run(Ptr(0, 20), snapshot).Haptic);
            var everything = new PolicyConfig
            {
                Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions, Profile = EffectProfile.Everything,
            };
            Assert.Single(Run(everything, Ptr(0, 20), snapshot).Haptic);
        }
    }

    [Fact]
    public void FocusedPendingTabLosesEligibilityWhenFreshMetadataShowsContent()
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(TogglePointer(0) with { X = 0 });
        interpreter.OnPointer(TogglePointer(10));
        var tab = ToggleSnapshot(12, null, "TabItem") with
        {
            RequestedAtMs = 10, IsToggle = false, ParentControlType = "Tab",
        };
        Assert.Empty(interpreter.OnSnapshot(tab, 12).Events);
        Assert.True(interpreter.HasPendingArrival);
        interpreter.OnSnapshot(tab with { RequestedAtMs = 30, ParentControlType = "Group", HasDropdown = true }, 32);
        var result = interpreter.OnTick(60);
        var policy = new HapticPolicy(PolicyConfig.Sample(EffectProfile.Focused, 0.8), _ => "ms-teams")
        {
            ConsentGiven = true, OutputRouteVerified = true,
        };
        Assert.DoesNotContain(result.Events, e => policy.Evaluate(e, 60).Outcome == PolicyOutcome.Emit);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FocusedNeverLandsOnCloseEvenIfTheLegacySettingIsEnabled(bool enabled)
    {
        var cfg = PolicyConfig.Sample(EffectProfile.Focused, 0.55);
        cfg.CloseButtonHoverEnabled = enabled;
        var output = Run(cfg, Ptr(0, 20, 40),
            """{"t":2,"type":"element","req":0,"x":20,"y":40,"pid":100,"controlType":"Button","rid":"native-close","caption":"Close","bounds":[0,0,44,80]}""");
        Assert.Empty(output.Haptic);
    }

    [Fact]
    public void NativeCaptionClassificationTakesPrecedenceOverGeometry()
    {
        var candidate = new ElementSnapshot
        {
            ControlType = "Button", ParentControlType = "TitleBar",
            Bounds = new RectD(280, 0, 40, 30), ParentBounds = new RectD(0, 0, 320, 30),
        };
        Assert.True(SemanticVocabulary.IsCaptionClose(candidate));
        Assert.False(candidate.IsFocusedLanding);
        Assert.False(SemanticVocabulary.IsCaptionClose(candidate with { CaptionButton = CaptionButton.Maximize }));
        Assert.False(SemanticVocabulary.IsCaptionClose(candidate with { CaptionButton = CaptionButton.Minimize }));
        Assert.False((candidate with { CaptionButton = CaptionButton.Maximize }).IsFocusedLanding);
        Assert.False((candidate with { CaptionButton = CaptionButton.Minimize }).IsFocusedLanding);
        Assert.False((candidate with { CaptionButton = CaptionButton.Close }).IsFocusedLanding);
    }

    [Theory]
    [InlineData("", false, "notepad", false)]
    [InlineData(",\"password\":true", false, "notepad", false)]
    [InlineData(",\"enabled\":false", false, "notepad", false)]
    [InlineData(",\"offscreen\":true", false, "notepad", false)]
    [InlineData("", true, "notepad", false)]
    [InlineData("", false, "KeePass", false)]
    public void FocusedGeometryOnlyCloseIsRemoved(string extra, bool primary, string process, bool allowed)
    {
        var lines = new[]
        {
            $$"""{"type":"process","pid":100,"name":"{{process}}"}""",
            Ptr(0, 300, 250),
        }.Concat(Caption(300, 494, "close", 459, 71, MeasuredTitleBar, primary, extra).Split('\n'));
        var config = PolicyConfig.Sample(EffectProfile.Focused, 0.8);
        var output = TraceReplay.Run(lines, config);
        Assert.Equal(allowed ? 1 : 0, output.Emitted);
        Assert.All(output.Haptic, d => Assert.Equal(("Hover", "EnterControl"), (d.Waveform, d.EventKind)));
        Assert.DoesNotContain(output.Decisions, d => d.EventKind == "CrossGroupBoundary");
        config.CloseButtonHoverEnabled = false;
        Assert.Empty(TraceReplay.Run(lines, config).Haptic);
    }

    [Theory]
    [InlineData(459, 71, 0)]
    [InlineData(389, 70, 0)]
    [InlineData(318, 70, 0)]
    public void FocusedCaptionWithoutParentBoundsDoesNotGuessClose(int left, int width, int expected)
    {
        var output = Run(Ptr(0, left + 10, 140),
            El(2, left + 10, "Button", "caption", left, w: width, h: 44, y: 140, by: 121, parent: "TitleBar"));
        Assert.Equal(expected, output.Emitted);
    }

    [Fact]
    public void BoundlessCaptionDwellNeedsConfirmationAtTheCurrentPointerPosition()
    {
        foreach (var refresh in new[] { false, true })
        {
            var interpreter = new SemanticInterpreter(new InterpreterConfig { Profile = EffectProfile.Everything });
            interpreter.OnPointer(TogglePointer(0) with { X = 0 });
            interpreter.OnPointer(TogglePointer(10));
            var close = ToggleSnapshot(12, null, "Button") with
            {
                Source = "native-caption", CaptionButton = CaptionButton.Close, Bounds = null, IsToggle = false,
                RequestedAtMs = 10,
            };
            Assert.Empty(interpreter.OnSnapshot(close, 12).Events);
            Assert.True(interpreter.HasPendingArrival);
            Assert.Empty(interpreter.OnPointer(TogglePointer(20) with { X = 40 }).Events);
            Assert.Empty(interpreter.OnPointer(TogglePointer(30) with { X = 40 }).Events);
            if (refresh)
            {
                Assert.Empty(interpreter.OnSnapshot(close with { QueryX = 40, RequestedAtMs = 30 }, 35).Events);
            }

            var result = interpreter.OnTick(90);
            Assert.Equal(refresh ? 1 : 0, result.Events.Count(e => e.Kind == SemanticEventKind.EnterControl));
        }
    }

    [Fact]
    public void FocusedDropdownHoverDoesNotAddAClickPulse()
    {
        var o = Run(
            Ptr(0, 20), El(2, 20, "ComboBox", "combo", 0),
            Ptr(100, 20, primary: true), El(102, 20, "ComboBox", "combo", 0),
            Ptr(120, 20), El(122, 20, "ComboBox", "combo", 0),
            Ptr(400, 70), El(402, 70, "ListItem", "option", 50, parent: "ComboBox"));
        Assert.Equal(["Hover"], o.Haptic.Select(d => d.Waveform));
    }

    [Theory]
    [InlineData("Document")]
    [InlineData("Text")]
    [InlineData("Edit")]
    [InlineData("Pane")]
    [InlineData("Group")]
    [InlineData("ToolBar")]
    public void InlineLinksHoverInEveryStyleAndOtherLinksAreEverythingOnly(string parent)
    {
        var link = El(2, 20, "Hyperlink", "link", 0, parent: parent);
        foreach (var profile in new[] { EffectProfile.Quiet, EffectProfile.Focused, EffectProfile.Everything })
        {
            var cfg = new PolicyConfig { Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions, Profile = profile, GroupBoundaryEnabled = false };
            var o = Run(cfg, Ptr(0, 20), link);
            Assert.Equal(profile == EffectProfile.Everything || SemanticVocabulary.TextContainers.Contains(parent) ? 1 : 0, o.Emitted);
            Assert.All(o.Haptic, d => Assert.Equal("Hover", d.Waveform));
            foreach (var flag in new[] { "\"password\":true", "\"enabled\":false", "\"offscreen\":true" })
            {
                Assert.Empty(Run(cfg, Ptr(0, 20), link[..^1] + "," + flag + "}").Haptic);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BrowserToolbarPanelIsNeitherFocusedHoverNorToggleConfirmation(bool before)
    {
        var interpreter = new SemanticInterpreter();
        var snapshot = ToggleSnapshot(1, before, "Button") with { IsBrowserToolbarCommand = true };
        interpreter.OnPointer(TogglePointer(0));
        Assert.Empty(interpreter.OnSnapshot(snapshot, 1).Events);
        Assert.Empty(interpreter.OnTick(50).Events);
        Assert.False(interpreter.ObservingToggle);
        var policy = new HapticPolicy(PolicyConfig.Sample(EffectProfile.Focused, 0.55), _ => "chrome")
        {
            ConsentGiven = true, OutputRouteVerified = true,
        };
        interpreter.OnPointer(TogglePointer(20, down: true));
        interpreter.OnPointer(TogglePointer(40));
        Assert.Empty(interpreter.OnSnapshot(snapshot with { RequestedAtMs = 55, IsToggledOn = !before }, 55).Events);
        Assert.False(interpreter.NeedsToggleRefresh(60));
    }

    [Theory]
    [InlineData("CheckBox", false)]
    [InlineData("CheckBox", true)]
    [InlineData("Button", false)]
    [InlineData("Button", true)]
    public void ToggleChangeWaitsForReleaseAndObservedStateAndEmitsOnlyOnce(string type, bool before)
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(TogglePointer(0));
        interpreter.OnSnapshot(ToggleSnapshot(1, before, type), 1);
        interpreter.OnPointer(TogglePointer(20, down: true));
        Assert.DoesNotContain(interpreter.OnSnapshot(ToggleSnapshot(25, !before, type), 25).Events,
            e => e.Kind == SemanticEventKind.ToggleChanged);
        interpreter.OnPointer(TogglePointer(40));
        Assert.True(interpreter.NeedsToggleRefresh(50));
        var e = Assert.Single(interpreter.OnSnapshot(ToggleSnapshot(55, !before, type), 55).Events);
        Assert.Equal(SemanticEventKind.ToggleChanged, e.Kind);
        Assert.Equal(!before, e.Element?.IsToggledOn);
        Assert.Contains(before ? "toggle-state:on-to-off" : "toggle-state:off-to-on", e.Evidence);
        Assert.Equal(40, e.PointerTimestampMs);
        var policy = new HapticPolicy(PolicyConfig.Sample(EffectProfile.Focused, 0.55), _ => "app")
        {
            ConsentGiven = true, OutputRouteVerified = true,
        };
        Assert.Equal("Step", policy.Evaluate(e, 55).Intent?.Waveform);
        Assert.False(interpreter.NeedsToggleRefresh(60));
        Assert.Empty(interpreter.OnSnapshot(ToggleSnapshot(65, !before, type), 65).Events);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(null, true)]
    [InlineData(null, false)]
    [InlineData(false, null)]
    [InlineData(true, null)]
    public void ToggleDoesNotInventAStateChange(bool? before, bool? after)
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(TogglePointer(0));
        interpreter.OnSnapshot(ToggleSnapshot(1, before), 1);
        interpreter.OnPointer(TogglePointer(20, down: true));
        interpreter.OnPointer(TogglePointer(40));
        Assert.DoesNotContain(interpreter.OnSnapshot(ToggleSnapshot(55, after), 55).Events,
            e => e.Kind == SemanticEventKind.ToggleChanged);
    }

    [Fact]
    public void StationaryToggleRefreshKeepsTheBaselineFreshWithoutEmitting()
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(TogglePointer(0));
        interpreter.OnSnapshot(ToggleSnapshot(1, false), 1);
        Assert.True(interpreter.ObservingToggle);
        Assert.Empty(interpreter.OnSnapshot(ToggleSnapshot(2000, false), 2000).Events);
        interpreter.OnPointer(TogglePointer(2020, down: true));
        interpreter.OnPointer(TogglePointer(2040));
        Assert.Equal(SemanticEventKind.ToggleChanged,
            Assert.Single(interpreter.OnSnapshot(ToggleSnapshot(2050, true), 2050).Events).Kind);
    }

    [Theory]
    [InlineData("no-click")]
    [InlineData("leave")]
    [InlineData("foreground")]
    [InlineData("device")]
    [InlineData("identity")]
    [InlineData("unknown")]
    [InlineData("disabled")]
    [InlineData("password")]
    [InlineData("offscreen")]
    [InlineData("device-switch")]
    [InlineData("pre-release-request")]
    [InlineData("late")]
    [InlineData("stale-baseline")]
    [InlineData("quiet")]
    public void ToggleConfirmationFailsClosed(string scenario)
    {
        foreach (var style in new[] { EffectProfile.Focused, EffectProfile.Everything })
        {
            AssertToggleConfirmationFailsClosed(scenario, before: false, style);
            AssertToggleConfirmationFailsClosed(scenario, before: true, style);
        }
    }

    private static void AssertToggleConfirmationFailsClosed(string scenario, bool before, EffectProfile style)
    {
        var interpreter = new SemanticInterpreter(new InterpreterConfig { Profile = style });
        interpreter.OnPointer(TogglePointer(0));
        interpreter.OnSnapshot(ToggleSnapshot(1, before), 1);
        if (scenario == "stale-baseline")
        {
            interpreter.OnPointer(TogglePointer(1000, down: true));
            interpreter.OnPointer(TogglePointer(1020));
            Assert.Empty(interpreter.OnSnapshot(ToggleSnapshot(1030, !before), 1030).Events);
            return;
        }

        if (scenario != "no-click")
        {
            interpreter.OnPointer(TogglePointer(20, down: true));
            interpreter.OnPointer(TogglePointer(40));
        }

        switch (scenario)
        {
            case "leave": interpreter.OnPointer(TogglePointer(45) with { X = 200 }); break;
            case "foreground": interpreter.OnPointer(TogglePointer(45) with { ForegroundPid = 200 }); break;
            case "device": interpreter.OnDeviceUnavailable(45); break;
            case "device-switch": interpreter.OnPointer(TogglePointer(45) with { DeviceHandle = 8 }); break;
            case "unknown": interpreter.OnSnapshot(ToggleSnapshot(45, null), 45); break;
            case "quiet": interpreter.Config.Profile = EffectProfile.Quiet; break;
        }

        var at = scenario == "late" ? 250 : 55;
        var snapshot = ToggleSnapshot(at, !before);
        if (scenario == "identity")
        {
            snapshot = snapshot with { RuntimeIdHash = "different-control" };
        }

        snapshot = scenario switch
        {
            "disabled" => snapshot with { IsEnabled = false },
            "password" => snapshot with { IsPassword = true },
            "offscreen" => snapshot with { IsOffscreen = true },
            "pre-release-request" => snapshot with { RequestedAtMs = 30 },
            _ => snapshot,
        };

        Assert.DoesNotContain(interpreter.OnSnapshot(snapshot, at).Events, e => e.Kind == SemanticEventKind.ToggleChanged);
    }

    [Theory]
    [InlineData(false, 190, true)]
    [InlineData(true, 190, true)]
    [InlineData(false, 191, false)]
    [InlineData(true, 191, false)]
    public void ToggleConfirmationKeepsThe150MillisecondReleaseDeadline(bool before, double observedAt, bool accepted)
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(TogglePointer(0));
        interpreter.OnSnapshot(ToggleSnapshot(1, before), 1);
        interpreter.OnPointer(TogglePointer(20, down: true));
        interpreter.OnPointer(TogglePointer(40));
        var result = interpreter.OnSnapshot(ToggleSnapshot(observedAt, !before), observedAt);
        Assert.Equal(accepted ? 1 : 0, result.Events.Count(e => e.Kind == SemanticEventKind.ToggleChanged));
    }

    private static PointerObservation TogglePointer(double t, bool down = false) => new(t, 20, 15, InputOrigin.Replay, down, 100, 7);

    [Theory]
    [InlineData("class-change-during-click", false)]
    [InlineData("class-change-at-press", false)]
    [InlineData("invalidation", false)]
    [InlineData("class-change-during-click", true)]
    [InlineData("class-change-at-press", true)]
    [InlineData("invalidation", true)]
    public void ToggleConfirmationCannotCrossAnInteractionGeneration(string scenario, bool before)
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(TogglePointer(0));
        interpreter.OnSnapshot(ToggleSnapshot(1, before), 1);
        var inputClass = scenario == "class-change-at-press" ? PointerInputClass.Promoted : PointerInputClass.Handle;
        interpreter.OnPointer(TogglePointer(20, down: true) with { InputClass = inputClass });
        interpreter.OnPointer(TogglePointer(40) with { InputClass = inputClass });
        if (scenario == "invalidation")
        {
            interpreter.Invalidate("test");
        }

        interpreter.OnPointer(TogglePointer(45) with { InputClass = PointerInputClass.Promoted });
        Assert.True(interpreter.Generation > 0);
        var fresh = ToggleSnapshot(55, !before) with { Generation = interpreter.Generation };
        Assert.DoesNotContain(interpreter.OnSnapshot(fresh, 55).Events, e => e.Kind == SemanticEventKind.ToggleChanged);
        Assert.False(interpreter.NeedsToggleRefresh(55));
    }

    private static ElementSnapshot ToggleSnapshot(double t, bool? on, string type = "CheckBox") => new()
    {
        ProcessId = 100, ControlType = type, RuntimeIdHash = "toggle", Bounds = new RectD(0, 0, 100, 30),
        IsToggle = true, IsToggledOn = on, RequestedAtMs = t, ReceivedAtMs = t, QueryX = 20, QueryY = 15,
    };
}
