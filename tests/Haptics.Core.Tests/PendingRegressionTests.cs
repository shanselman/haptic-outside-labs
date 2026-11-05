using Haptics.Core;

namespace Haptics.Core.Tests;

public class PendingRegressionTests
{
    [Theory]
    [InlineData("transient-error-button.jsonl")]
    [InlineData("transient-error-slider.jsonl")]
    public void TransientFailureDoesNotManufactureAnotherArrival(string trace)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "traces", trace);
        var output = TraceReplay.Run(path,
            new PolicyConfig { Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions, Profile = EffectProfile.Everything });
        Assert.Equal(["Hover"], output.Haptic.Select(d => d.Waveform));
        var focused = TraceReplay.Run(path);
        Assert.Single(focused.Decisions, d => d.EventKind == "EnterControl");
        Assert.Equal(1, focused.Emitted);
    }

    [Theory]
    [InlineData("dwell-lookup-40ms.jsonl", 50)]
    [InlineData("dwell-lookup-150ms.jsonl", 160)]
    public void LookupLatencyDoesNotRestartDwell(string trace, double landedAt)
    {
        var output = TraceReplay.Run(Path.Combine(AppContext.BaseDirectory, "traces", trace));
        Assert.Equal(landedAt, Assert.Single(output.Haptic).T);
    }

    [Theory]
    [InlineData(71)]
    [InlineData(84)]
    [InlineData(85)]
    public void SchedulingDelayThroughFortyFiveMillisecondsStillLands(double tick)
    {
        var interpreter = Pending();
        var landed = Assert.Single(interpreter.OnTick(tick).Events);
        Assert.Equal(40, landed.PointerTimestampMs);
        Assert.Equal(tick, landed.DecidedAtMs);
    }

    [Fact]
    public void BeyondFortyFiveMillisecondsExpiresWithoutMovingTheCause()
    {
        var interpreter = Pending();
        var result = interpreter.OnTick(86);
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
            ProcessId = 100, RuntimeIdHash = "button", ControlType = "ComboBox", RequestedAtMs = 10,
            QueryX = 20, QueryY = 15, Bounds = new RectD(0, 0, 100, 30),
        }, 10);
        return interpreter;
    }
}
