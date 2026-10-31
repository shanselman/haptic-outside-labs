using System.Text.Json;
using Haptics.Core;
using Haptics.Lab.Experiments;

namespace Haptics.Windows.Tests;

public class ExperimentDeviceEvidenceTests
{
    [Fact]
    public void SameVidPidDevicesRetainSeparateHashedIdentitiesAndCounts()
    {
        var devices = new Dictionary<long, int> { [10] = 3, [20] = 7 };
        RawDeviceInfo Describe(long handle) =>
            new(handle, "Mouse", "VID_045E&PID_1234", handle == 10 ? "hash-a" : "hash-b", "Bluetooth");
        var rows = Exp020BackgroundForm.PrimeDeviceCounts(devices, Describe);
        Assert.Equal(2, rows.Count);
        Assert.Equal(["hash-a", "hash-b"], rows.Select(r => r.NameHash));
        Assert.Equal([3, 7], rows.Select(r => r.Events));
        Assert.Single(rows.Select(r => r.VidPid).Distinct());
        var json = JsonSerializer.Serialize(rows, HapticsJson.Compact);
        Assert.DoesNotContain("Handle", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hash-a", json);
        Assert.Contains("hash-b", json);
    }

    [Fact]
    public void UnknownAndHandlelessDevicesDoNotCollide()
    {
        var devices = new Dictionary<long, int> { [0] = 2, [10] = 4, [20] = 6 };
        var rows = Exp020BackgroundForm.PrimeDeviceCounts(devices,
            handle => new RawDeviceInfo(handle, "Mouse", null, "none", "unknown"));
        Assert.Equal(3, rows.Count);
        Assert.Equal(12, rows.Sum(r => r.Events));
    }

    [Fact]
    public void ForegroundIdentityComesFromTheSentControllerAndTheControlObservation()
    {
        var pulse = Trial("controller-a", control: false);
        pulse.Context["controllerIdHash"] = "stale-observation";
        Assert.Equal("controller-a", Exp010ForegroundForm.TargetControllerIdentity([pulse, Trial("controller-a", control: true)]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("none")]
    [InlineData("controller-b")]
    public void MissingOrMixedTrialIdentityCannotSupplyABaseline(string? other)
    {
        Assert.Null(Exp010ForegroundForm.TargetControllerIdentity([Trial("controller-a", false), Trial(other, true)]));
        Assert.Null(Exp010ForegroundForm.TargetControllerIdentity([Trial("controller-a", true), Trial(other, false)]));
    }

    [Fact]
    public void InvalidOrUnansweredTrialsDoNotDefineTheBaselineIdentity()
    {
        var invalid = Trial("controller-b", false);
        invalid.Invalidate("negative-control");
        var unanswered = Trial("controller-c", true);
        unanswered.Answer = TrialAnswer.None;
        Assert.Equal("controller-a", Exp010ForegroundForm.TargetControllerIdentity([Trial("controller-a", false), invalid, unanswered]));
        Assert.Null(Exp010ForegroundForm.TargetControllerIdentity([invalid, unanswered]));
    }

    [Theory]
    [InlineData("controller-a", ExperimentStatus.FAIL)]
    [InlineData("controller-b", ExperimentStatus.INDETERMINATE)]
    [InlineData(null, ExperimentStatus.INDETERMINATE)]
    [InlineData("none", ExperimentStatus.INDETERMINATE)]
    public void NotFeltRequiresEveryPulseToMatchTheTargetBaseline(string? lastPulseIdentity, ExperimentStatus expected)
    {
        var trials = Enumerable.Range(0, 6).Select(_ => Trial("controller-a", false, OutputRoutes.RawThreadManager)).ToList();
        foreach (var pulse in trials)
        {
            pulse.Answer = TrialAnswer.FeltNo;
            pulse.Attempt!.Accepted = true;
        }

        trials[^1].Attempt!.ControllerIdHash = lastPulseIdentity;
        trials.AddRange(Enumerable.Range(0, 6).Select(_ =>
        {
            var control = Trial("controller-a", true);
            control.Answer = TrialAnswer.FeltNo;
            return control;
        }));
        var baseline = Exp020BackgroundForm.ArmBaselinePassed(trials, OutputRoutes.RawThreadManager, "controller-a", true);
        Assert.Equal(expected, PhysicalEvaluator.Evaluate(OutputRoutes.RawThreadManager, trials, baseline).Status);
        Assert.False(Exp020BackgroundForm.ArmBaselinePassed(trials, OutputRoutes.RawThreadManager, "controller-a", false));
    }

    private static TrialRecord Trial(string? identity, bool control, string arm = "Step") => new()
    {
        Index = 1,
        Arm = arm,
        IsControl = control,
        Answer = TrialAnswer.FeltYes,
        Context = new() { ["controllerIdHash"] = identity },
        Attempt = control ? null : new HapticAttempt { Route = OutputRoutes.ForegroundManager, ControllerIdHash = identity },
    };
}
