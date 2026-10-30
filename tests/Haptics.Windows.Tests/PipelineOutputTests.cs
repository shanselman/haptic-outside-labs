using Haptics.Core;
using static Haptics.Windows.Tests.PipelineCharacterizationTests;

namespace Haptics.Windows.Tests;

public class PipelineOutputTests
{
    [Theory]
    [InlineData("Button", 40, 30, "Hover")]
    [InlineData("Button", 44, 80, "Click")]
    [InlineData("TabItem", 80, 30, "Step")]
    public void SingleMouseArrivalReachesRecordingSink(string type, int width, int height, string waveform)
    {
        using var rig = new Rig();
        rig.Observe(100, 12, Snapshot(100, type, width, height));
        Assert.Empty(rig.Sink.Requests);
        Assert.Equal(1, rig.Sink.Pending);
        rig.Flush(110);

        var request = Assert.Single(rig.Sink.Requests);
        Assert.Same(rig.Mouse, request.Controller);
        Assert.Equal(waveform, request.Intent.Waveform);
        Assert.Equal(OutputRoutes.CachedController, request.Route);
        Assert.Equal(request.Intent.Intensity, request.Intensity);
        Assert.Equal(110, Assert.Single(rig.Events.OfType<OutputEvent>()).AtMs);
    }

    [Fact]
    public void SingleMouseStripEdgeReachesRecordingSinkAsCollide()
    {
        using var rig = new Rig();
        rig.Observe(100, 12, Snapshot(100) with
        {
            Bounds = new RectD(80, 0, 40, 30),
            QueryX = 100,
            ParentControlType = "ToolBar",
            ParentRuntimeIdHash = "strip",
            ParentBounds = new RectD(0, 0, 120, 30),
        });
        rig.Flush(100);
        rig.Observe(400, 12, Snapshot(400, "Pane", 400, 30) with
        {
            RuntimeIdHash = "beyond",
            Bounds = new RectD(120, 0, 400, 30),
            QueryX = 140,
        });
        rig.Flush(400);

        Assert.Equal(["Step", "Collide"], rig.Sink.Requests.Select(r => r.Intent.Waveform));
        Assert.All(rig.Sink.Requests, r => Assert.Same(rig.Mouse, r.Controller));
    }

