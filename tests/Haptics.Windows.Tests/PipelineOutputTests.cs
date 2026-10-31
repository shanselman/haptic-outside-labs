using Haptics.Core;
using Haptics.Tray;
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
    public void QueuedIntentDoesNotSwitchDevicesBeforeOutput()
    {
        using var rig = new Rig();
        var pad = Controller();
        typeof(CapturedController).GetField("<DeviceType>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(pad, global::Windows.Devices.Haptics.HapticDeviceType.Touchpad);
        rig.Pipeline.Controllers = new Dictionary<long, CapturedController> { [12] = rig.Mouse, [34] = pad };
        rig.Observe(100, 12, Snapshot(100));
        Pointer(rig.Pipeline, 105, 21, 15, 34);
        rig.Flush(110);

        Assert.Empty(rig.Sink.Requests);
        Assert.Equal(34, rig.Pipeline.LastDeviceHandle);
        Assert.Contains(rig.Events.OfType<NoteEvent>(), e => e.Note == "skip:stale-device-generation");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void UnknownInputDoesNotReachLatestPrimedFallback(long handle)
    {
        using var rig = new Rig();
        var latest = Controller();
        var bank = new ControllerBank();
        typeof(ControllerBank).GetProperty(nameof(ControllerBank.Latest))!.SetValue(bank, latest);
        bank.ApplyTo(rig.Pipeline);
        rig.Observe(100, handle, Snapshot(100));
        rig.Flush(110);

        Assert.Empty(rig.Sink.Requests);
        Assert.Contains(rig.Events.OfType<NoteEvent>(), e => e.Note == "skip:no-touchpad-mapping" || e.Note == "skip:unmapped-device");
    }

    [Fact]
    public void ExplicitSingleControllerStillWorksForExperimentCallers()
    {
        using var rig = new Rig();
        var single = Controller();
        rig.Pipeline.Controllers = null;
        rig.Pipeline.Controller = single;
        rig.Observe(100, 99, Snapshot(100));
        rig.Flush(110);

        Assert.Same(single, Assert.Single(rig.Sink.Requests).Controller);
    }

    [Fact]
    public void ExplicitExperimentControllerDoesNotFallbackWhenDeviceMapIsActive()
    {
        using var rig = new Rig();
        rig.Pipeline.Controller = Controller();
        rig.Observe(100, 99, Snapshot(100));
        rig.Flush(110);

        Assert.Empty(rig.Sink.Requests);
        Assert.Contains(rig.Events.OfType<NoteEvent>(), e => e.Note == "skip:unmapped-device");
    }

    [Fact]
    public void ExplicitExperimentControllerDoesNotRoutePromotedInput()
    {
        using var rig = new Rig();
        rig.Pipeline.Controller = Controller();
        rig.Observe(100, 12, Snapshot(100), PointerInputClass.Promoted);
        rig.Flush(110);

        Assert.Empty(rig.Sink.Requests);
        Assert.Contains(rig.Events.OfType<NoteEvent>(), e => e.Note == "skip:promoted-input");
    }

    [Fact]
    public void TouchpadOnlyManualMappingReachesTheRecordingSink()
    {
        using var rig = new Rig();
        var touchpad = Controller();
        typeof(CapturedController).GetField("<DeviceType>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(touchpad, global::Windows.Devices.Haptics.HapticDeviceType.Touchpad);
        var bank = new ControllerBank(() => touchpad);
        Assert.True(bank.TryCapture(0, PointerInputClass.Handleless, 100, 100));
        bank.ApplyTo(rig.Pipeline);
        rig.Observe(100, 0, Snapshot(100));
        rig.Flush(110);

        Assert.Same(touchpad, Assert.Single(rig.Sink.Requests).Controller);
    }

    [Fact]
    public void DiscoveryOnlyGenericTouchpadStaysSilentUntilManualPrime()
    {
        using var rig = new Rig();
        var discovered = Controller();
        var touchpad = Controller();
        SetController(discovered, global::Windows.Devices.Haptics.HapticDeviceType.Generic, "touchpad");
        SetController(touchpad, global::Windows.Devices.Haptics.HapticDeviceType.Touchpad, "touchpad");
        var bank = new ControllerBank(() => touchpad);
        bank.AddDiscovered(new ControllerDiscoveryResult(
            new Dictionary<long, CapturedController> { [34] = discovered }, "discovered"));
        bank.ApplyTo(rig.Pipeline);

        rig.Observe(100, 0, Snapshot(100));
        rig.Flush(110);
        Assert.Empty(rig.Sink.Requests);
        Assert.Contains(rig.Events.OfType<NoteEvent>(), e => e.Note == "skip:no-touchpad-mapping");

        Assert.True(bank.TryCapture(0, PointerInputClass.Handleless, 800, 800));
        bank.ApplyTo(rig.Pipeline);
        rig.Observe(800, 0, Snapshot(800) with { RuntimeIdHash = "primed-touchpad", QueryX = 21 });
        rig.Flush(810);

        Assert.Same(touchpad, Assert.Single(rig.Sink.Requests).Controller);
    }

    [Fact]
    public void RemovingOneOfTwoSameControllerHandlesPreservesTheLiveRoute()
    {
        using var rig = new Rig();
        SetController(rig.Mouse, global::Windows.Devices.Haptics.HapticDeviceType.Generic, "same");
        rig.Pipeline.Controllers = new Dictionary<long, CapturedController> { [12] = rig.Mouse, [14] = rig.Mouse };
        Invoke(rig.Pipeline, "OnDeviceChanged", 14L, false);
        rig.Observe(100, 12, Snapshot(100));
        rig.Flush(110);
        Assert.Same(rig.Mouse, Assert.Single(rig.Sink.Requests).Controller);
    }

    [Fact]
    public void LateOldHandleRemovalDoesNotRevokeFreshSameIdentityHandle()
    {
        using var rig = new Rig();
        var old = Controller();
        SetController(old, global::Windows.Devices.Haptics.HapticDeviceType.Generic, "same");
        SetController(rig.Mouse, global::Windows.Devices.Haptics.HapticDeviceType.Generic, "same");
        var bank = new ControllerBank();
        bank.AddDiscovered(new ControllerDiscoveryResult(
            new Dictionary<long, CapturedController> { [12] = old, [14] = rig.Mouse }, "ready"));
        bank.ApplyTo(rig.Pipeline);
        Invoke(rig.Pipeline, "OnDeviceChanged", 12L, false);
        bank.RemoveDevice(12);
        bank.ApplyTo(rig.Pipeline);
        rig.Observe(100, 14, Snapshot(100));
        rig.Flush(110);
        Assert.Same(rig.Mouse, Assert.Single(rig.Sink.Requests).Controller);
    }

    [Theory]
    [InlineData(unchecked((int)0x80010108))]
    [InlineData(unchecked((int)0x80000013))]
    [InlineData(unchecked((int)0x8007048F))]
    public void LostControllerDropsMappingAndFreshRediscoveryRecovers(int hresult)
    {
        using var rig = new Rig();
        rig.Sink.OnSend = request => new HapticAttempt { Route = request.Route, HResult = hresult, Error = "fixture lost" };
        rig.Observe(100, 12, Snapshot(100));
        rig.Flush(110);
        Assert.Empty(rig.Pipeline.Controllers!);
        rig.Sink.OnSend = null;
        var fresh = Controller();
        rig.Pipeline.Controllers = new Dictionary<long, CapturedController> { [12] = fresh };
        rig.Observe(1000, 12, Snapshot(1000) with { QueryX = 21, RuntimeIdHash = "fresh" });
        rig.Flush(1010);
        Assert.Equal(2, rig.Sink.Requests.Count);
        Assert.Same(fresh, rig.Sink.Requests[1].Controller);
    }

    [Theory]
    [InlineData("waveform-not-supported-by-controller", null)]
    [InlineData("controller-mismatch", null)]
    [InlineData("fixture-transient", unchecked((int)0x80004005))]
    [InlineData(null, null)]
    public void UnsupportedOrTransientFailureKeepsTheController(string? error, int? hresult)
    {
        using var rig = new Rig();
        rig.Sink.OnSend = request => new HapticAttempt { Route = request.Route, Error = error, HResult = hresult };
        rig.Observe(100, 12, Snapshot(100));
        rig.Flush(110);
        Assert.Same(rig.Mouse, rig.Pipeline.Controllers![12]);
    }

    [Fact]
    public void RepeatedConnectCyclesPruneTombstonesWithoutRevivingStaleMappings()
    {
        using var rig = new Rig();
        var stale = Controller();
        rig.Pipeline.Controllers = new Dictionary<long, CapturedController> { [12] = stale };
        Invoke(rig.Pipeline, "OnDeviceChanged", 12L, false);
        for (long i = 1; i <= 200; i++)
        {
            rig.Pipeline.Controllers = new Dictionary<long, CapturedController> { [i] = Controller() };
            Invoke(rig.Pipeline, "OnDeviceChanged", i, false);
            rig.Pipeline.Controllers = new Dictionary<long, CapturedController>();
        }

        foreach (var name in new[] { "_removedDevices", "_removedControllers" })
        {
            var value = typeof(ObservationPipeline).GetField(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(rig.Pipeline)!;
            Assert.Equal(0, (int)value.GetType().GetProperty("Count")!.GetValue(value)!);
        }

        rig.Pipeline.Controllers = new Dictionary<long, CapturedController> { [12] = stale };
        rig.Observe(1000, 12, Snapshot(1000));
        rig.Flush(1010);
        Assert.Empty(rig.Sink.Requests);
        Assert.Contains(rig.Events.OfType<NoteEvent>(), e => e.Note == "skip:removed-device");
    }

    [Fact]
    public void LostHandlelessControllerMappedAtNonzeroHandleCannotKeepReady()
    {
        using var rig = new Rig();
        SetController(rig.Mouse, global::Windows.Devices.Haptics.HapticDeviceType.Touchpad, "pad");
        var bank = new ControllerBank();
        bank.AddDiscovered(new ControllerDiscoveryResult(
            new Dictionary<long, CapturedController> { [12] = rig.Mouse }, "ready"));
        bank.ApplyTo(rig.Pipeline);
        rig.Sink.OnSend = request => new HapticAttempt
        {
            Route = request.Route, HResult = unchecked((int)0x80000013),
        };
        rig.Observe(100, 0, Snapshot(100));
        rig.Flush(110);
        Assert.Empty(rig.Pipeline.Controllers!);
        var lost = Assert.Single(rig.Events.OfType<ControllerLostEvent>());
        Assert.True(bank.RemoveLostController(lost.Handle, lost.Controller));
        Assert.False(bank.Any);
    }

    [Fact]
    public void LostOutputChangesTrayReadinessAndOneDiscoveryRestoresIt()
    {
        using var rig = new Rig();
        var bank = new ControllerBank();
        bank.AddDiscovered(new ControllerDiscoveryResult(
            new Dictionary<long, CapturedController> { [12] = rig.Mouse }, "ready"));
        bank.ApplyTo(rig.Pipeline);
        var fresh = Controller();
        var refresh = new ControllerRefresh(() => Task.FromResult(new ControllerDiscoveryResult(
            new Dictionary<long, CapturedController> { [12] = fresh }, "recovered")));
        rig.Sink.OnSend = request => new HapticAttempt
        {
            Route = request.Route, Error = "fixture disconnected", HResult = unchecked((int)0x80010108),
        };
        rig.Observe(100, 12, Snapshot(100));
        rig.Flush(110);
        Assert.True(bank.Any);
        var lost = Assert.Single(rig.Events.OfType<ControllerLostEvent>());
        Assert.True(TrayContext.HandleControllerLoss(bank, refresh, lost));
        Assert.False(bank.Any); // The host's Primed/Ready source.
        Assert.True(refresh.Poll(0, true, bank.Any).Started);
        var result = refresh.Poll(1, true, bank.Any).Result;
        bank.AddDiscovered(Assert.IsType<ControllerDiscoveryResult>(result));
        bank.ApplyTo(rig.Pipeline);
        Assert.True(bank.Any);
        Assert.False(refresh.Poll(2, true, bank.Any).Started);
        Assert.Single(rig.Sink.Requests); // Acquisition/refresh itself never emits a pulse.
        Assert.Equal(0, rig.Sink.Pending);
        Assert.False(TrayContext.HandleControllerLoss(bank, refresh, lost)); // Late loss cannot remove the fresh map.
        rig.Sink.OnSend = null;
        rig.Observe(1000, 12, Snapshot(1000) with { QueryX = 21, RuntimeIdHash = "fresh" });
        rig.Flush(1010);
        Assert.Same(fresh, rig.Sink.Requests.Last().Controller);
    }

    [Fact]
    public void MouseRemovalDoesNotSilenceTheMappedTouchpad()
    {
        using var rig = new Rig();
        var touchpad = Controller();
        typeof(CapturedController).GetField("<DeviceType>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(touchpad, global::Windows.Devices.Haptics.HapticDeviceType.Touchpad);
        rig.Pipeline.Controllers = new Dictionary<long, CapturedController> { [12] = rig.Mouse, [0] = touchpad };
        Invoke(rig.Pipeline, "OnDeviceChanged", 12L, false);

        rig.Observe(100, 0, Snapshot(100));
        rig.Flush(110);

        Assert.Same(touchpad, Assert.Single(rig.Sink.Requests).Controller);
    }

    [Fact]
    public void RemovingTouchpadHandleAlsoRevokesItsHandlelessAlias()
    {
        using var rig = new Rig();
        var discovered = Controller();
        var touchpad = Controller();
        SetController(discovered, global::Windows.Devices.Haptics.HapticDeviceType.Generic, "touchpad");
        SetController(touchpad, global::Windows.Devices.Haptics.HapticDeviceType.Touchpad, "touchpad");
        rig.Pipeline.Controllers = new Dictionary<long, CapturedController> { [0] = touchpad, [34] = discovered };
        Invoke(rig.Pipeline, "OnDeviceChanged", 34L, false);

        rig.Observe(100, 0, Snapshot(100));
        rig.Flush(110);

        Assert.Empty(rig.Sink.Requests);
        Assert.Contains(rig.Events.OfType<NoteEvent>(), e => e.Note == "skip:removed-device");
    }

    [Fact]
    public void HandlelessInputWithTwoMappedTouchpadsStaysSilent()
    {
        using var rig = new Rig();
        var first = Controller();
        var second = Controller();
        foreach (var touchpad in new[] { first, second })
        {
            typeof(CapturedController).GetField("<DeviceType>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(touchpad, global::Windows.Devices.Haptics.HapticDeviceType.Touchpad);
        }

        rig.Pipeline.Controllers = new Dictionary<long, CapturedController> { [0] = first, [34] = second };
        rig.Observe(100, 0, Snapshot(100));
        rig.Flush(110);

        Assert.Empty(rig.Sink.Requests);
        Assert.Contains(rig.Events.OfType<NoteEvent>(), e => e.Note == "skip:ambiguous-touchpad");
    }

    [Fact]
    public void ReusedRawHandleStaysSilentUntilReprimedWithNewController()
    {
        using var rig = new Rig();
        rig.Pipeline.Controllers = new Dictionary<long, CapturedController> { [12] = rig.Mouse };
        Invoke(rig.Pipeline, "OnDeviceChanged", 12L, false);
        rig.Pipeline.Controllers = new Dictionary<long, CapturedController> { [12] = rig.Mouse };

        rig.Observe(100, 12, Snapshot(100));
        rig.Flush(110);
        Assert.Empty(rig.Sink.Requests);
        Assert.Contains(rig.Events.OfType<NoteEvent>(), e => e.Note == "skip:removed-device");

        var reconnected = Controller();
        rig.Pipeline.Controllers = new Dictionary<long, CapturedController> { [12] = reconnected };
        rig.Observe(800, 12, Snapshot(800) with { RuntimeIdHash = "reconnected", QueryX = 21 });
        rig.Flush(810);

        Assert.True(rig.Sink.Requests.Count == 1,
            string.Join(" | ", rig.Events.Select(e => e switch
            {
                DecisionEvent d => $"decision:{d.Decision.Outcome}:{d.Decision.Reason}",
                NoteEvent n => $"note:{n.Note}",
                SnapshotEvent s => $"snapshot:{string.Join(",", s.Notes)}",
                _ => e.GetType().Name,
            })));
        Assert.Same(reconnected, Assert.Single(rig.Sink.Requests).Controller);
    }

    [Fact]
    public void ReconnectedDeviceWithNewHandleStaysSilentUntilMapped()
    {
        using var rig = new Rig();
        rig.Pipeline.Controllers = new Dictionary<long, CapturedController> { [12] = rig.Mouse };
        Invoke(rig.Pipeline, "OnDeviceChanged", 12L, false);

        rig.Observe(100, 34, Snapshot(100));
        rig.Flush(110);
        Assert.Empty(rig.Sink.Requests);
        Assert.Contains(rig.Events.OfType<NoteEvent>(), e => e.Note == "skip:unmapped-device");

        var reconnected = Controller();
        rig.Pipeline.Controllers = new Dictionary<long, CapturedController> { [34] = reconnected };
        rig.Observe(800, 34, Snapshot(800) with { RuntimeIdHash = "new-handle", QueryX = 21 });
        rig.Flush(810);

        Assert.Same(reconnected, Assert.Single(rig.Sink.Requests).Controller);
    }

    [Fact]
    public void ReconnectedControllerWithSameIdentityOnNewHandleCanRoute()
    {
        using var rig = new Rig();
        var removed = Controller();
        SetController(removed, global::Windows.Devices.Haptics.HapticDeviceType.Mouse, "same-controller-id");
        rig.Pipeline.Controllers = new Dictionary<long, CapturedController> { [12] = removed };
        Invoke(rig.Pipeline, "OnDeviceChanged", 12L, false);

        var reconnected = Controller();
        SetController(reconnected, global::Windows.Devices.Haptics.HapticDeviceType.Mouse, "same-controller-id");
        rig.Pipeline.Controllers = new Dictionary<long, CapturedController> { [34] = reconnected };
        rig.Observe(800, 34, Snapshot(800) with { RuntimeIdHash = "new-handle-same-controller", QueryX = 21 });
        rig.Flush(810);

        Assert.Same(reconnected, Assert.Single(rig.Sink.Requests).Controller);
    }

    [Fact]
    public void SameHandleRediscoveryAllowsFreshHandlelessTouchpadReprime()
    {
        using var rig = new Rig();
        var discovered = Controller();
        var initialTouchpad = Controller();
        SetController(discovered, global::Windows.Devices.Haptics.HapticDeviceType.Generic, "touchpad-id");
        SetController(initialTouchpad, global::Windows.Devices.Haptics.HapticDeviceType.Touchpad, "touchpad-id");
        var capture = initialTouchpad;
        var bank = new ControllerBank(() => capture);
        bank.AddDiscovered(new ControllerDiscoveryResult(
            new Dictionary<long, CapturedController> { [34] = discovered }, "initial"));
        Assert.True(bank.TryCapture(0, PointerInputClass.Handleless, 100, 100));
        bank.ApplyTo(rig.Pipeline);

        Invoke(rig.Pipeline, "OnDeviceChanged", 34L, false);
        Assert.True(bank.RemoveDevice(34));
        var rediscovered = Controller();
        SetController(rediscovered, global::Windows.Devices.Haptics.HapticDeviceType.Generic, "touchpad-id");
        bank.AddDiscovered(new ControllerDiscoveryResult(
            new Dictionary<long, CapturedController> { [34] = rediscovered }, "reconnected"));
        bank.ApplyTo(rig.Pipeline);

        var reconnectedTouchpad = Controller();
        SetController(reconnectedTouchpad, global::Windows.Devices.Haptics.HapticDeviceType.Touchpad, "touchpad-id");
        capture = reconnectedTouchpad;
        Assert.True(bank.TryCapture(0, PointerInputClass.Handleless, 800, 800));
        bank.ApplyTo(rig.Pipeline);
        rig.Observe(800, 0, Snapshot(800) with { RuntimeIdHash = "handleless-reconnected", QueryX = 21 });
        rig.Flush(810);

        Assert.Same(reconnectedTouchpad, Assert.Single(rig.Sink.Requests).Controller);
    }

    [Fact]
    public void PromotedInputIsSilentEvenWhenMouseHandleIsMapped()
    {
        using var rig = new Rig();
        rig.Observe(100, 12, Snapshot(100), PointerInputClass.Promoted);
        rig.Flush(110);

        Assert.Empty(rig.Sink.Requests);
        Assert.Contains(rig.Events.OfType<NoteEvent>(), e => e.Note == "skip:promoted-input");
    }

    [Fact]
    public void AutoPrimedTrayMouseStillReachesTheRecordingSink()
    {
        using var rig = new Rig();
        var refresh = new ControllerRefresh(() => Task.FromResult(
            new ControllerDiscoveryResult(
                new Dictionary<long, CapturedController> { [12] = rig.Mouse }, "discovered")));
        var bank = new ControllerBank(() => null);
        refresh.Request();
        Assert.True(refresh.Poll(0, enabled: true, alreadyPrimed: false).Started);
        var result = refresh.Poll(1, enabled: true, alreadyPrimed: false).Result;
        Assert.NotNull(result);
        bank.AddDiscovered(result!);
        bank.ApplyTo(rig.Pipeline);

        rig.Observe(100, 12, Snapshot(100));
        rig.Flush(110);

        Assert.Same(rig.Mouse, Assert.Single(rig.Sink.Requests).Controller);
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
    public void OffWhileOutputIsQueuedNeverReachesSink()
    {
        using var rig = new Rig();
        rig.Observe(100, 12, Snapshot(100));
        rig.Pipeline.Policy.Config.Mode = HapticsMode.Off;
        rig.Flush(110);

        Assert.Empty(rig.Sink.Requests);
        Assert.Contains(rig.Events.OfType<NoteEvent>(), e => e.Note == "skip:output-not-authorized");
        rig.Clock.NowMs = 120;
        rig.Pipeline.SubmitCooperative("Align", 0);
        Assert.Equal(0, rig.Sink.Pending);
        Assert.Equal("mode-off", rig.Events.OfType<DecisionEvent>().Last().Decision.Reason);
    }

    [Theory]
    [InlineData(HapticsMode.ObserveOnly, true, true)]
    [InlineData(HapticsMode.On, false, true)]
    [InlineData(HapticsMode.On, true, false)]
    public void QueuedOutputRechecksCurrentAuthorization(HapticsMode mode, bool consent, bool verified)
    {
        using var rig = new Rig();
        rig.Observe(100, 12, Snapshot(100));
        Assert.Equal(1, rig.Sink.Pending);
        rig.Pipeline.Policy.Config.Mode = mode;
        rig.Pipeline.Policy.ConsentGiven = consent;
        rig.Pipeline.Policy.OutputRouteVerified = verified;
        rig.Flush(110);

        Assert.Empty(rig.Sink.Requests);
        Assert.Contains(rig.Events.OfType<NoteEvent>(), e => e.Note == "skip:output-not-authorized");
        Assert.Equal(1, rig.Pipeline.Stats().Emits);
    }

    [Fact]
    public void DisposeInvalidatesQueuedOutputEvenIfAuthorizationIsRestored()
    {
        using var rig = new Rig();
        rig.Observe(100, 12, Snapshot(100));
        var generation = rig.Pipeline.Interpreter.Generation;
        rig.Pipeline.Dispose();
        Assert.True(rig.Pipeline.Interpreter.Generation > generation);
        rig.Pipeline.Policy.Config.Mode = HapticsMode.On;
        rig.Pipeline.Policy.ConsentGiven = true;
        rig.Flush(110);
        Assert.Empty(rig.Sink.Requests);
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
        Pointer(rig.Pipeline, 100, 20, 15, 12);
        rig.Pipeline.SubmitCooperative("Align", 0);
        Assert.Equal(1, rig.Sink.Pending);
        rig.Flush(110);
        var request = Assert.Single(rig.Sink.Requests);
        Assert.Equal("Align", request.Intent.Waveform);
        Assert.Equal(100, request.Intent.PointerTimestampMs);
        Assert.Equal(100, Assert.Single(rig.Events.OfType<DecisionEvent>()).AtMs);
    }

    [Fact]
    public void CooperativeIntentWithoutFreshPointerOriginStaysSilent()
    {
        using var rig = new Rig();
        rig.Clock.NowMs = 100;
        rig.Pipeline.SubmitCooperative("Align", 0);
        rig.Flush(110);

        Assert.Empty(rig.Sink.Requests);
        Assert.Contains(rig.Events.OfType<NoteEvent>(), e => e.Note == "skip:no-fresh-input-origin");
    }

    [Fact]
    public void CooperativeIntentWithStalePointerOriginStaysSilent()
    {
        using var rig = new Rig();
        Pointer(rig.Pipeline, 100, 20, 15, 12);
        rig.Clock.NowMs = 351;
        rig.Pipeline.SubmitCooperative("Align", 0);
        rig.Flush(360);

        Assert.Empty(rig.Sink.Requests);
        Assert.Contains(rig.Events.OfType<NoteEvent>(), e => e.Note == "skip:no-fresh-input-origin");
    }

    [Fact]
    public void QueuedCooperativeIntentRechecksPointerFreshnessBeforeOutput()
    {
        using var rig = new Rig();
        Pointer(rig.Pipeline, 100, 20, 15, 12);
        rig.Clock.NowMs = 350;
        rig.Pipeline.SubmitCooperative("Align", 0);

        Assert.Equal(1, rig.Sink.Pending);
        rig.Flush(499);

        Assert.Empty(rig.Sink.Requests);
        Assert.Contains(rig.Events.OfType<NoteEvent>(), e => e.Note == "skip:stale-input-origin");
    }

    [Fact]
    public void SemanticEventWithDifferentGenerationIsRejected()
    {
        using var rig = new Rig();
        Pointer(rig.Pipeline, 100, 20, 15, 12);
        var generation = rig.Pipeline.Interpreter.Generation;
        var inputContextType = typeof(ObservationPipeline).GetNestedType(
            "InputRouteContext", System.Reflection.BindingFlags.NonPublic)!;
        var inputContext = inputContextType.GetConstructors(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Single(constructor => constructor.GetParameters().Length == 5)
            .Invoke([12L, PointerInputClass.Handle, Environment.ProcessId, 100d, generation]);
        var staleEvent = rig.Pipeline.Interpreter.Cooperative("Align", Environment.ProcessId, 100) with
        {
            Generation = generation + 1,
        };

        Invoke(rig.Pipeline, "HandleLocked", new InterpretResult([staleEvent], []), 100d, inputContext);
        rig.Flush(110);

        Assert.Empty(rig.Sink.Requests);
        Assert.Contains(rig.Events.OfType<NoteEvent>(), e => e.Note == "skip:stale-device-generation");
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

        public void Observe(
            double t,
            long handle,
            ElementSnapshot snapshot,
            PointerInputClass? inputClass = null)
        {
            Clock.NowMs = t;
            Pointer(Pipeline, t, snapshot.QueryX, snapshot.QueryY, handle, inputClass);
            var generation = Pipeline.Interpreter.Generation;
            Invoke(Pipeline, "OnUia", new UiaResponse { Ok = true, Generation = generation, Snapshot = snapshot with { Generation = generation } }, t, 0.0);
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

    private static void SetController(CapturedController controller, global::Windows.Devices.Haptics.HapticDeviceType type, string idHash)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(CapturedController).GetField("<DeviceType>k__BackingField", flags)!.SetValue(controller, type);
        typeof(CapturedController).GetField("<IdHash>k__BackingField", flags)!.SetValue(controller, idHash);
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
