using System.Reflection;
using System.Runtime.CompilerServices;
using Haptics.Core;
using Windows.Devices.Haptics;

namespace Haptics.Windows.Tests;

public class ControllerRoutingTests
{
    [Fact]
    public void NonzeroHandleUsesOnlyItsExactMapping()
    {
        var mouse = Controller(HapticDeviceType.Mouse, "mouse");
        var pad = Controller(HapticDeviceType.Touchpad, "pad");
        var controllers = new Dictionary<long, CapturedController> { [12] = mouse, [34] = pad };

        Assert.Same(mouse, ControllerRouting.Resolve(controllers, 12, PointerInputClass.Handle).Controller);
        Assert.Null(ControllerRouting.Resolve(controllers, 99, PointerInputClass.Handle).Controller);
        Assert.Equal("skip:input-class-mismatch",
            ControllerRouting.Resolve(controllers, 12, PointerInputClass.Handleless).Reason);
    }

    [Fact]
    public void HandlelessInputRequiresExactlyOneTouchpad()
    {
        var pad = Controller(HapticDeviceType.Touchpad, "pad");
        var onePad = new Dictionary<long, CapturedController> { [12] = pad };
        var twoPads = new Dictionary<long, CapturedController>
        {
            [12] = pad,
            [34] = Controller(HapticDeviceType.Touchpad, "other-pad"),
        };

        Assert.Same(pad, ControllerRouting.Resolve(onePad, 0, PointerInputClass.Handleless).Controller);
        Assert.Equal("skip:no-touchpad-mapping",
            ControllerRouting.Resolve(new Dictionary<long, CapturedController>(), 0, PointerInputClass.Handleless).Reason);
        Assert.Equal("skip:ambiguous-touchpad", ControllerRouting.Resolve(twoPads, 0, PointerInputClass.Handleless).Reason);
    }

    [Fact]
    public void PromotedInputIsSilentEvenWhenItsHandleIsMapped()
    {
        var mouse = Controller(HapticDeviceType.Mouse, "mouse");
        var route = ControllerRouting.Resolve(
            new Dictionary<long, CapturedController> { [12] = mouse }, 12, PointerInputClass.Promoted);

        Assert.Null(route.Controller);
        Assert.Equal("skip:promoted-input", route.Reason);
    }

    [Theory]
    [InlineData(PointerInputClass.Handle, HapticDeviceType.Mouse, true)]
    [InlineData(PointerInputClass.Handleless, HapticDeviceType.Touchpad, true)]
    [InlineData(PointerInputClass.Handle, HapticDeviceType.Touchpad, false)]
    [InlineData(PointerInputClass.Handleless, HapticDeviceType.Mouse, false)]
    [InlineData(PointerInputClass.Promoted, HapticDeviceType.Mouse, false)]
    public void ManualCaptureRequiresMatchingInputClassAndControllerType(
        PointerInputClass inputClass, HapticDeviceType type, bool expected) =>
        Assert.Equal(expected, ControllerRouting.MatchesInputClass(inputClass, type));

    [Fact]
    public void ManualCaptureRequiresFreshMatchingRawInput()
    {
        var mouse = Controller(HapticDeviceType.Mouse, "mouse");
        var captureCalls = 0;
        var bank = new ControllerBank(() =>
        {
            captureCalls++;
            return mouse;
        });

        Assert.False(bank.TryCapture(12, PointerInputClass.Handle, 0, ControllerBank.ManualCaptureFreshnessMs + 1));
        Assert.Equal(0, captureCalls);
        Assert.False(bank.TryCapture(12, PointerInputClass.Promoted, 100, 100));
        Assert.False(bank.TryCapture(12, PointerInputClass.Handle, 100, 100 + ControllerBank.ManualCaptureFreshnessMs + 1));
        Assert.Equal(0, captureCalls);
        Assert.True(bank.TryCapture(12, PointerInputClass.Handle, 100, 100 + ControllerBank.ManualCaptureFreshnessMs));
        Assert.Equal(1, captureCalls);
        Assert.Same(mouse, bank.Map[12]);
    }

    [Fact]
    public void ManualHandlelessCaptureAcceptsOnlyTouchpad()
    {
        var pad = Controller(HapticDeviceType.Touchpad, "pad");
        var bank = new ControllerBank(() => pad);

        Assert.True(bank.TryCapture(0, PointerInputClass.Handleless, 100, 100));
        Assert.Same(pad, bank.Map[0]);
    }

    [Fact]
    public void HandlelessPrimingCanClassifyAnEnumeratedGenericTouchpad()
    {
        var discovered = Controller(HapticDeviceType.Generic, "same-controller");
        var primed = Controller(HapticDeviceType.Touchpad, "same-controller");
        var bank = new ControllerBank(() => primed);
        bank.AddDiscovered(new ControllerDiscoveryResult(
            new Dictionary<long, CapturedController> { [34] = discovered }, "ready"));

        Assert.True(bank.TryCapture(0, PointerInputClass.Handleless, 100, 100));
        Assert.Same(discovered, bank.Map[34]);
        Assert.Same(primed, ControllerRouting.Resolve(bank.Map, 0, PointerInputClass.Handleless).Controller);
    }

