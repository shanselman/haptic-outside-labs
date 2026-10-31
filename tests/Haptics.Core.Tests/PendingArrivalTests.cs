using Haptics.Core;

namespace Haptics.Core.Tests;

public class PendingArrivalTests
{
    private static PointerObservation Pointer(double t, int x) => new(t, x, 15, InputOrigin.Replay, false, 100);

    private static ElementSnapshot Button(double requested = 10) => new()
    {
        ProcessId = 100, ControlType = "Button", RuntimeIdHash = "button", RequestedAtMs = requested,
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
        var arrival = Assert.Single(interpreter.OnTick(90).Events);
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
    [InlineData(90)]
    [InlineData(105)]
    [InlineData(120)]
    public void DwellCauseIsTheDeadlineNotTheLateCallbackTime(double callback)
    {
        var interpreter = Pending();
        var arrival = Assert.Single(interpreter.OnTick(callback).Events);
        Assert.Equal(90, arrival.PointerTimestampMs);
        Assert.Equal(callback, arrival.DecidedAtMs);
    }

    [Theory]
    [InlineData(136)]
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
        Assert.Equal(interpreter.Generation, Assert.Single(interpreter.OnTick(190).Events).Generation);
        Assert.Equal(interpreter.Generation, interpreter.Cooperative("Align", 100, 200).Generation);
    }

    [Fact]
    public void SlowValidLookupCanLandOnTheNextTickWithoutRestartingDwell()
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(Pointer(0, 0));
        interpreter.OnPointer(Pointer(10, 20));
        interpreter.OnSnapshot(Button(), 150);
        Assert.True(interpreter.HasPendingArrival);
        var arrival = Assert.Single(interpreter.OnTick(160).Events);
        Assert.Equal(150, arrival.PointerTimestampMs);
        Assert.Equal(160, arrival.DecidedAtMs);
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
        Assert.Contains(interpreter.OnSnapshot(next, 201).Events, e => e.Kind == SemanticEventKind.EnterControl);
    }
}
