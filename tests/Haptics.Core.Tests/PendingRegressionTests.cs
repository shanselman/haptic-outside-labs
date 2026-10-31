using Haptics.Core;

namespace Haptics.Core.Tests;

public class PendingRegressionTests
{
    [Theory]
    [InlineData("transient-error-button.jsonl")]
    [InlineData("transient-error-slider.jsonl")]
    public void TransientFailureDoesNotManufactureAnotherArrival(string trace)
    {
        var output = TraceReplay.Run(Path.Combine(AppContext.BaseDirectory, "traces", trace));
        Assert.Equal(["Hover"], output.Haptic.Select(d => d.Waveform));
    }

    [Theory]
    [InlineData("dwell-lookup-40ms.jsonl", 90)]
    [InlineData("dwell-lookup-150ms.jsonl", 170)]
    public void LookupLatencyDoesNotRestartDwell(string trace, double landedAt)
    {
        var output = TraceReplay.Run(Path.Combine(AppContext.BaseDirectory, "traces", trace));
        Assert.Equal(landedAt, Assert.Single(output.Haptic).T);
    }

    [Theory]
    [InlineData(121)]
    [InlineData(134)]
    [InlineData(135)]
    public void SchedulingDelayThroughFortyFiveMillisecondsStillLands(double tick)
    {
        var interpreter = Pending();
        var landed = Assert.Single(interpreter.OnTick(tick).Events);
        Assert.Equal(90, landed.PointerTimestampMs);
        Assert.Equal(tick, landed.DecidedAtMs);
    }

    [Fact]
    public void BeyondFortyFiveMillisecondsExpiresWithoutMovingTheCause()
    {
        var interpreter = Pending();
        var result = interpreter.OnTick(136);
        Assert.Empty(result.Events);
        Assert.Contains("arrival-expired", result.Notes);
    }

    private static SemanticInterpreter Pending()
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(new PointerObservation(0, 0, 15, InputOrigin.Replay, false, 100));
        interpreter.OnPointer(new PointerObservation(10, 20, 15, InputOrigin.Replay, false, 100));
        interpreter.OnSnapshot(new ElementSnapshot
        {
            ProcessId = 100, RuntimeIdHash = "button", ControlType = "Button", RequestedAtMs = 10,
            QueryX = 20, QueryY = 15, Bounds = new RectD(0, 0, 100, 30),
        }, 10);
        return interpreter;
    }
}
