using Haptics.Core;
using static Haptics.Windows.Tests.PipelineCharacterizationTests;

namespace Haptics.Windows.Tests;

/// <summary>Drive the raw callback and its bounded request-route table, not ObservePointer's shortcut.</summary>
public class RawPipelineRoutingTests
{
    [Theory]
    [InlineData(EffectProfile.Focused, 12)]
    [InlineData(EffectProfile.Focused, 0)]
    [InlineData(EffectProfile.Everything, 12)]
    [InlineData(EffectProfile.Everything, 0)]
    public void SliderThumbMetadataPreservesRawRoutingAndDragRangeHistory(EffectProfile style, long handle)
    {
        using var rig = new RawRig(useDwell: true, style: style);
        ElementSnapshot Slider(double t, int x, RectD? thumb, double value) => Snapshot(t, "Slider") with
        {
            Generation = rig.Generation, RuntimeIdHash = "slider", Bounds = new RectD(0, 0, 200, 30),
            QueryX = x, SliderThumbBounds = thumb, Range = new RangeInfo(value, 0, 100, 1, 10),
            ParentControlType = null, ParentRuntimeIdHash = null,
        };
        rig.Raw(0, handle);
        rig.RespondSnapshot(Slider(0, 20, null, 50), 2);
        rig.Tick(50);
        Assert.Equal(0, rig.Sink.Pending);
        rig.X = 100;
        rig.Raw(100, handle);
        rig.RespondSnapshot(Slider(100, 100, new RectD(90, 5, 20, 20), 50), 102);
        rig.Tick(150);
        rig.Sink.Flush();
        rig.Raw(300, handle, RawMouseButtons.LeftDown);
        rig.RespondSnapshot(Slider(300, 100, new RectD(90, 5, 20, 20), 60), 302);
        rig.Sink.Flush();
        rig.Raw(400, handle, RawMouseButtons.LeftUp);
        rig.RespondSnapshot(Slider(400, 100, new RectD(90, 5, 20, 20), 60), 402);
        rig.Tick(450);
        rig.Sink.Flush();
        Assert.Equal(2, rig.Sink.Requests.Count);
        rig.X = 190;
        rig.Raw(700, handle, RawMouseButtons.LeftDown);
        rig.RespondSnapshot(Slider(700, 190, new RectD(180, 5, 20, 20), 100), 702);
        rig.Sink.Flush();
        Assert.Equal(["Hover", "Step", "Collide"], rig.Sink.Requests.Select(r => r.Intent.Waveform));
        Assert.All(rig.Sink.Requests, r => Assert.Same(handle == 0 ? rig.Pad : rig.Mouse, r.Controller));
    }

    [Theory]
    [InlineData(EffectProfile.Focused, 12)]
    [InlineData(EffectProfile.Focused, 0)]
    [InlineData(EffectProfile.Everything, 12)]
    [InlineData(EffectProfile.Everything, 0)]
    public void RepeatedCheckboxChangesProduceOnePulseAndActivityDecisionPerClick(EffectProfile style, long handle)
    {
        using var rig = new RawRig(useDwell: true, style: style);
        rig.Raw(0, handle);
        rig.Respond(0, rig.Generation, "checkbox", toggleOn: false);
        var state = false;
        for (var i = 0; i < 6; i++)
        {
            var at = 100 + i * 500;
            if (i > 0)
            {
                rig.Tick(at - 50);
                rig.Respond(at - 50, rig.Generation, "checkbox", toggleOn: state);
            }
            rig.Raw(at, handle, RawMouseButtons.LeftDown);
            rig.Raw(at + 20, handle, RawMouseButtons.LeftUp);
            rig.Respond(at + 20, rig.Generation, "checkbox", toggleOn: state);
            state = !state;
            rig.Tick(at + 45);
            rig.Respond(at + 45, rig.Generation, "checkbox", toggleOn: state);
            rig.Sink.Flush();
            Assert.Equal(i + 1, rig.Sink.Requests.Count);
            var decisions = rig.Events.OfType<DecisionEvent>()
                .Where(e => e.Decision.Event.Kind == SemanticEventKind.ToggleChanged).ToArray();
            Assert.Equal(i + 1, decisions.Length);
            Assert.Equal(PolicyOutcome.Emit, decisions[^1].Decision.Outcome);
            Assert.Contains(state ? "turned on" : "turned off",
                Haptics.Lab.ShowcaseWindow.Friendly(decisions[^1].Decision));
        }
        Assert.All(rig.Sink.Requests, request =>
        {
            Assert.Equal("Step", request.Intent.Waveform);
            Assert.Same(handle == 0 ? rig.Pad : rig.Mouse, request.Controller);
        });
    }