    [Fact]
    public void QueuedMouseIntentIsSentToDeviceOfLatestInput()
    {
        // current behavior; lane C/E changes this.
        using var rig = new Rig();
        var pad = Controller();
        rig.Pipeline.Controllers = new Dictionary<long, CapturedController> { [12] = rig.Mouse, [34] = pad };
        rig.Observe(100, 12, Snapshot(100));
        Pointer(rig.Pipeline, 105, 21, 15, 34);
        rig.Flush(110);

        var request = Assert.Single(rig.Sink.Requests);
        Assert.Equal(100, request.Intent.PointerTimestampMs);
        Assert.Equal(34, rig.Pipeline.LastDeviceHandle);
        Assert.Same(pad, request.Controller);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void UnknownInputReachesLatestPrimedFallback(long handle)
    {
        // current behavior; lane C/E changes this.
        using var rig = new Rig();
        var latest = Controller();
        var bank = new ControllerBank();
        typeof(ControllerBank).GetProperty(nameof(ControllerBank.Latest))!.SetValue(bank, latest);
        bank.ApplyTo(rig.Pipeline);
        rig.Observe(100, handle, Snapshot(100));
        rig.Flush(110);

        Assert.Same(latest, Assert.Single(rig.Sink.Requests).Controller);
    }

    [Fact]
    public void ExactDeviceMappingWinsOverLatestFallback()
    {
        using var rig = new Rig();
        rig.Pipeline.Controller = Controller();
        rig.Observe(100, 12, Snapshot(100));
        rig.Flush(110);
        Assert.Same(rig.Mouse, Assert.Single(rig.Sink.Requests).Controller);
    }

    [Fact]
    public void OffWhileOutputIsQueuedStillReachesSink()
    {
        // current behavior; lane C/E changes this.
        using var rig = new Rig();
        rig.Observe(100, 12, Snapshot(100));
        rig.Pipeline.Policy.Config.Mode = HapticsMode.Off;
        rig.Flush(110);

        Assert.Equal("Hover", Assert.Single(rig.Sink.Requests).Intent.Waveform);
        rig.Clock.NowMs = 120;
        rig.Pipeline.SubmitCooperative("Align", 0);
        Assert.Equal(0, rig.Sink.Pending);
        Assert.Equal("mode-off", rig.Events.OfType<DecisionEvent>().Last().Decision.Reason);
    }

    [Theory]
    [InlineData(250, true)]
    [InlineData(251, false)]
    public void DeadlineUsesInjectedClockIncludingExactBoundary(double outputAt, bool sent)
    {
        using var rig = new Rig();
        rig.Observe(100, 12, Snapshot(100));
        rig.Flush(outputAt);

        Assert.Equal(sent ? 1 : 0, rig.Sink.Requests.Count);
        Assert.Equal(sent ? 0 : 1, rig.Pipeline.Stats().Expired);
        var output = Assert.Single(rig.Events.OfType<OutputEvent>());
        Assert.Equal(outputAt, output.AtMs);
        Assert.Equal(sent ? "dry-run-route" : "expired-before-output", output.Status);
    }

    [Fact]
    public void DwellUsesInjectedClockWithoutWaitingForATimer()
    {
        using var rig = new Rig();
        Pointer(rig.Pipeline, 0, 0, 15, 12);
        rig.Observe(10, 12, Snapshot(10) with { Bounds = new RectD(80, 0, 40, 30), QueryX = 100 });
        Assert.True(rig.Pipeline.Interpreter.HasPendingArrival);
        rig.Tick(89);
        Assert.Equal(0, rig.Sink.Pending);
        rig.Tick(90);
        Assert.False(rig.Pipeline.Interpreter.HasPendingArrival);
        Assert.Equal(1, rig.Sink.Pending);
        rig.Flush(90);

        var request = Assert.Single(rig.Sink.Requests);
        Assert.Equal(90, request.Intent.PointerTimestampMs);
        Assert.Equal("Hover", request.Intent.Waveform);
        Assert.Equal(90, Assert.Single(rig.Events.OfType<DecisionEvent>()).Decision.DecidedAtMs);
    }

    [Theory]
    [InlineData(HapticsMode.Off, true, true)]
    [InlineData(HapticsMode.On, false, true)]
    [InlineData(HapticsMode.On, true, false)]
    [InlineData(HapticsMode.ObserveOnly, true, true)]
    public void SinkCannotReceiveIntentWithoutPolicyAdmission(HapticsMode mode, bool consent, bool verified)
    {
        using var rig = new Rig();
        rig.Pipeline.Policy.Config.Mode = mode;
        rig.Pipeline.Policy.ConsentGiven = consent;
        rig.Pipeline.Policy.OutputRouteVerified = verified;
        rig.Observe(100, 12, Snapshot(100));
        Assert.Equal(0, rig.Sink.Pending);
        rig.Flush(110);
        Assert.Empty(rig.Sink.Requests);
        Assert.NotEqual(PolicyOutcome.Emit, Assert.Single(rig.Events.OfType<DecisionEvent>()).Decision.Outcome);
    }

    [Fact]
    public void CooperativeIntentUsesSameClockPolicyQueueAndSink()
    {
        using var rig = new Rig();
        rig.Clock.NowMs = 100;
        rig.Pipeline.SubmitCooperative("Align", 0);
        Assert.Equal(1, rig.Sink.Pending);
        rig.Flush(110);
        var request = Assert.Single(rig.Sink.Requests);
        Assert.Equal("Align", request.Intent.Waveform);
        Assert.Equal(100, request.Intent.PointerTimestampMs);
        Assert.Equal(100, Assert.Single(rig.Events.OfType<DecisionEvent>()).AtMs);
    }

    [Theory]
    [InlineData(true, "api-accepted")]
    [InlineData(false, "api-rejected:fixture-rejection")]
    public void SinkAttemptsKeepExistingAccounting(bool accepted, string status)
    {
        using var rig = new Rig();
        rig.Sink.OnSend = request => new HapticAttempt
        {
            Route = request.Route,
            Accepted = accepted,
            Error = accepted ? null : "fixture-rejection",
            InputToCallMs = 10,
        };
        rig.Observe(100, 12, Snapshot(100));
        rig.Flush(110);

        var stats = rig.Pipeline.Stats();
        Assert.Equal(1, stats.Emits);
        Assert.Equal(accepted ? 1 : 0, stats.ApiAccepted);
        Assert.Equal(accepted ? 0 : 1, stats.ApiRejected);
        Assert.Equal(10, stats.InputToCallP50);
        Assert.Equal(status, Assert.Single(rig.Events.OfType<OutputEvent>()).Status);
    }

    [Theory]
    [InlineData(1.0, 0.68)]
    [InlineData(1.25, 0.85)]
    [InlineData(2.0, 1.0)]
    public void TouchpadBoostIsAppliedBeforeSinkAndClamped(double boost, double expected)
    {
        using var rig = new Rig();
        typeof(CapturedController).GetField("<DeviceType>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(rig.Mouse, global::Windows.Devices.Haptics.HapticDeviceType.Touchpad);
        rig.Pipeline.TouchpadBoost = boost;
        rig.Observe(100, 12, Snapshot(100));
        rig.Flush(110);
        Assert.Equal(expected, Assert.Single(rig.Sink.Requests).Intensity, precision: 10);
    }

    [Fact]
    public void DefaultOptionsKeepMonotonicClockAndNativeOutput()
    {
        var options = new PipelineOptions();
        Assert.Same(MonotonicClock.Instance, options.Clock);
        Assert.Null(options.OutputSink);
    }

    private sealed class Rig : IDisposable
    {
        public FakeClock Clock { get; } = new();
        public RecordingPipelineOutputSink Sink { get; } = new();
        public CapturedController Mouse { get; } = Controller();
        public ObservationPipeline Pipeline { get; }
        public List<PipelineEvent> Events { get; } = [];

        public Rig()
        {
            Pipeline = new ObservationPipeline(PolicyConfig.Sample(EffectProfile.Focused, 0.8), new PipelineOptions
            {
                Clock = Clock,
                OutputSink = Sink,
                Route = OutputRoutes.CachedController,
            });
            Pipeline.Policy.ConsentGiven = true;
            Pipeline.Policy.OutputRouteVerified = true;
            Pipeline.Controllers = new Dictionary<long, CapturedController> { [12] = Mouse };
            Pipeline.Event += Events.Add;
        }

        public void Observe(double t, long handle, ElementSnapshot snapshot)
        {
            Clock.NowMs = t;
            Pointer(Pipeline, t, snapshot.QueryX, snapshot.QueryY, handle);
            Invoke(Pipeline, "OnUia", new UiaResponse { Ok = true, Snapshot = snapshot }, t, 0.0);
        }

        public void Tick(double t)
        {
            Clock.NowMs = t;
            Invoke(Pipeline, "Tick");
        }

        public void Flush(double t)
        {
            Clock.NowMs = t;
            Sink.Flush();
        }

        public void Dispose() => Pipeline.Dispose();
    }
}

internal sealed class RecordingPipelineOutputSink : IPipelineOutputSink
{
    private readonly Queue<Action> _queue = new();
    public List<PipelineOutputRequest> Requests { get; } = [];
    public Func<PipelineOutputRequest, HapticAttempt?>? OnSend { get; set; }
    public int Pending => _queue.Count;
    public void Post(Action output) => _queue.Enqueue(output);

    public HapticAttempt? Send(PipelineOutputRequest request)
    {
        Requests.Add(request);
        return OnSend?.Invoke(request);
    }

    public void Flush()
    {
        while (_queue.TryDequeue(out var output))
        {
            output();
        }
    }
}
