using System.Text.Json;
using Haptics.Core;

namespace Haptics.Core.Tests;

public class InputTraceTests
{
    [Theory]
    [InlineData(0, PointerInputClass.Handleless)]
    [InlineData(42, PointerInputClass.Handle)]
    public void ExistingPointerConstructorsDeriveInputClassWithoutChangingOrigin(long handle, PointerInputClass expected)
    {
        var pointer = new PointerObservation(10, 20, 30, InputOrigin.Replay, false, 100, handle);
        Assert.Equal(expected, pointer.InputClass);
        Assert.Equal(InputOrigin.Replay, pointer.Origin);
        Assert.Equal(pointer, JsonSerializer.Deserialize<PointerObservation>(JsonSerializer.Serialize(pointer, HapticsJson.Compact), HapticsJson.Compact));
    }

    [Fact]
    public void LegacyPointerDefaultsRemainReplayHandlelessAndForegroundZero()
    {
        var output = TraceReplay.Run(["""{"type":"pointer","t":10,"x":20,"y":30}"""]);
        var pointer = Assert.Single(output.Pointers);
        Assert.Equal(new PointerObservation(10, 20, 30, InputOrigin.Replay, false, 0), pointer);
    }

    [Theory]
    [InlineData(0, PointerInputClass.Handleless)]
    [InlineData(42, PointerInputClass.Handle)]
    public void LegacySerializedPointerDerivesInputClass(long handle, PointerInputClass expected)
    {
        var json = $$"""{"timestampMs":10,"x":20,"y":30,"origin":"Replay","primaryDown":false,"foregroundPid":100,"deviceHandle":{{handle}}}""";
        var pointer = JsonSerializer.Deserialize<PointerObservation>(json, HapticsJson.Compact);
        Assert.Equal(expected, pointer.InputClass);
        Assert.Equal(new PointerObservation(10, 20, 30, InputOrigin.Replay, false, 100, handle), pointer);
    }

    [Fact]
    public void DefaultPointerIsHandleless() =>
        Assert.Equal(PointerInputClass.Handleless, default(PointerObservation).InputClass);

    [Theory]
    [InlineData("HardwareRaw", 42, "Handle", InputOrigin.HardwareRaw, PointerInputClass.Handle)]
    [InlineData("SyntheticOrUnknown", 0, "Handleless", InputOrigin.SyntheticOrUnknown, PointerInputClass.Handleless)]
    [InlineData("SyntheticOrUnknown", 42, "Promoted", InputOrigin.SyntheticOrUnknown, PointerInputClass.Promoted)]
    public void ReplayPreservesInputMetadata(string origin, long handle, string inputClass, InputOrigin expectedOrigin, PointerInputClass expectedClass)
    {
        var output = TraceReplay.Run([
            $$"""{"type":"pointer","t":10,"x":20,"y":30,"fg":100,"handle":{{handle}},"origin":"{{origin}}","inputClass":"{{inputClass}}"}""",
        ]);
        var pointer = Assert.Single(output.Pointers);
        Assert.Equal(handle, pointer.DeviceHandle);
        Assert.Equal(expectedOrigin, pointer.Origin);
        Assert.Equal(expectedClass, pointer.InputClass);
    }

    [Theory]
    [InlineData("origin", "not-an-origin")]
    [InlineData("origin", "123")]
    [InlineData("inputClass", "not-a-class")]
    [InlineData("inputClass", "123")]
    public void UnknownInputMetadataFailsExplicitly(string field, string value)
    {
        Assert.Throws<InvalidDataException>(() => TraceReplay.Run([
            $$"""{"type":"pointer","t":10,"x":20,"y":30,"{{field}}":"{{value}}"}""",
        ]));
    }

    [Fact]
    public void ArrivalAndRemovalRetainHandlesAndUseExistingGlobalAvailabilityBehavior()
    {
        var output = TraceReplay.Run([
            """{"type":"device","t":0,"handle":42,"arrived":true}""",
            """{"type":"device","t":10,"handle":42,"arrived":false}""",
        ]);
        Assert.Equal([
            new TraceReplay.ReplayDeviceChange(0, 42, true),
            new TraceReplay.ReplayDeviceChange(10, 42, false),
        ], output.DeviceChanges);
        Assert.Contains(output.Decisions, d => d.EventKind == "DeviceUnavailable");
        Assert.Contains(output.Notes, n => n.EndsWith("state-cleared:device-unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void ForegroundEventUsesExistingPointerTransitionAndCarriesForwardToNextPointer()
    {
        var output = TraceReplay.Run([
            """{"type":"pointer","t":0,"x":20,"y":30,"fg":100,"handle":42}""",
            """{"type":"foreground","t":10,"pid":200}""",
            """{"type":"pointer","t":20,"x":21,"y":30,"handle":42}""",
        ]);
        Assert.Single(output.Decisions, d => d.EventKind == "ForegroundChanged");
        Assert.Equal(200, output.Pointers[1].ForegroundPid);
        Assert.Contains(output.Notes, n => n.EndsWith("state-cleared:foreground-changed", StringComparison.Ordinal));
    }

    [Fact]
    public void ForegroundBeforeFirstPointerSuppliesItsForegroundPid()
    {
        var output = TraceReplay.Run([
            """{"type":"foreground","t":0,"pid":200}""",
            """{"type":"pointer","t":10,"x":20,"y":30}""",
        ]);
        Assert.Equal(200, Assert.Single(output.Pointers).ForegroundPid);
        Assert.Empty(output.Decisions);
    }
}
