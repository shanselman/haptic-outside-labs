using Haptics.Tray;

namespace Haptics.Windows.Tests;

public class StartupPrimingTests
{
    private static ControllerDiscoveryResult Unavailable =>
        new(new Dictionary<long, CapturedController>(), "No automatically accessible controller.");

    [Fact]
    public void PendingLookupDoesNotImmediatelyOpenAWindow()
    {
        var pending = new TaskCompletionSource<ControllerDiscoveryResult>();
        var startup = new StartupPriming(pending.Task, 100);
        Assert.Equal((null, false), startup.Poll(100, enabled: true, alreadyPrimed: false));
        Assert.Equal((null, false), startup.Poll(100 + StartupPriming.FallbackAfterMs - 1, true, false));
        Assert.Equal((null, true), startup.Poll(100 + StartupPriming.FallbackAfterMs, true, false));
        Assert.Equal((null, false), startup.Poll(9000, true, false));
    }

    [Fact]
    public void UnavailableLookupFallsBackWithoutWaitingForTimeout()
    {
        var result = Unavailable;
        var startup = new StartupPriming(Task.FromResult(result), 0);
        var update = startup.Poll(1, true, false);
        Assert.Same(result, update.Result);
        Assert.True(update.ShowFallback);
        Assert.Equal((null, false), startup.Poll(2, true, false));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void OffOrAlreadyPrimedNeverOpensFallback(bool enabled, bool alreadyPrimed)
    {
        var startup = new StartupPriming(Task.FromResult(Unavailable), 0);
        Assert.False(startup.Poll(10000, enabled, alreadyPrimed).ShowFallback);
    }

    [Fact]
    public void LateResultIsStillDeliveredOnceAfterTimeout()
    {
        var pending = new TaskCompletionSource<ControllerDiscoveryResult>();
        var startup = new StartupPriming(pending.Task, 0);
        Assert.True(startup.Poll(StartupPriming.FallbackAfterMs, true, false).ShowFallback);
        var result = Unavailable;
        pending.SetResult(result);
        var update = startup.Poll(10000, true, true);
        Assert.Same(result, update.Result);
        Assert.False(update.ShowFallback);
        Assert.Null(startup.Poll(11000, true, true).Result);
    }

    [Fact]
    public void UnexpectedWorkerErrorsAreNotTurnedIntoSuccess()
    {
        var error = new InvalidOperationException("Unexpected bug");
        var startup = new StartupPriming(Task.FromException<ControllerDiscoveryResult>(error), 0);
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => startup.Poll(1, true, false)));
    }
}
