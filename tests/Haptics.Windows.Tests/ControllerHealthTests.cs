using Haptics.Core;

namespace Haptics.Windows.Tests;

public class ControllerHealthTests
{
    [Theory]
    [InlineData(true, null, null, "None")]
    [InlineData(false, "waveform-not-supported-by-controller", null, "UnsupportedWaveform")]
    [InlineData(false, "controller-mismatch", null, "Transient")]
    [InlineData(false, "no-captured-controller", null, "Transient")]
    [InlineData(false, "fixture", unchecked((int)0x80070006), "Lost")]
    [InlineData(false, "manager-null", null, "Transient")]
    [InlineData(false, "api-absent", null, "Transient")]
    [InlineData(false, null, null, "Transient")]
    [InlineData(false, "fixture", unchecked((int)0x80004005), "Transient")]
    public void FailureClassificationRequiresExplicitLossEvidence(
        bool accepted, string? error, int? hresult, string expected) =>
        Assert.Equal(expected, ControllerHealth.Classify(new HapticAttempt
        {
            Route = OutputRoutes.CachedController, Accepted = accepted, Error = error, HResult = hresult,
        }).ToString());
}