    [Theory]
    [InlineData(12)]
    [InlineData(0)]
    public void DelayedCrossedTabReplyCannotSuppressTheStoppedTabThroughTheRawPipeline(long handle)
    {
        using var rig = new RawRig(useDwell: true);
        ElementSnapshot Tab(double t, int x, string id, int left, int width) => Snapshot(t, "TabItem") with
        {
            Generation = rig.Generation, RuntimeIdHash = id, QueryX = x,
            Bounds = new RectD(left, 0, width, 30), IsSelected = false,
        };
        rig.Raw(0, handle);
        rig.RespondSnapshot(Tab(0, 20, "first", 0, 40), 1);
        rig.X = 90;
        rig.Raw(10, handle);
        rig.X = 110;
        rig.Raw(20, handle);
        rig.RespondSnapshot(Tab(10, 90, "crossed", 40, 60), 30);
        var destination = Tab(20, 110, "destination", 100, 100);
        rig.RespondSnapshot(destination, 40);
        rig.Tick(69);
        Assert.Equal(0, rig.Sink.Pending);
        rig.Tick(70);
        Assert.Equal(1, rig.Sink.Pending);
        rig.Sink.Flush();
        var request = Assert.Single(rig.Sink.Requests);
        Assert.Equal("Hover", request.Intent.Waveform);
        Assert.Equal(destination.ElementKey, request.Intent.ElementKey);
        Assert.Equal(70, request.Intent.PointerTimestampMs);
        Assert.Same(handle == 0 ? rig.Pad : rig.Mouse, request.Controller);
        Assert.DoesNotContain(rig.Events.OfType<NoteEvent>(), e => e.Note == "skip:unknown-input-origin");
    }

