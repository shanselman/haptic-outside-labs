using System.Reflection;
using Haptics.Core;

namespace Haptics.Windows.Tests;

public class UiaLifecycleTests
{
    private static string LabExe => Path.Combine(AppContext.BaseDirectory, "haptics-lab.exe");

    [Fact]
    public async Task StartFailureCompletesRequestExplicitly()
    {
        using var client = new UiaClient(Path.Combine(AppContext.BaseDirectory, "missing-uia-worker.exe"), [],
            new UiaClientOptions { TimeoutMs = 100, StartupTimeoutMs = 100 });
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var response = await client.QueryAsync(0, 0, "ping").WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(elapsed.ElapsedMilliseconds <= 250, $"{elapsed.ElapsedMilliseconds}ms");
        Assert.False(response.Ok);
        Assert.Equal(1, response.Id);
        Assert.Equal("worker-start-failed", response.ClientStatus);
        Assert.Equal(response.ClientStatus, response.Error);
    }

    [Fact]
    public async Task ExhaustedBudgetCompletesRequestAndWatchdogRestartsAfterRefill()
    {
        using var client = new UiaClient(LabExe, [UiaWorker.Arg],
            new UiaClientOptions { MaxRestartsPerMinute = 1, TimeoutMs = 100 });
        var gate = Field<object>(client, "_gate");
        var previousStart = MonotonicClock.Instance.NowMs - 59_000;
        lock (gate)
        {
            Field<Queue<double>>(client, "_restarts").Enqueue(previousStart);
        }

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var response = await client.QueryAsync(0, 0, "ping").WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(elapsed.ElapsedMilliseconds <= 250, $"{elapsed.ElapsedMilliseconds}ms");
        Assert.False(response.Ok);
        Assert.Equal("worker-restart-budget-exhausted", response.ClientStatus);
        Assert.Equal(response.ClientStatus, response.Error);

        lock (gate)
        {
            Assert.Equal(previousStart + 60_000, Field<double?>(client, "_retryAt"));
        }

        Assert.True(SpinWait.SpinUntil(() =>
        {
            lock (gate)
            {
                return Field<System.Diagnostics.Process?>(client, "_proc") is not null;
            }
        }, TimeSpan.FromSeconds(5)), "Watchdog did not restart after budget refill without another request.");

        var ping = await client.QueryAsync(0, 0, "ping").WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(ping.Ok, ping.Error);
        Assert.Equal(2, ping.Id);
    }

    [Fact]
    public async Task FailedStartsConsumeTheRestartBudget()
    {
        using var client = new UiaClient(Path.Combine(AppContext.BaseDirectory, "missing-uia-worker.exe"), [],
            new UiaClientOptions { MaxRestartsPerMinute = 1 });
        await client.QueryAsync(0, 0).WaitAsync(TimeSpan.FromSeconds(2));
        var response = await client.QueryAsync(0, 0).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("worker-restart-budget-exhausted", response.ClientStatus);
    }

    private static T Field<T>(UiaClient client, string name) =>
        (T)typeof(UiaClient).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
}
