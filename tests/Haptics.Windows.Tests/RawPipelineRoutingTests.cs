using Haptics.Core;
using static Haptics.Windows.Tests.PipelineCharacterizationTests;

namespace Haptics.Windows.Tests;

/// <summary>Drive the raw callback and its bounded request-route table, not ObservePointer's shortcut.</summary>
public class RawPipelineRoutingTests
{
    [Fact]
    public void LatestRawRouteAlternatesMouseAndTouchpadWithoutReroutingQueuedOutput()
    {
        using var rig = new RawRig();
        for (var i = 0; i < 6; i++)
        {
            var handle = i % 2 == 0 ? 12L : 0L;
            rig.X = 20 + i * 60;
            rig.Raw(100 + i * 900, handle);
            rig.Respond(rig.Clock.NowMs, rig.Generation, $"target-{i}");
            rig.Sink.Flush();
            Assert.Equal(i + 1, rig.Sink.Requests.Count);
            Assert.Same(handle == 0 ? rig.Pad : rig.Mouse, rig.Sink.Requests.Last().Controller);
        }

        Assert.Equal(6, rig.Sink.Requests.Count);
    }

    [Fact]
    public void StaleRawResponseAfterSwitchIsDroppedAndLatestResponseRoutes()
    {
        using var rig = new RawRig();
        rig.Raw(100, 12);
        var old = rig.Generation;
        rig.Raw(105, 0);
        rig.Respond(100, old, "old");
        rig.Sink.Flush();
        Assert.Empty(rig.Sink.Requests);
        Assert.Contains(rig.Events.OfType<NoteEvent>(), e => e.Note == "skip:stale-device-generation");

        rig.Respond(105, rig.Generation, "latest");
        rig.Sink.Flush();
        Assert.Same(rig.Pad, Assert.Single(rig.Sink.Requests).Controller);
    }

    [Fact]
    public void Over64RawRequestsDropTrimmedResponseButRetainLatest()
    {
        using var rig = new RawRig();
        for (var i = 0; i < 200; i++)
        {
            rig.Raw(100 + i, 12, RawMouseButtons.LeftUp);
        }

        rig.Respond(100, rig.Generation, "trimmed");
        rig.Sink.Flush();
        Assert.Empty(rig.Sink.Requests);
        Assert.Contains(rig.Events.OfType<NoteEvent>(), e => e.Note == "skip:unknown-input-origin");
        rig.Respond(299, rig.Generation, "latest");
        rig.Sink.Flush();
        Assert.Same(rig.Mouse, Assert.Single(rig.Sink.Requests).Controller);
    }

    [Fact]
    public void ButtonWithoutMovementStoresItsOwnRequestRoute()
    {
        using var rig = new RawRig();
        rig.Raw(100, 12);
        rig.Raw(150, 12, RawMouseButtons.LeftUp);
        rig.Raw(160, 12); // No request: the response must use the stored route, not the latest-input shortcut.
        rig.Respond(150, rig.Generation, "button");
        rig.Sink.Flush();
        Assert.Same(rig.Mouse, Assert.Single(rig.Sink.Requests).Controller);
        Assert.DoesNotContain(rig.Events.OfType<NoteEvent>(), e => e.Note == "skip:unknown-input-origin");
    }

    [Fact]
    public void RawHandleReuseCannotReviveOldQueuedWorkButFreshMappingRoutes()
    {
        using var rig = new RawRig();
        rig.Raw(100, 12);
        var old = rig.Generation;
        rig.Respond(100, old, "before-removal");
        Invoke(rig.Pipeline, "OnDeviceChanged", 12L, false);
        rig.Sink.Flush();
        Assert.Empty(rig.Sink.Requests);
        rig.Raw(1000, 12);
        rig.Respond(1000, rig.Generation, "removed");
        rig.Sink.Flush();
        Assert.Empty(rig.Sink.Requests);

        var fresh = Controller();
        rig.Pipeline.Controllers = new Dictionary<long, CapturedController> { [12] = fresh, [0] = rig.Pad };
        rig.X = 80;
        rig.Raw(2000, 12, RawMouseButtons.LeftUp);
        rig.Respond(2000, rig.Generation, "reconnected");
        rig.Sink.Flush();
        Assert.Same(fresh, Assert.Single(rig.Sink.Requests).Controller);
    }

    private sealed class RawRig : IDisposable
    {
        public FakeClock Clock { get; } = new();
        public RecordingPipelineOutputSink Sink { get; } = new();
        public CapturedController Mouse { get; } = Controller();
        public CapturedController Pad { get; } = Controller();
        public ObservationPipeline Pipeline { get; }
        public List<PipelineEvent> Events { get; } = [];
        public long Generation => Pipeline.Interpreter.Generation;
        public int X { get; set; } = 20;

        public RawRig()
        {
            typeof(CapturedController).GetField("<DeviceType>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(Pad, global::Windows.Devices.Haptics.HapticDeviceType.Touchpad);
            Pipeline = new ObservationPipeline(new Haptics.Tray.TraySettings().ToPolicy(), new PipelineOptions
            {
                Clock = Clock,
                OutputSink = Sink,
                Route = OutputRoutes.CachedController,
                PointerPosition = () => (X, 15),
                ForegroundPid = () => Environment.ProcessId,
            });
            Pipeline.Policy.ConsentGiven = true;
            Pipeline.Policy.OutputRouteVerified = true;
            Pipeline.Controllers = new Dictionary<long, CapturedController> { [12] = Mouse, [0] = Pad };
            Pipeline.Event += Events.Add;
        }

        public void Raw(double t, long handle, RawMouseButtons buttons = RawMouseButtons.None)
        {
            Clock.NowMs = t;
            Invoke(Pipeline, "OnRaw", new RawMouseInput(t, RawInputCode.Background, handle, 0, buttons, 0, 0, 0, 0));
        }

        public void Respond(double t, long generation, string id) =>
            Invoke(Pipeline, "OnUia", new UiaResponse
            {
                Ok = true,
                Generation = generation,
                Snapshot = Snapshot(t) with
                {
                    Generation = generation, RuntimeIdHash = id, QueryX = X,
                    Bounds = new RectD(X - 20, 0, 40, 30),
                },
            }, t, 0d);

        public void Dispose() => Pipeline.Dispose();
    }
}
