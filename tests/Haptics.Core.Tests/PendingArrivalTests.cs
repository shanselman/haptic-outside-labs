using Haptics.Core;

namespace Haptics.Core.Tests;

public class PendingArrivalTests
{
    [Theory]
    [InlineData("taller-link")]
    [InlineData("smaller-parent")]
    [InlineData("missing-parent")]
    [InlineData("reparented")]
    public void GroupLinkDwellCancelsWhenFreshMetadataLosesEligibility(string change)
    {
        var interpreter = new SemanticInterpreter();
        var link = Button(0) with
        {
            ControlType = "Hyperlink", ParentControlType = "Group",
            Bounds = new RectD(0, 0, 100, 20), ParentBounds = new RectD(0, 0, 200, 80),
        };
        interpreter.OnPointer(Pointer(0, 20));
        interpreter.OnSnapshot(link, 1);
        Assert.True(interpreter.HasPendingArrival);
        var updated = change switch
        {
            "taller-link" => link with { Bounds = new RectD(0, 0, 100, 25) },
            "smaller-parent" => link with { ParentBounds = new RectD(0, 0, 200, 39) },
            "missing-parent" => link with { ParentBounds = null },
            "reparented" => link with { ParentControlType = "ListItem" },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        interpreter.OnSnapshot(updated with { RequestedAtMs = 10 }, 11);
        Assert.False(interpreter.HasPendingArrival);
        interpreter.OnSnapshot(link with { RequestedAtMs = 20 }, 21);
        Assert.Empty(interpreter.OnTick(30).Events);
        Assert.False(interpreter.HasPendingArrival);
    }

    [Theory]
    [InlineData("ComboBox", 47, 55, 1, 30)]
    [InlineData("ComboBox", 48, 48, 1, 50)]
    [InlineData("ComboBox", 47, 56, 1, 50)]
    [InlineData("ComboBox", 100, 30, 1, 30)]
    [InlineData("ComboBox", 94, 110, 2, 30)]
    [InlineData("ComboBox", 96, 96, 2, 50)]
    [InlineData("ComboBox", 70.5, 84, 1.5, 50)]
    [InlineData("ComboBox", 71.9, 83.9, 1.5, 30)]
    [InlineData("TabItem", 40, 20, 1, 50)]
    [InlineData("TabItem", 80, 40, 2, 50)]
    [InlineData("Slider", 20, 20, 1, 30)]
    [InlineData("Slider", 48, 48, 1, 50)]
    public void DwellUsesLogicalTargetSizeAndTabsAlwaysWaitFifty(string type, double width, double height, double scale, double dwell)
    {
        foreach (var style in Enum.GetValues<EffectProfile>())
        {
            var interpreter = new SemanticInterpreter(new InterpreterConfig { Profile = style });
            var bounds = new RectD(0, 0, width, height);
            var element = Button(0) with
            {
                ControlType = type, ParentControlType = type == "TabItem" ? "Tab" : null,
                Bounds = type == "Slider" ? new RectD(0, 0, 500, 300) : bounds,
                SliderThumbBounds = type == "Slider" ? bounds : null, Scale = scale, QueryX = 1, QueryY = 1,
            };
            interpreter.OnPointer(Pointer(0, 1) with { Y = 1 });
            interpreter.OnSnapshot(element, 1);
            Assert.Empty(interpreter.OnTick(dwell - 1).Events);
            var arrival = Assert.Single(interpreter.OnTick(dwell).Events);
            Assert.Equal(dwell, arrival.PointerTimestampMs);
            Assert.Empty(interpreter.OnTick(dwell + 10).Events);
        }
    }

    [Fact]
    public void GeometryRefreshMayExtendButNeverShortenAnExistingDwell()
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(Pointer(0, 20));
        interpreter.OnSnapshot(Button(0), 1);
        interpreter.OnSnapshot(Button(10) with { Bounds = new RectD(0, 0, 100, 56) }, 11);
        interpreter.OnSnapshot(Button(20), 21);
        Assert.Empty(interpreter.OnTick(49).Events);
        Assert.Equal(50, Assert.Single(interpreter.OnTick(50).Events).PointerTimestampMs);
        interpreter.OnPointer(Pointer(100, 150));
        interpreter.OnPointer(Pointer(200, 20));
        interpreter.OnSnapshot(Button(200), 201);
        Assert.Empty(interpreter.OnTick(229).Events);
        Assert.Equal(230, Assert.Single(interpreter.OnTick(230).Events).PointerTimestampMs);
    }

