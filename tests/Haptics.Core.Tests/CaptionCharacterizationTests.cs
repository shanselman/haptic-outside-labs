using Haptics.Core;

namespace Haptics.Core.Tests;

public class CaptionCharacterizationTests
{
    [Theory]
    [InlineData("close", 280)]
    [InlineData("maximize", 240)]
    [InlineData("minimize", 200)]
    public void FirstCaptionArrivalIsStepAndCoalescesHover(string caption, int left)
    {
        // Current behavior; lane F changes Close only. The label is fixture identity, never UIA Name.
        var output = TraceReplay.Run([
            """{"type":"process","pid":100,"name":"notepad"}""",
            $$"""{"type":"pointer","t":0,"x":{{left + 20}},"y":15,"fg":100}""",
            $$"""{"type":"element","t":2,"pid":100,"controlType":"Button","rid":"{{caption}}","bounds":[{{left}},0,40,30],"parentType":"TitleBar","parentRid":"caption","parentBounds":[0,0,320,30]}""",
        ]);

        var haptic = Assert.Single(output.Haptic);
        Assert.Equal("Step", haptic.Waveform);
        Assert.Equal("CrossGroupBoundary", haptic.EventKind);
        Assert.Contains(output.Decisions, d => d.EventKind == "EnterControl" &&
            d.Outcome == PolicyOutcome.Suppressed && d.Reason == "coalesced-with-group-boundary");
    }

    [Fact]
    public void MovingWithinCaptionFromMinimizeToMaximizeKeepsHover()
    {
        var output = TraceReplay.Run([
            """{"type":"process","pid":100,"name":"notepad"}""",
            """{"type":"pointer","t":0,"x":220,"y":15,"fg":100}""",
            """{"type":"element","t":2,"pid":100,"controlType":"Button","rid":"minimize","bounds":[200,0,40,30],"parentType":"TitleBar","parentRid":"caption","parentBounds":[0,0,320,30]}""",
            """{"type":"pointer","t":300,"x":260,"y":15,"fg":100}""",
            """{"type":"element","t":302,"pid":100,"controlType":"Button","rid":"maximize","bounds":[240,0,40,30],"parentType":"TitleBar","parentRid":"caption","parentBounds":[0,0,320,30]}""",
        ]);

        Assert.Equal(["Step", "Hover"], output.Haptic.Select(d => d.Waveform));
    }
}
