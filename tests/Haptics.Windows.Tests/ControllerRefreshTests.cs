using Haptics.Tray;

namespace Haptics.Windows.Tests;

public class ControllerRefreshTests
{
    private static ControllerDiscoveryResult EmptyResult =>
        new(new Dictionary<long, CapturedController>(), "No controller");

    [Fact]
    public void ArrivalStartsANewLookupButRemovalAloneDoesNot()
    {
        var calls = 0;
        var refresh = new ControllerRefresh(() =>
        {
            calls++;
            return Task.FromResult(EmptyResult);
        });
        refresh.Request();
        Assert.True(refresh.Poll(0, true, false).Started);
        Assert.NotNull(refresh.Poll(1, true, false).Result);
        refresh.DeviceChanged(arrived: false);
        Assert.False(refresh.Poll(2, true, false).Started);
        refresh.DeviceChanged(arrived: true);
        Assert.True(refresh.Poll(3, true, false).Started);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void DeviceChangesDiscardStaleResultsAndCoalesceIntoOneRefresh()
    {
        var jobs = new List<TaskCompletionSource<ControllerDiscoveryResult>>();
        var refresh = new ControllerRefresh(() =>
        {
            var job = new TaskCompletionSource<ControllerDiscoveryResult>();
            jobs.Add(job);
            return job.Task;
        });
        refresh.Request();
        refresh.Poll(0, true, false);
        refresh.DeviceChanged(false);
        refresh.DeviceChanged(true);
        refresh.DeviceChanged(true);
        Assert.False(refresh.Poll(1, true, false).Started);
        Assert.Single(jobs);
        jobs[0].SetResult(EmptyResult);
        var update = refresh.Poll(2, true, false);
        Assert.Null(update.Result);
        Assert.False(update.ShowFallback);
        Assert.True(update.Started);
        Assert.Equal(2, jobs.Count);
        var fresh = EmptyResult;
        jobs[1].SetResult(fresh);
        Assert.Same(fresh, refresh.Poll(3, true, false).Result);
    }

    [Fact]
    public void OffDefersDiscoveryAndNeverShowsFallback()
    {
        var calls = 0;
        var refresh = new ControllerRefresh(() =>
        {
            calls++;
            return Task.FromResult(EmptyResult);
        });
        refresh.DeviceChanged(true);
        var off = refresh.Poll(5000, false, false);
        Assert.False(off.Started);
        Assert.False(off.ShowFallback);
        Assert.Equal(0, calls);
        Assert.True(refresh.Poll(6000, true, false).Started);
        Assert.False(refresh.Poll(7000, false, false).ShowFallback);
    }

    [Fact]
    public void StalledLookupKeepsFallbackWithoutStartingMoreWorkers()
    {
        var calls = 0;
        var pending = new TaskCompletionSource<ControllerDiscoveryResult>();
        var refresh = new ControllerRefresh(() =>
        {
            calls++;
            return pending.Task;
        });
        refresh.Request();
        refresh.Poll(0, true, false);
        refresh.DeviceChanged(true);
        Assert.True(refresh.Poll(StartupPriming.FallbackAfterMs, true, false).ShowFallback);
        Assert.False(refresh.Poll(StartupPriming.FallbackAfterMs + 1, true, false).ShowFallback);
        Assert.Equal(1, calls);
    }
}
