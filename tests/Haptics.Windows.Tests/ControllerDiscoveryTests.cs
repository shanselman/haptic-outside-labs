using System.Runtime.InteropServices;

namespace Haptics.Windows.Tests;

public class ControllerDiscoveryTests
{
    [Fact]
    public void DiscoveryDeduplicatesHandlesAndNeverGuessesTheHandlelessDevice()
    {
        var lookedUp = new List<long>();
        var result = ControllerDiscovery.Discover(() => [0, 12, 12, 34], h =>
        {
            lookedUp.Add(h);
            return null;
        });
        Assert.Equal([12L, 34L], lookedUp);
        Assert.Empty(result.Controllers);
        Assert.Contains("0 controller(s) from 2 mouse device(s)", result.Diagnostic);
        Assert.Contains("prime", result.Diagnostic);
    }

    [Fact]
    public void AccessDeniedIsReportedAndDoesNotPreventCheckingAnotherDevice()
    {
        var lookedUp = new List<long>();
        var result = ControllerDiscovery.Discover(() => [12, 34], h =>
        {
            lookedUp.Add(h);
            if (h == 12)
            {
                throw new COMException("Access denied", unchecked((int)0x80070005));
            }

            return null;
        });
        Assert.Equal([12L, 34L], lookedUp);
        Assert.Empty(result.Controllers);
        Assert.Contains("0x80070005", result.Diagnostic);
        Assert.Contains("1 lookup(s) failed", result.Diagnostic);
    }

    [Fact]
    public void MissingDeviceIsDifferentFromFailedLookup()
    {
        var result = ControllerDiscovery.Discover(() => [12], _ => null);
        Assert.Empty(result.Controllers);
        Assert.DoesNotContain("failed", result.Diagnostic);
        Assert.Contains("prime", result.Diagnostic);
    }

    [Fact]
    public void UnexpectedErrorsAreNotSuppressed()
    {
        var error = new InvalidOperationException("Unexpected acquisition bug");
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() =>
            ControllerDiscovery.Discover(() => [12], _ => throw error)));
    }
}
