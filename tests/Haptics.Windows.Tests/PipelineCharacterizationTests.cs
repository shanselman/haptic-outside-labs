using System.Reflection;
using System.Runtime.CompilerServices;
using Haptics.Core;

namespace Haptics.Windows.Tests;

/// <summary>Baseline probes use only pre-seam APIs so the same tests run on the parent revision.</summary>
public class PipelineCharacterizationTests
{
    [Theory]
    [InlineData("Button", 40, 30, "Hover")]
    [InlineData("Button", 44, 80, "Click")]
    [InlineData("TabItem", 80, 30, "Step")]
    public void SingleMouseArrivalKeepsItsWaveform(string type, int width, int height, string waveform)
    {
        var clock = new FakeClock { NowMs = 100 };
        using var pipeline = Pipeline();
        var mouse = Controller();
        pipeline.Controllers = new Dictionary<long, CapturedController> { [12] = mouse };
        Pointer(pipeline, clock.NowMs, 20, 15, 12);
        var result = pipeline.Interpreter.OnSnapshot(Snapshot(clock.NowMs, type, width, height), clock.NowMs);
        var decisions = result.Events.Select(e => pipeline.Policy.Evaluate(e, clock.NowMs)).ToArray();

        Assert.Equal(waveform, Assert.Single(decisions, d => d.Outcome == PolicyOutcome.Emit).Intent!.Waveform);
        Assert.Same(mouse, LatestController(pipeline));
    }

    [Fact]
    public void SingleMouseLeavingStripEndKeepsCollide()
    {
        var clock = new FakeClock { NowMs = 100 };
        using var pipeline = Pipeline();
        Pointer(pipeline, clock.NowMs, 100, 15, 12);
        var button = Snapshot(clock.NowMs, "Button", 40, 30) with
        {
            Bounds = new RectD(80, 0, 40, 30),
            QueryX = 100,
            ParentControlType = "ToolBar",
            ParentRuntimeIdHash = "strip",
            ParentBounds = new RectD(0, 0, 120, 30),
        };
        foreach (var e in pipeline.Interpreter.OnSnapshot(button, clock.NowMs).Events)
        {
            pipeline.Policy.Evaluate(e, clock.NowMs);
        }

        clock.NowMs = 400;
        Pointer(pipeline, clock.NowMs, 140, 15, 12);
        var beyond = Snapshot(clock.NowMs, "Pane", 400, 30) with
        {
            RuntimeIdHash = "beyond",
            Bounds = new RectD(120, 0, 400, 30),
            QueryX = 140,
        };
        var decisions = pipeline.Interpreter.OnSnapshot(beyond, clock.NowMs).Events
            .Select(e => pipeline.Policy.Evaluate(e, clock.NowMs)).ToArray();
        var intent = Assert.Single(decisions, d => d.Outcome == PolicyOutcome.Emit).Intent!;
        Assert.Equal("Collide", intent.Waveform);
        Assert.Equal("strip-edge", intent.SourceRule);
    }

    [Fact]
    public void ControllerSelectionFollowsLatestInputRatherThanIntentCause()
    {
        // current behavior; lane C/E changes this.
        using var pipeline = Pipeline();
        var mouse = Controller();
        var pad = Controller();
        pipeline.Controllers = new Dictionary<long, CapturedController> { [12] = mouse, [34] = pad };
        Pointer(pipeline, 100, 20, 15, 12);
        Assert.Same(mouse, LatestController(pipeline));
        Pointer(pipeline, 110, 21, 15, 34);
        Assert.Same(pad, LatestController(pipeline));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void LatestPrimedControllerIsFallbackForUnmappedInput(long handle)
    {
        // current behavior; lane C/E changes this.
        using var pipeline = Pipeline();
        var latest = Controller();
        var bank = new ControllerBank();
        typeof(ControllerBank).GetProperty(nameof(ControllerBank.Latest))!.SetValue(bank, latest);
        bank.ApplyTo(pipeline);
        Pointer(pipeline, 100, 20, 15, handle);
        Assert.Same(latest, bank.Latest);
        Assert.Same(latest, LatestController(pipeline));
    }

    [Fact]
    public void OffDoesNotRevokeAnAlreadyAdmittedOutput()
    {
        // current behavior; lane C/E changes this. Infinite expiry isolates authorization from the base's real clock.
        var clock = new FakeClock { NowMs = 100 };
        using var pipeline = Pipeline();
        pipeline.Policy.Config.SemanticIntentExpiryMs = double.PositiveInfinity;
        var decision = pipeline.Policy.Evaluate(pipeline.Interpreter.Cooperative("Align", 0, clock.NowMs), clock.NowMs);
        Assert.Equal(PolicyOutcome.Emit, decision.Outcome);
        var outputs = new List<OutputEvent>();
        pipeline.Event += e => { if (e is OutputEvent output) outputs.Add(output); };
        var queue = new Queue<Action>();
        queue.Enqueue(() => Invoke(pipeline, "Output", decision.Intent!));
        pipeline.Policy.Config.Mode = HapticsMode.Off;
        queue.Dequeue()();

        Assert.Equal("dry-run-route", Assert.Single(outputs).Status);
        Assert.Equal("mode-off", pipeline.Policy.Evaluate(pipeline.Interpreter.Cooperative("Align", 0, 110), 110).Reason);
    }

    private static ObservationPipeline Pipeline()
    {
        var pipeline = new ObservationPipeline(PolicyConfig.Sample(EffectProfile.Focused, 0.8));
        pipeline.Policy.ConsentGiven = true;
        pipeline.Policy.OutputRouteVerified = true;
        return pipeline;
    }

    internal static CapturedController Controller() =>
        (CapturedController)RuntimeHelpers.GetUninitializedObject(typeof(CapturedController));

    internal static void Pointer(ObservationPipeline pipeline, double t, int x, int y, long handle)
    {
        typeof(ObservationPipeline).GetField("_lastDevice", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(pipeline, handle);
        pipeline.Interpreter.OnPointer(new PointerObservation(t, x, y,
            handle == 0 ? InputOrigin.SyntheticOrUnknown : InputOrigin.HardwareRaw, false, 0, handle));
    }

    private static CapturedController? LatestController(ObservationPipeline pipeline) =>
        (CapturedController?)Invoke(pipeline, "ControllerForLatestInput");

    internal static object? Invoke(ObservationPipeline pipeline, string method, params object[] args) =>
        typeof(ObservationPipeline).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pipeline, args);

    internal static ElementSnapshot Snapshot(double t, string type = "Button", int width = 40, int height = 30) => new()
    {
        ProcessId = Environment.ProcessId,
        RuntimeIdHash = "target",
        ControlType = type,
        Bounds = new RectD(0, 0, width, height),
        QueryX = 20,
        QueryY = 15,
        RequestedAtMs = t,
        ReceivedAtMs = t,
    };
}
