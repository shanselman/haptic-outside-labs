using System.Text.Json;
using Haptics.Core;

namespace Haptics.Windows.Tests;

public class UiaGenerationTests
{
    private static UiaClient Worker(double timeout = 3000) =>
        new(Path.Combine(AppContext.BaseDirectory, "haptics-lab.exe"), [UiaWorker.Arg, UiaWorker.FaultInjectionArg],
            new UiaClientOptions { TimeoutMs = timeout, AllowFaultInjection = true });

    private static async Task<UiaResponse> Result(Task<UiaResponse> task, long generation, string? status = null)
    {
        var response = await task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(generation, response.Generation);
        Assert.Equal(status, response.ClientStatus);
        return response;
    }

    [Fact]
    public async Task SuccessAndCoalescedRequestsRetainTheirOwnGeneration()
    {
        using var client = Worker();
        Assert.True((await Result(client.QueryAsync(0, 0, "ping", 10), 10)).Ok);
        var slow = client.QueryAsync(400, 0, "slow", 11);
        var replaced = client.QueryAsync(0, 0, "ping", 12);
        var latest = client.QueryAsync(0, 0, "ping", 13);
        await Result(replaced, 12, "superseded");
        Assert.True((await Result(slow, 11)).Ok);
        Assert.True((await Result(latest, 13)).Ok);
    }

    [Fact]
    public async Task RequestLatestStampsSnapshotOnTheClientOnly()
    {
        using var client = Worker();
        var received = new TaskCompletionSource<UiaResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Response += (response, _, _) => received.TrySetResult(response);
        client.RequestLatest(0, 0, 123, 0, 17);
        var response = await Result(received.Task, 17);
        if (response.Snapshot is { } snapshot)
        {
            Assert.Equal(17, snapshot.Generation);
            Assert.Equal(123, snapshot.RequestedAtMs);
            Assert.DoesNotContain("generation", JsonSerializer.Serialize(snapshot, HapticsJson.Compact),
                StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("generation", JsonSerializer.Serialize(response, HapticsJson.Compact),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailedStartRetainsGeneration()
    {
        using var client = new UiaClient(Path.Combine(AppContext.BaseDirectory, "missing-uia-worker.exe"), []);
        await Result(client.QueryAsync(0, 0, generation: 21), 21, "worker-start-failed");
    }

    [Fact]
    public async Task ExhaustedRestartBudgetRetainsGeneration()
    {
        using var client = new UiaClient(Path.Combine(AppContext.BaseDirectory, "missing-uia-worker.exe"), [],
            new UiaClientOptions { MaxRestartsPerMinute = 0 });
        await Result(client.QueryAsync(0, 0, generation: 22), 22, "worker-restart-budget-exhausted");
    }

    [Fact]
    public async Task ExitBeforeReadyRetainsGeneration()
    {
        using var client = new UiaClient(Environment.GetEnvironmentVariable("ComSpec")!, ["/d", "/c", "exit", "0"]);
        await Result(client.QueryAsync(0, 0, generation: 23), 23, "worker-exited-before-ready");
    }

    [Fact]
    public async Task StartupDeadlineRetainsGeneration()
    {
        using var client = new UiaClient(Environment.GetEnvironmentVariable("ComSpec")!, ["/d", "/c", "set /p waiting="],
            new UiaClientOptions { StartupTimeoutMs = 100 });
        await Result(client.QueryAsync(0, 0, generation: 24), 24, "worker-start-timeout");
    }

    [Fact]
    public async Task QueryDeadlineRetainsGeneration()
    {
        using var client = Worker(100);
        await client.QueryAsync(0, 0, "ping");
        await Result(client.QueryAsync(0, 0, "hang", 25), 25, "timeout");
    }

    [Fact]
    public async Task DisposalRetainsBothOutstandingAndPendingGenerations()
    {
        using var client = Worker();
        await client.QueryAsync(0, 0, "ping");
        var outstanding = client.QueryAsync(10000, 0, "slow", 26);
        var pending = client.QueryAsync(0, 0, "ping", 27);
        client.Dispose();
        await Result(outstanding, 26, "disposed");
        await Result(pending, 27, "disposed");
        await Result(client.QueryAsync(0, 0, generation: 28), 28, "disposed");
    }
}
