using System.Text.Json;
using Haptics.Core;
using Haptics.Windows;

namespace Haptics.Windows.Tests;

public class OutputOwnershipTests
{
    [Fact]
    public void NamedLeaseHasAtMostOneOwnerAndCanBeReacquiredAfterRelease()
    {
        var name = $@"Local\HapticsEverywhereTest-{Guid.NewGuid():N}";
        using var first = OutputOwnership.Create("tray", name);
        using var second = OutputOwnership.Create("Showcase", name);

        Assert.True(first.TryAcquire());
        Assert.True(first.IsOwner);
        Assert.False(second.TryAcquire());
        Assert.False(second.IsOwner);

        first.Dispose();

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
        Assert.True(next.TryAcquire());
        Assert.True(next.IsOwner);
    }
}
