using System.Diagnostics;
using System.Text.Json;
using Haptics.Core;
using Haptics.Lab.Experiments;
using Haptics.Windows;

namespace Haptics.Windows.Tests;

public class OutputOwnershipTests
{
    [Fact]
    public void NamedLeaseBlocksAnotherThreadAndCanBeReacquiredAfterRelease()
    {
        var name = $@"Local\HapticsEverywhereTest-{Guid.NewGuid():N}";
        using var first = OutputOwnership.Create("tray", name);
        using var second = OutputOwnership.Create("Showcase", name);
        using var acquiredSignal = new ManualResetEventSlim();
        using var releaseSignal = new ManualResetEventSlim();
        var acquired = false;
        var ownerThread = new Thread(() =>
        {
            acquired = first.TryAcquire();
            acquiredSignal.Set();
            releaseSignal.Wait();
            first.Dispose();
        }) { IsBackground = true };

        ownerThread.Start();
        try
        {
            Assert.True(acquiredSignal.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(acquired);
            Assert.False(second.TryAcquire());
            Assert.False(second.IsOwner);
        }
        finally
        {
            releaseSignal.Set();
            Assert.True(ownerThread.Join(TimeSpan.FromSeconds(5)));
        }

        Assert.True(second.TryAcquire());
        Assert.True(second.IsOwner);
    }

    [Fact]
    public void TrialProvenanceRecordsOwnerAndWhetherLeaseWasGranted()
    {
        var name = $@"Local\HapticsEverywhereTest-{Guid.NewGuid():N}";
        using var owner = OutputOwnership.Create("EXP-010", name);
        var trial = new TrialRecord { Index = 1, Arm = "Click" };

        owner.Record(trial);
        Assert.Equal("EXP-010", trial.Context["outputOwner"]);
        Assert.False(Assert.IsType<bool>(trial.Context["outputOwnershipGranted"]));
        Assert.Contains("outputOwner", JsonSerializer.Serialize(trial, HapticsJson.Compact));

        Assert.True(owner.TryAcquire());
        owner.Record(trial);
        Assert.True(Assert.IsType<bool>(trial.Context["outputOwnershipGranted"]));
    }

    [Fact]
    public void DisposedOwnershipCannotBeReacquired()
    {
        var owner = OutputOwnership.Create("test", $@"Local\HapticsEverywhereTest-{Guid.NewGuid():N}");
        owner.Dispose();
        Assert.Throws<ObjectDisposedException>(() => owner.TryAcquire());
    }

    [Fact]
    public void StoppedOutputOwnerRejectsLaterSends()
    {
        using var owner = OutputOwnership.Create("test", $@"Local\HapticsEverywhereTest-{Guid.NewGuid():N}");
        Assert.True(owner.TryAcquire());
        owner.StopSending();
        var sent = false;

        Assert.False(owner.TryRun(() => sent = true));
        Assert.False(sent);
    }

    [Fact]
    public void FailedForegroundSessionCreationReleasesOutputLease()
    {
        var name = $@"Local\HapticsEverywhereTest-{Guid.NewGuid():N}";
        using var owner = OutputOwnership.Create("EXP-010", name);
        using var next = OutputOwnership.Create("Showcase", name);
        Assert.True(owner.TryAcquire());

        Assert.Throws<IOException>(() => Exp010ForegroundForm.StartOwnedSession(owner,
            () => throw new IOException("test session start failure")));
        Assert.False(owner.IsOwner);
        Assert.True(next.TryAcquire());
    }

    [Fact]
    public void DisposeWaitsForAnActiveOutputSend()
    {
        var name = $@"Local\HapticsEverywhereTest-{Guid.NewGuid():N}";
        using var owner = OutputOwnership.Create("test", name);
        using var next = OutputOwnership.Create("next", name);
        using var sendStarted = new ManualResetEventSlim();
        using var disposeStarted = new ManualResetEventSlim();
        using var sendCompleted = new ManualResetEventSlim();
        Assert.True(owner.TryAcquire());
        var releasedDuringSend = false;
        var sendThread = new Thread(() => owner.TryRun(() =>
        {
            sendStarted.Set();
            if (disposeStarted.Wait(TimeSpan.FromSeconds(5)))
            {
                var deadline = Environment.TickCount64 + 100;
                while (Environment.TickCount64 < deadline)
                {
                    releasedDuringSend |= !owner.IsOwner;
                    Thread.Yield();
                }
            }
            else
            {
                releasedDuringSend = true;
            }

            sendCompleted.Set();
        })) { IsBackground = true };

        sendThread.Start();
        Assert.True(sendStarted.Wait(TimeSpan.FromSeconds(5)));
        disposeStarted.Set();
        owner.Dispose();

        Assert.True(sendCompleted.IsSet);
        Assert.False(releasedDuringSend);
        Assert.True(sendThread.Join(TimeSpan.FromSeconds(5)));
        Assert.True(next.TryAcquire());
    }

    [Fact]
    public void DisposeReleasesLeaseWhileOwnerThreadStaysAlive()
    {
        var name = $@"Local\HapticsEverywhereTest-{Guid.NewGuid():N}";
        using var owner = OutputOwnership.Create("tray", name);
        using var next = OutputOwnership.Create("Showcase", name);
        using var disposed = new ManualResetEventSlim();
        using var exit = new ManualResetEventSlim();
        var acquired = false;
        var ownerThread = new Thread(() =>
        {
            acquired = owner.TryAcquire();
            owner.Dispose();
            disposed.Set();
            exit.Wait(TimeSpan.FromSeconds(10));
        }) { IsBackground = true };

        ownerThread.Start();
        try
        {
            Assert.True(disposed.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(acquired);
            Assert.True(TryAcquireWithin(next, TimeSpan.FromSeconds(2)));
            Assert.True(ownerThread.IsAlive);
        }
        finally
        {
            exit.Set();
            Assert.True(ownerThread.Join(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void AbandonedLeaseCanBeAcquiredAfterOwnerThreadExits()
    {
        var name = $@"Local\HapticsEverywhereTest-{Guid.NewGuid():N}";
        using var abandoned = OutputOwnership.Create("previous", name);
        using var next = OutputOwnership.Create("next", name);
        var acquired = false;
        var ownerThread = new Thread(() => acquired = abandoned.TryAcquire());

        ownerThread.Start();
        Assert.True(ownerThread.Join(TimeSpan.FromSeconds(5)));
        Assert.True(acquired);
        Assert.False(abandoned.IsOwner);
        Assert.True(TryAcquireWithin(next, TimeSpan.FromSeconds(5)));
        Assert.True(next.IsOwner);
    }

    private static bool TryAcquireWithin(OutputOwnership ownership, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        do
        {
            if (ownership.TryAcquire())
            {
                return true;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(10));
        } while (stopwatch.Elapsed < timeout);

        return ownership.TryAcquire();
    }
}
