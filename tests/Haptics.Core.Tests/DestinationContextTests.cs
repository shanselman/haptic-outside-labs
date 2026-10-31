using Haptics.Core;

namespace Haptics.Core.Tests;

public class DestinationContextTests
{
    private static PointerObservation Pointer(double t, int x) => new(t, x, 15, InputOrigin.Replay, false, 100);

    private static ElementSnapshot Element(double requested, string type, string id, int x, int process = 100) => new()
    {
        RequestedAtMs = requested, ProcessId = process, ControlType = type, RuntimeIdHash = id,
        QueryX = x, QueryY = 15, Bounds = new RectD(x - 20, 0, 40, 30),
    };

    private static HapticPolicy Policy() => new(new PolicyConfig
    {
        Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions, MinimumGlobalSpacingMs = 0,
    }, pid => pid switch { 100 => "notepad", 200 => "KeePass", 400 => "paint", _ => null })
    {
        ConsentGiven = true, OutputRouteVerified = true,
    };

    [Theory]
    [InlineData("password")]
    [InlineData("disabled")]
    [InlineData("offscreen")]
    [InlineData("excluded")]
    [InlineData("unknown")]
    [InlineData("unknown-process")]
    public void StripEdgeChecksTheDestinationInsteadOfOnlyTheOldButton(string context)
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(Pointer(0, 100));
        interpreter.OnSnapshot(Element(0, "Button", "last", 100) with
        {
            ParentControlType = "ToolBar", ParentRuntimeIdHash = "strip", ParentBounds = new RectD(0, 0, 120, 30),
        }, 1);
        interpreter.OnPointer(Pointer(300, 140));
        var destination = Element(300, context == "unknown" ? "Unknown" : "Pane", "destination", 140,
            context == "excluded" ? 200 : context == "unknown-process" ? 300 : 100) with
        {
            IsPassword = context == "password", IsEnabled = context != "disabled", IsOffscreen = context == "offscreen",
        };
        var edge = Assert.Single(interpreter.OnSnapshot(destination, 301).Events,
            e => e.Kind == SemanticEventKind.GroupEdge);