    [Theory]
    [InlineData(12, false)]
    [InlineData(0, false)]
    [InlineData(12, true)]
    [InlineData(0, true)]
    public void TogglePollRetainsTheClickDeviceAndDoesNotRefreshRawInputAge(long handle, bool before)
    {
        using var rig = new RawRig();
        rig.ClickToggle(handle, before);
        rig.Tick(145);
        rig.Respond(145, rig.Generation, "toggle", toggleOn: !before);
        Assert.Equal(120, rig.Pipeline.LastInputTimestampMs);
        Assert.Empty(rig.Sink.Requests);
        rig.Sink.Flush();

        var request = Assert.Single(rig.Sink.Requests);
        Assert.Same(handle == 0 ? rig.Pad : rig.Mouse, request.Controller);
        Assert.Equal("Step", request.Intent.Waveform);
        Assert.Equal(120, request.Intent.PointerTimestampMs);
        rig.Tick(245);
        rig.Respond(245, rig.Generation, "toggle", toggleOn: !before);
        rig.Sink.Flush();
        Assert.Single(rig.Sink.Requests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TogglePollCannotSurviveADeviceSwitchBeforeResponseOrOutput(bool switchAfterResponse, bool before)
    {
        using var rig = new RawRig();
        rig.ClickToggle(12, before);
        var generation = rig.Generation;
        rig.Tick(145);
        if (switchAfterResponse)
        {
            rig.Respond(145, generation, "toggle", toggleOn: !before);
            Assert.Equal(1, rig.Sink.Pending);
        }

        rig.Raw(150, 0);
        if (!switchAfterResponse)
        {
            rig.Respond(145, generation, "toggle", toggleOn: !before);
        }

        rig.Sink.Flush();
        Assert.Empty(rig.Sink.Requests);
        Assert.Contains(rig.Events.OfType<NoteEvent>(), e => e.Note == "skip:stale-device-generation");
    }

    [Fact]
    public void TogglePollDoesNotRequestWhenThePointerHasDeparted()
    {
        using var rig = new RawRig();
        rig.ClickToggle(12);
        rig.X = 200;
        rig.Tick(145);
        rig.Respond(145, rig.Generation, "toggle", toggleOn: true);
        rig.Sink.Flush();
        Assert.Empty(rig.Sink.Requests);
        Assert.Contains(rig.Events.OfType<NoteEvent>(), e => e.Note == "skip:unknown-input-origin");
    }

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

        public RawRig(bool useDwell = false, EffectProfile style = EffectProfile.Focused)
        {
            typeof(CapturedController).GetField("<DeviceType>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(Pad, global::Windows.Devices.Haptics.HapticDeviceType.Touchpad);
            Pipeline = new ObservationPipeline(new Haptics.Tray.TraySettings { Style = style }.ToPolicy(), new PipelineOptions
            {
                Clock = Clock,
                OutputSink = Sink,
                Route = OutputRoutes.CachedController,
                PointerPosition = () => (X, 15),
                ForegroundPid = () => Environment.ProcessId,
            });
            Pipeline.Policy.ConsentGiven = true;
            if (!useDwell)
            {
                Pipeline.Interpreter.Config.LandingDwellMs = 0; // Isolate raw-device routing from dwell timing.
                Pipeline.Interpreter.Config.SmallTargetDwellMs = 0;
            }
            Pipeline.Policy.OutputRouteVerified = true;
            Pipeline.Controllers = new Dictionary<long, CapturedController> { [12] = Mouse, [0] = Pad };
            Pipeline.Event += Events.Add;
        }

        public void Raw(double t, long handle, RawMouseButtons buttons = RawMouseButtons.None)
        {
            Clock.NowMs = t;
            Invoke(Pipeline, "OnRaw", new RawMouseInput(t, RawInputCode.Background, handle, 0, buttons, 0, 0, 0, 0));
        }

        public void ClickToggle(long handle, bool before = false)
        {
            Raw(100, handle);
            Respond(100, Generation, "toggle", toggleOn: before);
            Raw(110, handle, RawMouseButtons.LeftDown);
            Raw(120, handle, RawMouseButtons.LeftUp);
            Respond(120, Generation, "toggle", toggleOn: before);
        }

        public void Tick(double t)
        {
            Clock.NowMs = t;
            Invoke(Pipeline, "Tick");
        }

        public void RespondSnapshot(ElementSnapshot snapshot, double at)
        {
            Clock.NowMs = at;
            Invoke(Pipeline, "OnUia", new UiaResponse
            {
                Ok = true, Generation = snapshot.Generation, Snapshot = snapshot,
            }, snapshot.RequestedAtMs, at - snapshot.RequestedAtMs);
        }

        public void Respond(double t, long generation, string id, bool? toggleOn = null) =>
            Invoke(Pipeline, "OnUia", new UiaResponse
            {
                Ok = true,
                Generation = generation,
                Snapshot = Snapshot(t) with
                {
                    Generation = generation, RuntimeIdHash = id, QueryX = X,
                    Bounds = new RectD(X - 20, 0, 40, 30),
                    ControlType = toggleOn.HasValue ? "CheckBox" : "ComboBox",
                    IsToggle = toggleOn.HasValue, IsToggledOn = toggleOn,
                },
            }, t, 0d);

        public void Dispose() => Pipeline.Dispose();
    }
}
