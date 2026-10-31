using Haptics.Core;

namespace Haptics.Core.Tests;

public class ForegroundStationaryTests
{
    [Fact]
    public void DeviceRemovalThenStationaryTapDoesNotRearrive()
    {
        var output = TraceReplay.Run(Path.Combine(AppContext.BaseDirectory, "traces", "device-removed-stationary-tap.jsonl"));
        Assert.Equal(["Hover"], output.Haptic.Select(d => d.Waveform));
        Assert.Contains(output.Notes, n => n.EndsWith("stationary-ui-change-silent", StringComparison.Ordinal));
    }

    [Fact]
    public void ClickToActivateDoesNotRearriveWithoutPointerMovement()
    {
        var output = TraceReplay.Run(Path.Combine(AppContext.BaseDirectory, "traces", "foreground-change-stationary.jsonl"));
        Assert.Equal(["Hover"], output.Haptic.Select(d => d.Waveform));
        Assert.Contains(output.Notes, n => n.EndsWith("stationary-ui-change-silent", StringComparison.Ordinal));
    }
}
