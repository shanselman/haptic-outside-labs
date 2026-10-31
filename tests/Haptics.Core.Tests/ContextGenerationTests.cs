using Haptics.Core;

namespace Haptics.Core.Tests;

public class ContextGenerationTests
{
    private static PointerObservation Pointer(double t, int x = 20, int foreground = 100, long handle = 1) =>
        new(t, x, 15, InputOrigin.Replay, false, foreground, handle);

    private static ElementSnapshot Button(double requested = 10) => new()
    {
        ProcessId = 100, ControlType = "Button", RuntimeIdHash = "button",
        Bounds = new RectD(0, 0, 100, 30), QueryX = 20, QueryY = 15, RequestedAtMs = requested,
    };

    [Fact]
    public void ForegroundChangeRejectsAnOldAnswerEvenAtTheSamePointAndTime()
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(Pointer(10));
        var old = Button();
        interpreter.OnPointer(Pointer(10, foreground: 200));

        var result = interpreter.OnSnapshot(old, 11);

        Assert.Empty(result.Events);
        Assert.Contains("generation-snapshot-discarded", result.Notes);
        Assert.Null(interpreter.Current);
    }

    [Fact]
    public void DeviceChangeRejectsAnOldAnswerEvenAtTheSamePointAndTime()
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(Pointer(10));
        var old = Button();
        interpreter.OnPointer(Pointer(10, handle: 2));

        var result = interpreter.OnSnapshot(old, 11);

        Assert.Empty(result.Events);
        Assert.Contains("generation-snapshot-discarded", result.Notes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeviceChangeCancelsDwellWithoutRearrivingOnTheSameElement(bool changeClass)
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(Pointer(0, x: 0));
        interpreter.OnPointer(Pointer(10));
        interpreter.OnSnapshot(Button(), 11);
        Assert.True(interpreter.HasPendingArrival);

        var next = changeClass
            ? Pointer(20) with { InputClass = PointerInputClass.Promoted }
            : Pointer(20, handle: 2);
        Assert.Empty(interpreter.OnPointer(next).Events);

        Assert.False(interpreter.HasPendingArrival);
        Assert.Empty(interpreter.OnTick(100).Events);
        Assert.NotNull(interpreter.Current);
        Assert.DoesNotContain(interpreter.OnSnapshot(Button(20) with { Generation = interpreter.Generation }, 21).Events,
            e => e.Kind == SemanticEventKind.EnterControl);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("shutdown")]
    [InlineData("device-removed")]
    public void InvalidationRevokesAllContextAndOldAnswers(string reason)
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(Pointer(10));
        interpreter.OnSnapshot(Button(), 11);
        var before = interpreter.Generation;

        Assert.Contains($"state-cleared:{reason}", interpreter.Invalidate(reason).Notes);
        Assert.Equal(before + 1, interpreter.Generation);
        Assert.Null(interpreter.Current);
        Assert.Null(interpreter.LastPointer);
        Assert.False(interpreter.HasPendingArrival);
        interpreter.OnPointer(Pointer(20));
        Assert.Contains("generation-snapshot-discarded", interpreter.OnSnapshot(Button(), 21).Notes);
        Assert.Contains(interpreter.OnSnapshot(Button(20) with { Generation = interpreter.Generation }, 21).Events,
            e => e.Kind == SemanticEventKind.EnterControl);
    }

    [Fact]
    public void RepeatedGenerationChangesDoNotReviveAnOldAnswer()
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(Pointer(10));
        interpreter.OnPointer(Pointer(10, handle: 2));
        interpreter.OnPointer(Pointer(10));
        Assert.Equal(2, interpreter.Generation);
        Assert.Contains("generation-snapshot-discarded", interpreter.OnSnapshot(Button(), 11).Notes);
        Assert.Contains(interpreter.OnSnapshot(Button() with { Generation = 2 }, 11).Events,
            e => e.Kind == SemanticEventKind.EnterControl);
    }
}