    [Fact]
    public void RemovingEnumeratedTouchpadAlsoRemovesItsHandlelessAliasAndAllowsReprime()
    {
        var discovered = Controller(HapticDeviceType.Generic, "same-touchpad");
        var firstPrime = Controller(HapticDeviceType.Touchpad, "same-touchpad");
        var reconnected = Controller(HapticDeviceType.Touchpad, "same-touchpad");
        var current = firstPrime;
        var bank = new ControllerBank(() => current);
        bank.AddDiscovered(new ControllerDiscoveryResult(
            new Dictionary<long, CapturedController> { [34] = discovered }, "ready"));
        Assert.True(bank.TryCapture(0, PointerInputClass.Handleless, 100, 100));

        Assert.True(bank.RemoveDevice(34));
        Assert.DoesNotContain(0, bank.Map.Keys);

        current = reconnected;
        Assert.True(bank.TryCapture(0, PointerInputClass.Handleless, 200, 200));
        Assert.Same(reconnected, bank.Map[0]);
    }

    [Fact]
    public void RemovingOneSameIdentityHandlePreservesOtherNonzeroHandles()
    {
        var old = Controller(HapticDeviceType.Generic, "mouse");
        var current = Controller(HapticDeviceType.Generic, "mouse");
        var bank = new ControllerBank();
        bank.AddDiscovered(new ControllerDiscoveryResult(
            new Dictionary<long, CapturedController> { [12] = current, [14] = old }, "ready"));
        Assert.True(bank.RemoveDevice(14));
        Assert.Same(current, Assert.Single(bank.Map).Value);
        Assert.True(bank.Any);
    }

    [Fact]
    public void HandlelessControllerLossRemovesItsDiscoveryAliasesButKeepsOtherDevices()
    {
        var pad = Controller(HapticDeviceType.Touchpad, "pad");
        var generic = Controller(HapticDeviceType.Generic, "pad");
        var mouse = Controller(HapticDeviceType.Mouse, "mouse");
        var bank = new ControllerBank(() => pad);
        bank.AddDiscovered(new ControllerDiscoveryResult(
            new Dictionary<long, CapturedController> { [12] = mouse, [34] = generic }, "ready"));
        Assert.True(bank.TryCapture(0, PointerInputClass.Handleless, 100, 100));
        Assert.True(bank.RemoveLostController(0, pad));
        Assert.Equal(12, Assert.Single(bank.Map).Key);
        Assert.Null(bank.Latest);
        Assert.False(bank.RemoveLostController(0, pad));
    }

    [Fact]
    public void ManualCaptureCannotMapOneControllerToAnotherRawHandle()
    {
        var known = Controller(HapticDeviceType.Mouse, "same-controller");
        var bank = new ControllerBank(() => Controller(HapticDeviceType.Mouse, "same-controller"));
        bank.AddDiscovered(new ControllerDiscoveryResult(
            new Dictionary<long, CapturedController> { [34] = known }, "ready"));

        Assert.False(bank.TryCapture(12, PointerInputClass.Handle, 100, 100));
        Assert.DoesNotContain(12, bank.Map.Keys);
    }

    [Fact]
    public void ManagerRouteRequiresAConfidentControllerIdentityMatch()
    {
        var mouse = Controller(HapticDeviceType.Mouse, "mouse");

        Assert.True(HapticsApi.MatchesExpectedController("mouse", mouse));
        Assert.False(HapticsApi.MatchesExpectedController("other", mouse));
        Assert.False(HapticsApi.MatchesExpectedController(null, mouse));
        Assert.False(HapticsApi.MatchesExpectedController("none", Controller(HapticDeviceType.Mouse, "none")));
    }

    [Fact]
    public void ApplyingManualPrimingDoesNotPublishLatestAsAutomaticFallback()
    {
        var mouse = Controller(HapticDeviceType.Mouse, "mouse");
        var bank = new ControllerBank(() => mouse);
        Assert.True(bank.TryCapture(12, PointerInputClass.Handle, 100, 100));
        using var pipeline = new ObservationPipeline(PolicyConfig.Sample(EffectProfile.Focused, 0.8));

        bank.ApplyTo(pipeline);

        Assert.Same(mouse, pipeline.Controllers![12]);
        Assert.Null(pipeline.Controller);
    }

    private static CapturedController Controller(HapticDeviceType type, string id)
    {
        var controller = (CapturedController)RuntimeHelpers.GetUninitializedObject(typeof(CapturedController));
        Set(controller, "<DeviceType>k__BackingField", type);
        Set(controller, "<IdHash>k__BackingField", id);
        return controller;
    }

    private static void Set(CapturedController controller, string field, object value) =>
        typeof(CapturedController).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, value);
}
