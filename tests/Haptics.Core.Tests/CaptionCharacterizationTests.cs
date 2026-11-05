using Haptics.Core;

namespace Haptics.Core.Tests;

public class CaptionCharacterizationTests
{
    private static TraceReplay.ReplayOutput Run(IEnumerable<string> lines) => TraceReplay.Run(lines,
        new PolicyConfig { Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions, Profile = EffectProfile.Everything });

    [Theory]
    [InlineData("maximize", 240)]
    [InlineData("minimize", 200)]
    public void FirstCaptionArrivalIsGroupHoverAndCoalescesTheControlHover(string caption, int left)
    {
        // Everything retains generic caption behavior. Labels are fixture identities, never UIA Names.
        var output = Run([
            """{"type":"process","pid":100,"name":"notepad"}""",
            $$"""{"type":"pointer","t":0,"x":{{left + 20}},"y":15,"fg":100}""",
            $$"""{"type":"element","t":2,"pid":100,"controlType":"Button","rid":"{{caption}}","bounds":[{{left}},0,40,30],"parentType":"TitleBar","parentRid":"caption","parentBounds":[0,0,320,30]}""",
        ]);

        var haptic = Assert.Single(output.Haptic);
        Assert.Equal("Hover", haptic.Waveform);
        Assert.Equal("CrossGroupBoundary", haptic.EventKind);
        Assert.Contains(output.Decisions, d => d.EventKind == "EnterControl" &&
            d.Outcome == PolicyOutcome.Suppressed && d.Reason == "coalesced-with-group-boundary");
    }

    [Fact]
    public void FirstCloseArrivalIsOneHoverWithoutGroupStep()
    {
        // Lane F: was Step (CrossGroupBoundary) with the Hover coalesced; now one EnterControl Hover.
        var output = Run([
            """{"type":"process","pid":100,"name":"notepad"}""",
            """{"type":"pointer","t":0,"x":300,"y":15,"fg":100}""",
            """{"type":"element","t":2,"pid":100,"controlType":"Button","rid":"close","bounds":[280,0,40,30],"parentType":"TitleBar","parentRid":"caption","parentBounds":[0,0,320,30]}""",
        ]);

        var haptic = Assert.Single(output.Haptic);
        Assert.Equal("Hover", haptic.Waveform);
        Assert.Equal("EnterControl", haptic.EventKind);
        Assert.DoesNotContain(output.Decisions, d => d.EventKind == "CrossGroupBoundary");
    }

    [Fact]
    public void MovingWithinCaptionFromMinimizeToMaximizeKeepsHover()
    {
        var output = Run([
            """{"type":"process","pid":100,"name":"notepad"}""",
            """{"type":"pointer","t":0,"x":220,"y":15,"fg":100}""",
            """{"type":"element","t":2,"pid":100,"controlType":"Button","rid":"minimize","bounds":[200,0,40,30],"parentType":"TitleBar","parentRid":"caption","parentBounds":[0,0,320,30]}""",
            """{"type":"pointer","t":300,"x":260,"y":15,"fg":100}""",
            """{"type":"element","t":302,"pid":100,"controlType":"Button","rid":"maximize","bounds":[240,0,40,30],"parentType":"TitleBar","parentRid":"caption","parentBounds":[0,0,320,30]}""",
        ]);

        Assert.Equal(["Hover", "Hover"], output.Haptic.Select(d => d.Waveform));
    }
}