    [Fact]
    public void MissingBoundsUseConservativeFiftyMillisecondDwell()
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(Pointer(0, 20));
        interpreter.OnSnapshot(Button(0) with { Bounds = null }, 1);
        Assert.Empty(interpreter.OnTick(49).Events);
        Assert.Equal(50, Assert.Single(interpreter.OnTick(50).Events).PointerTimestampMs);
    }

    [Theory]
    [InlineData(EffectProfile.Focused)]
    [InlineData(EffectProfile.Everything)]
    public void SliderTrackToThumbStartsItsOwnDwellAndUnsampledExitRearmsIt(EffectProfile style)
    {
        var interpreter = new SemanticInterpreter(new InterpreterConfig { Profile = style });
        var track = Button(0) with { ControlType = "Slider", Bounds = new RectD(0, 0, 200, 30) };
        interpreter.OnPointer(Pointer(0, 20));
        interpreter.OnSnapshot(track, 1);
        Assert.False(interpreter.HasPendingArrival);
        Assert.Empty(interpreter.OnTick(50).Events);
        var thumb = track with { SliderThumbBounds = new RectD(90, 5, 20, 20), QueryX = 100, RequestedAtMs = 100 };
        interpreter.OnPointer(Pointer(100, 100));
        interpreter.OnSnapshot(thumb, 101);
        Assert.Empty(interpreter.OnTick(129).Events);
        Assert.Equal(130, Assert.Single(interpreter.OnTick(130).Events).PointerTimestampMs);
        interpreter.OnPointer(Pointer(400, 111)); // Still on the slider, but outside its thumb by one pixel.
        interpreter.OnPointer(Pointer(420, 100));
        interpreter.OnSnapshot(thumb with { RequestedAtMs = 420 }, 421);
        Assert.Empty(interpreter.OnTick(449).Events);
        Assert.Equal(450, Assert.Single(interpreter.OnTick(450).Events).PointerTimestampMs);
    }

    [Theory]
    [InlineData(EffectProfile.Focused)]
    [InlineData(EffectProfile.Everything)]
    public void SliderThumbExitOrLostMetadataCancelsHoverAndStationaryThumbChangesStaySilent(EffectProfile style)
    {
        foreach (var loseMetadata in new[] { false, true })
        {
            var interpreter = new SemanticInterpreter(new InterpreterConfig { Profile = style });
            var slider = Button(0) with { ControlType = "Slider", SliderThumbBounds = new RectD(10, 5, 20, 20) };
            interpreter.OnPointer(Pointer(0, 20));
            interpreter.OnSnapshot(slider, 1);
            if (loseMetadata)
            {
                interpreter.OnSnapshot(slider with { RequestedAtMs = 20, SliderThumbBounds = null }, 21);
                interpreter.OnSnapshot(slider with { RequestedAtMs = 30 }, 31);
            }
            else
            {
                interpreter.OnPointer(Pointer(20, 31));
            }
            Assert.Empty(interpreter.OnTick(50).Events);
            Assert.False(interpreter.HasPendingArrival);
        }
    }

    [Theory]
    [InlineData(EffectProfile.Focused, false, false)]
    [InlineData(EffectProfile.Focused, true, false)]
    [InlineData(EffectProfile.Quiet, false, false)]
    [InlineData(EffectProfile.Quiet, true, false)]
    [InlineData(EffectProfile.Everything, false, false)]
    [InlineData(EffectProfile.Everything, true, false)]
    [InlineData(EffectProfile.Focused, true, true)]
    [InlineData(EffectProfile.Quiet, true, true)]
    [InlineData(EffectProfile.Everything, true, true)]
    public void DelayedNeighborReplyCannotConsumeTheFinalTabLanding(EffectProfile style, bool neighborWasCurrent, bool boundsOverlap)
    {
        var interpreter = new SemanticInterpreter(new InterpreterConfig { Profile = style });
        var first = new ElementSnapshot
        {
            ProcessId = 100, ControlType = "TabItem", ParentControlType = "Tab", RuntimeIdHash = "first",
            Bounds = new RectD(neighborWasCurrent ? 40 : 0, 0, neighborWasCurrent ? 60 : 40, 30),
            QueryX = neighborWasCurrent ? 90 : 20, QueryY = 15, RequestedAtMs = 0,
        };
        interpreter.OnPointer(Pointer(0, first.QueryX));
        interpreter.OnSnapshot(first, 1);
        interpreter.OnPointer(Pointer(10, 90));
        interpreter.OnPointer(Pointer(20, 110));
        interpreter.OnSnapshot(first with
        {
            RuntimeIdHash = neighborWasCurrent ? "first" : "crossed", RequestedAtMs = 10,
            QueryX = 90, Bounds = new RectD(40, 0, boundsOverlap ? 80 : 60, 30),
        }, 30);
        var destination = first with
        {
            RuntimeIdHash = "destination", RequestedAtMs = 20, QueryX = 110, Bounds = new RectD(100, 0, 100, 30),
        };
        var result = interpreter.OnSnapshot(destination, 40);
        Assert.DoesNotContain("stationary-ui-change-silent", result.Notes);
        Assert.Empty(interpreter.OnTick(69).Events);
        var arrival = Assert.Single(interpreter.OnTick(70).Events);
        Assert.Equal(destination.ElementKey, arrival.ElementKey);
        Assert.Equal(70, arrival.PointerTimestampMs);
    }

    [Theory]
    [InlineData(EffectProfile.Focused)]
    [InlineData(EffectProfile.Quiet)]
    [InlineData(EffectProfile.Everything)]
    public void LandingNearATabEdgeIsNotHeldOnTheTabThatWasCrossed(EffectProfile style)
    {
        var interpreter = new SemanticInterpreter(new InterpreterConfig { Profile = style });
        var first = Button(0) with { ControlType = "TabItem", ParentControlType = "Tab", Bounds = new RectD(0, 0, 40, 30) };
        interpreter.OnPointer(Pointer(0, 20));
        interpreter.OnSnapshot(first, 1);
        interpreter.OnPointer(Pointer(20, 42));
        var next = first with { RequestedAtMs = 20, RuntimeIdHash = "next", QueryX = 42, Bounds = new RectD(40, 0, 40, 30) };
        interpreter.OnSnapshot(next, 21);
        Assert.Empty(interpreter.OnTick(69).Events);
        Assert.Equal(next.ElementKey, Assert.Single(interpreter.OnTick(70).Events).ElementKey);
    }

    private static PointerObservation Pointer(double t, int x) => new(t, x, 15, InputOrigin.Replay, false, 100);

    private static ElementSnapshot Button(double requested = 10) => new()
    {
        ProcessId = 100, ControlType = "ComboBox", RuntimeIdHash = "button", RequestedAtMs = requested,
        QueryX = 20, QueryY = 15, Bounds = new RectD(0, 0, 100, 30),
    };

    private static SemanticInterpreter Pending()
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(Pointer(0, 0));
        interpreter.OnPointer(Pointer(10, 20));
        interpreter.OnSnapshot(Button(), 10);
        Assert.True(interpreter.HasPendingArrival);
        return interpreter;
    }

    [Theory]
    [InlineData("password")]
    [InlineData("disabled")]
    [InlineData("offscreen")]
    [InlineData("error")]
    public void NewUnsafeMetadataCancelsPendingArrival(string flag)
    {
        var interpreter = Pending();
        var newer = Button(20) with
        {
            IsPassword = flag == "password",
            IsEnabled = flag != "disabled",
            IsOffscreen = flag == "offscreen",
            Error = flag == "error" ? "provider-failed" : null,
        };
        interpreter.OnSnapshot(newer, 21);
        Assert.False(interpreter.HasPendingArrival);
        Assert.Empty(interpreter.OnTick(90).Events);
        Assert.Empty(interpreter.OnPointer(Pointer(30, 20)).Events);
    }

    [Fact]
    public void NewerSafeMetadataIsUsedForTheArrival()
    {
        var interpreter = Pending();
        var newer = Button(20) with { Bounds = new RectD(0, 0, 100, 80), Scale = 2 };
        interpreter.OnSnapshot(newer, 21);
        var arrival = Assert.Single(interpreter.OnTick(80).Events);
        Assert.Same(newer, arrival.Element);
    }

    [Fact]
    public void OutOfOrderMetadataCannotOverwriteANewerPendingSnapshot()
    {
        var interpreter = Pending();
        var newer = Button(20) with { Bounds = new RectD(0, 0, 100, 80) };
        interpreter.OnSnapshot(newer, 21);
        interpreter.OnSnapshot(Button(15), 22);
        Assert.Same(newer, Assert.Single(interpreter.OnTick(90).Events).Element);
    }

    [Fact]
    public void LookupErrorClearsPendingEvenWithoutAnElementIdentity()
    {
        var interpreter = Pending();
        interpreter.OnSnapshot(new ElementSnapshot { Error = "timeout", RequestedAtMs = 20, QueryX = 20, QueryY = 15 }, 21);
        Assert.False(interpreter.HasPendingArrival);
        Assert.NotNull(interpreter.Current);
        Assert.Empty(interpreter.OnTick(90).Events);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(85)]
    public void DwellCauseIsTheDeadlineNotTheLateCallbackTime(double callback)
    {
        var interpreter = Pending();
        var arrival = Assert.Single(interpreter.OnTick(callback).Events);
        Assert.Equal(40, arrival.PointerTimestampMs);
        Assert.Equal(callback, arrival.DecidedAtMs);
    }

    [Theory]
    [InlineData(86)]
    [InlineData(5000)]
    public void LateTickExpiresInsteadOfManufacturingAFreshCause(double callback)
    {
        var interpreter = Pending();
        var result = interpreter.OnTick(callback);
        Assert.Empty(result.Events);
        Assert.Contains("arrival-expired", result.Notes);
        Assert.False(interpreter.HasPendingArrival);
    }

    [Fact]
    public void ADelayedPointerCannotReviveAnExpiredPendingArrival()
    {
        var interpreter = Pending();
        var result = interpreter.OnPointer(Pointer(5000, 20));
        Assert.Empty(result.Events);
        Assert.False(interpreter.HasPendingArrival);
    }

    [Fact]
    public void EventsCarryTheInteractionGenerationIncludingDwellAndCooperation()
    {
        var interpreter = Pending();
        interpreter.Invalidate("test");
        interpreter.OnPointer(Pointer(100, 0));
        interpreter.OnPointer(Pointer(110, 20));
        interpreter.OnSnapshot(Button(110) with { Generation = interpreter.Generation }, 111);
        Assert.Equal(interpreter.Generation, Assert.Single(interpreter.OnTick(180).Events).Generation);
        Assert.Equal(interpreter.Generation, interpreter.Cooperative("Align", 100, 200).Generation);
    }

    [Fact]
    public void SlowValidLookupLandsAsSoonAsMetadataArrivesWithoutRestartingDwell()
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(Pointer(0, 0));
        interpreter.OnPointer(Pointer(10, 20));
        var arrival = Assert.Single(interpreter.OnSnapshot(Button(), 150).Events);
        Assert.False(interpreter.HasPendingArrival);
        Assert.Equal(150, arrival.PointerTimestampMs);
        Assert.Equal(150, arrival.DecidedAtMs);
        Assert.Empty(interpreter.OnTick(160).Events);
    }

    [Fact]
    public void IgnoringAnOlderSnapshotDoesNotConsumeNewPointerMovement()
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(Pointer(0, 20));
        interpreter.OnSnapshot(Button(0), 0);
        interpreter.OnSnapshot(Button(20), 20);
        interpreter.OnPointer(Pointer(200, 140));
        Assert.Contains("older-snapshot-discarded", interpreter.OnSnapshot(Button(10) with { QueryX = 140 }, 200).Notes);
        var next = Button(200) with { RuntimeIdHash = "next", QueryX = 140, Bounds = new RectD(120, 0, 40, 30) };
        Assert.DoesNotContain(interpreter.OnSnapshot(next, 201).Events, e => e.Kind == SemanticEventKind.EnterControl);
        Assert.Contains(interpreter.OnTick(250).Events, e => e.Kind == SemanticEventKind.EnterControl);
    }

    [Theory]
    [InlineData(EffectProfile.Focused, 10)]
    [InlineData(EffectProfile.Focused, 1000)]
    [InlineData(EffectProfile.Everything, 10)]
    [InlineData(EffectProfile.Everything, 1000)]
    [InlineData(EffectProfile.Quiet, 10)]
    [InlineData(EffectProfile.Quiet, 1000)]
    public void SmallTargetsRequireThirtyMillisecondsRegardlessOfStyleOrApproachSpeed(EffectProfile style, double entry)
    {
        var interpreter = new SemanticInterpreter(new InterpreterConfig { Profile = style, LandingSpeedPxPerSec = 0 });
        interpreter.OnPointer(Pointer(0, -100));
        interpreter.OnPointer(Pointer(entry, 20));
        Assert.Empty(interpreter.OnSnapshot(Button(entry), entry + 1).Events);
        Assert.Empty(interpreter.OnPointer(Pointer(entry + 20, 21)).Events);
        Assert.Empty(interpreter.OnTick(entry + 29).Events);
        var arrival = Assert.Single(interpreter.OnTick(entry + 30).Events);
        Assert.Equal(SemanticEventKind.EnterControl, arrival.Kind);
        Assert.Equal(entry + 30, arrival.PointerTimestampMs);
        Assert.Contains("landed:dwell", arrival.Evidence);
        Assert.Empty(interpreter.OnTick(entry + 100).Events);
    }

    [Theory]
    [InlineData(EffectProfile.Focused)]
    [InlineData(EffectProfile.Everything)]
    [InlineData(EffectProfile.Quiet)]
    public void PassingThroughThenReturningToTheSameTabCanDwellWithoutAnInterveningSnapshot(EffectProfile style)
    {
        var interpreter = new SemanticInterpreter(new InterpreterConfig { Profile = style });
        var tab = Button(10) with { ControlType = "TabItem", ParentControlType = "Tab", IsSelected = false };
        interpreter.OnPointer(Pointer(0, -100));
        interpreter.OnPointer(Pointer(10, 20));
        interpreter.OnSnapshot(tab, 11);
        Assert.Contains("passed-through", interpreter.OnPointer(Pointer(30, 120)).Notes);
        Assert.Empty(interpreter.OnTick(60).Events);
        interpreter.OnPointer(Pointer(70, 20));
        interpreter.OnSnapshot(tab with { RequestedAtMs = 70 }, 71);
        Assert.Empty(interpreter.OnTick(119).Events);
        Assert.Equal(120, Assert.Single(interpreter.OnTick(120).Events).PointerTimestampMs);
    }

    [Fact]
    public void FocusedSliderHoverWaitsForDwell()
    {
        var interpreter = new SemanticInterpreter();
        var slider = Button(0) with
        {
            ControlType = "Slider", Bounds = new RectD(0, 0, 200, 80), SliderThumbBounds = new RectD(10, 5, 20, 20),
        };
        interpreter.OnPointer(Pointer(0, 20));
        Assert.Empty(interpreter.OnSnapshot(slider, 1).Events);
        Assert.Empty(interpreter.OnTick(29).Events);
        var arrival = Assert.Single(interpreter.OnTick(30).Events);
        Assert.Contains("focused:slider-hover", arrival.Evidence);
        Assert.Empty(interpreter.OnTick(100).Events);
    }

    [Theory]
    [InlineData(false, EffectProfile.Focused)]
    [InlineData(true, EffectProfile.Focused)]
    [InlineData(false, EffectProfile.Everything)]
    [InlineData(true, EffectProfile.Everything)]
    public void SliderPressOrDragEntryCannotAddHoverButKeepsDetents(bool enteredDragging, EffectProfile style)
    {
        var interpreter = new SemanticInterpreter(new InterpreterConfig { Profile = style });
        var slider = Button(0) with
        {
            ControlType = "Slider", Range = new RangeInfo(50, 0, 100, 1, 10),
            SliderThumbBounds = new RectD(10, 5, 20, 20),
        };
        interpreter.OnPointer(Pointer(0, 20) with { PrimaryDown = enteredDragging });
        interpreter.OnSnapshot(slider, 1);
        Assert.Equal(!enteredDragging, interpreter.HasPendingArrival);
        interpreter.OnPointer(Pointer(20, 21) with { PrimaryDown = true });
        Assert.False(interpreter.HasPendingArrival);
        var moved = slider with { RequestedAtMs = 20, Range = new RangeInfo(60, 0, 100, 1, 10) };
        Assert.Equal(SemanticEventKind.RangeStep, Assert.Single(interpreter.OnSnapshot(moved, 21).Events).Kind);
        Assert.Empty(interpreter.OnTick(50).Events);
        interpreter.OnPointer(Pointer(100, 21));
        Assert.Empty(interpreter.OnSnapshot(moved with { RequestedAtMs = 100 }, 101).Events);
        Assert.Empty(interpreter.OnTick(150).Events);
    }

    [Fact]
    public void EverythingHyperlinkDwellsOnceAndFlybysRemainSilent()
    {
        var interpreter = new SemanticInterpreter(new InterpreterConfig { Profile = EffectProfile.Everything });
        var link = Button(0) with { ControlType = "Hyperlink", ParentControlType = "Text" };
        interpreter.OnPointer(Pointer(0, 20));
        Assert.Empty(interpreter.OnSnapshot(link, 1).Events);
        Assert.Empty(interpreter.OnTick(29).Events);
        var arrival = Assert.Single(interpreter.OnTick(30).Events);
        Assert.Equal("Hyperlink", arrival.ControlType);
        Assert.Equal(30, arrival.PointerTimestampMs);
        Assert.Empty(interpreter.OnTick(100).Events);

        interpreter.OnPointer(Pointer(300, 140));
        interpreter.OnSnapshot(link with
        {
            RequestedAtMs = 300, RuntimeIdHash = "next-link", QueryX = 140, Bounds = new RectD(120, 0, 100, 30),
        }, 301);
        Assert.Contains("passed-through", interpreter.OnPointer(Pointer(320, 260)).Notes);
        Assert.Empty(interpreter.OnTick(350).Events);
    }

    [Fact]
    public void DelayedLookupOutsideTheTargetCannotSkipPointerContainment()
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(Pointer(0, 20));
        interpreter.OnPointer(Pointer(10, 42));
        var snapshot = Button(0) with { Bounds = new RectD(0, 0, 30, 30) };
        var result = interpreter.OnSnapshot(snapshot, 80);
        Assert.Empty(result.Events);
        Assert.Contains("superseded-snapshot-discarded", result.Notes);
        interpreter.OnPointer(Pointer(100, 20));
        interpreter.OnSnapshot(snapshot with { RequestedAtMs = 100 }, 101);
        Assert.Equal(130, Assert.Single(interpreter.OnTick(130).Events).PointerTimestampMs);
    }
}