        Assert.Equal(PolicyOutcome.Suppressed, Policy().Evaluate(edge, 301).Outcome);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("disabled")]
    [InlineData("offscreen")]
    [InlineData("excluded")]
    public void CooperativeEventsRetainNonInteractiveCurrentContext(string context)
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(Pointer(0, 20));
        var snapshot = Element(0, "Edit", "field", 20, context == "excluded" ? 200 : 100) with
        {
            IsPassword = context == "password", IsEnabled = context != "disabled", IsOffscreen = context == "offscreen",
        };
        interpreter.OnSnapshot(snapshot, 1);
        var cooperative = interpreter.Cooperative("Align", 100, 300);
        Assert.Same(snapshot, cooperative.Element);
        Assert.Equal(PolicyOutcome.Suppressed, Policy().Evaluate(cooperative, 300).Outcome);
    }

    [Fact]
    public void EverythingDensityOffOverridesPerAppThrottles()
    {
        var config = new PolicyConfig { Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions };
        config.ApplyProfile(EffectProfile.Everything);
        var policy = new HapticPolicy(config, _ => "chrome") { ConsentGiven = true, OutputRouteVerified = true };
        for (var i = 0; i < 12; i++)
        {
            var t = i * 100;
            var element = Element(t, "Button", $"button{i}", i * 40);
            var e = new SemanticEvent(SemanticEventKind.EnterControl, i, t, t, 100, "Button", element.ElementKey,
                null, 1, [], false, 0, element);
            Assert.Equal(PolicyOutcome.Emit, policy.Evaluate(e, t).Outcome);
        }
    }

    [Fact]
    public void StripEdgeWithoutDestinationContextFailsClosed()
    {
        var source = Element(0, "Button", "last", 100);
        var edge = new SemanticEvent(SemanticEventKind.GroupEdge, 1, 0, 0, 100, "Button", source.ElementKey,
            "strip", 1, [], false, 0, source);
        Assert.Equal(PolicyOutcome.Suppressed, Policy().Evaluate(edge, 0).Outcome);
    }

    [Fact]
    public void CooperativeEventsRetainLookupFailureContext()
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(Pointer(0, 20));
        interpreter.OnSnapshot(Element(0, "Button", "button", 20), 1);
        interpreter.OnSnapshot(new ElementSnapshot { Error = "timeout", RequestedAtMs = 20, QueryX = 20, QueryY = 15 }, 21);
        var cooperative = interpreter.Cooperative("Align", 100, 300);
        Assert.Equal("timeout", cooperative.Element?.Error);
        Assert.Equal(PolicyOutcome.Suppressed, Policy().Evaluate(cooperative, 300).Outcome);
    }

    [Fact]
    public void SafeNonInteractiveContextStillAllowsCooperativeAlignment()
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(Pointer(0, 20));
        var snapshot = Element(0, "Pane", "canvas", 20, 400);
        interpreter.OnSnapshot(snapshot, 1);
        var cooperative = interpreter.Cooperative("Align", 100, 300);
        Assert.Equal(PolicyOutcome.Emit, Policy().Evaluate(cooperative, 300).Outcome);
    }

    [Fact]
    public void SafeStripExitRetainsSourceMeaningAndAllowsAnotherVisibleProcess()
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(Pointer(0, 100));
        var source = Element(0, "Button", "last", 100) with
        {
            ParentControlType = "ToolBar", ParentRuntimeIdHash = "strip", ParentBounds = new RectD(0, 0, 120, 30),
        };
        interpreter.OnSnapshot(source, 1);
        interpreter.OnPointer(Pointer(300, 140));
        var destination = Element(300, "Pane", "safe", 140, 400);
        var edge = Assert.Single(interpreter.OnSnapshot(destination, 301).Events, e => e.Kind == SemanticEventKind.GroupEdge);
        Assert.Same(source, edge.Element);
        Assert.Same(destination, edge.Destination);
        Assert.Equal("100:strip", edge.GroupKey);
        Assert.Equal(PolicyOutcome.Emit, Policy().Evaluate(edge, 301).Outcome);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DisabledCollisionDestinationsStaySilentDespiteGenericDisabledOverride(bool strip)
    {
        var source = Element(0, strip ? "Button" : "Slider", "source", 100);
        var disabled = source with { IsEnabled = false };
        var e = new SemanticEvent(strip ? SemanticEventKind.GroupEdge : SemanticEventKind.RangeLimit,
            1, 0, 0, 100, source.ControlType, source.ElementKey, null, 1, [], false, 0,
            strip ? source : disabled, Destination: strip ? disabled : null);
        var policy = Policy();
        policy.Config.IncludeDisabledControls = true;
        Assert.Equal("disabled-control", policy.Evaluate(e, 0).Reason);
    }

    [Fact]
    public void RangeCollisionWithoutKnownCurrentContextFailsClosed()
    {
        var e = new SemanticEvent(SemanticEventKind.RangeLimit, 1, 0, 0, 100, "Slider", null,
            null, 1, [], false, 0, null);
        Assert.Equal("unknown-destination", Policy().Evaluate(e, 0).Reason);
    }

    [Fact]
    public void DestinationMustAlsoBeAllowlistedWhenTheSourceIsAllowed()
    {
        var source = Element(0, "Button", "source", 100);
        var destination = Element(0, "Pane", "destination", 140, 400);
        var e = new SemanticEvent(SemanticEventKind.GroupEdge, 1, 0, 0, 100, "Button", source.ElementKey,
            "strip", 1, [], false, 0, source, Destination: destination);
        var policy = Policy();
        policy.Config.AppPolicy = AppPolicy.OptInAllowlist;
        policy.Config.AllowedProcesses = ["notepad"];
        Assert.Equal("app-not-allowlisted", policy.Evaluate(e, 0).Reason);
    }

    [Fact]
    public void HysteresisContextCannotLetOlderSliderMetadataReplaceCurrent()
    {
        var interpreter = new SemanticInterpreter();
        interpreter.OnPointer(Pointer(0, 20));
        var slider = Element(0, "Slider", "slider", 20) with { Range = new RangeInfo(50, 0, 100, 1, 10) };
        interpreter.OnSnapshot(slider, 1);
        var refreshed = slider with { RequestedAtMs = 100 };
        interpreter.OnSnapshot(refreshed, 100);
        interpreter.OnPointer(Pointer(150, 42) with { PrimaryDown = true });
        var neighbor = Element(150, "Button", "neighbor", 42) with { Bounds = new RectD(42, 0, 40, 30) };
        Assert.Contains("hysteresis-hold", interpreter.OnSnapshot(neighbor, 151).Notes);
        var old = slider with { RequestedAtMs = 50, Range = new RangeInfo(0, 0, 100, 1, 10) };
        var result = interpreter.OnSnapshot(old, 200);
        Assert.Contains("older-snapshot-discarded", result.Notes);
        Assert.Empty(result.Events);
        Assert.Same(refreshed, interpreter.Current);
    }
}
