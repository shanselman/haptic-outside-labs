using System.Text.Json;
using Haptics.Core;
using Haptics.Lab;
using Haptics.Lab.Experiments;
using Haptics.Windows;

namespace Haptics.Windows.Tests;

public class ExperimentDeviceEvidenceTests
{
    [Theory]
    [InlineData(0L, 0xFF515700U, "promoted-pen")]
    [InlineData(0L, 0xFF515701U, "promoted-pen")]
    [InlineData(12L, 0xFF515700U, "promoted-pen")]
    [InlineData(0L, 0xFF515780U, "promoted-touch")]
    [InlineData(0L, 0xFF5157FFU, "promoted-touch")]
    [InlineData(0L, 0U, "Handleless")]
    [InlineData(12L, 0U, "Handle")]
    [InlineData(12L, 0xFF515600U, "Handle")]
    public void InputProbeSeparatesPenAndTouchPromotionWithoutEnablingOutput(
        long handle, uint extra, string expected)
    {
        var input = new RawMouseInput(0, RawInputCode.Background, handle, 0, RawMouseButtons.None, 0, 0, 0, extra);

        Assert.Equal(expected, Exp030RawInputForm.DescribeSource(input));
        if (expected.StartsWith("promoted-", StringComparison.Ordinal))
        {
            Assert.Equal(expected == "promoted-pen" ? PointerInputClass.Pen : PointerInputClass.Promoted, input.InputClass);
            Assert.Null(ControllerRouting.Resolve(null, handle, input.InputClass).Controller);
        }
    }

    [Theory]
    [InlineData(null, "mouse")]
    [InlineData("mouse", "mouse")]
    [InlineData("touchpad", "touchpad")]
    [InlineData("pen", "pen")]
    [InlineData("PEN", "pen")]
    [InlineData("any", "any")]
    public void ForegroundTargetIncludesPen(string? input, string expected) =>
        Assert.Equal(expected, Exp010ForegroundForm.ParseTarget(input));

    [Theory]
    [InlineData("")]
    [InlineData("stylus")]
    [InlineData("unknown")]
    public void UnknownForegroundTargetIsRejected(string target) =>
        Assert.Throws<ArgumentException>(() => Exp010ForegroundForm.ParseTarget(target));

    [Theory]
    [InlineData("unknown")]
    public void UnsupportedBackgroundTargetReportsAnErrorInsteadOfStartingTrials(string target) =>
        Assert.Throws<ArgumentException>(() => new Exp020BackgroundForm(CliOptions.Parse(["--target", target])));

    [Theory]
    [InlineData(PointerInputClass.Pen, true)]
    [InlineData(PointerInputClass.Promoted, false)]
    [InlineData(PointerInputClass.Handle, false)]
    [InlineData(PointerInputClass.Handleless, false)]
    public void BackgroundPenTrialsCanOnlyBeTriggeredByPenClassInput(PointerInputClass inputClass, bool expected) =>
        Assert.Equal(expected, Exp020BackgroundForm.AcceptsInput(true, inputClass));

    [Theory]
    [InlineData(true, 0, 0, 20, 30, true)]
    [InlineData(true, 2, 1, 10, 10, false)]
    [InlineData(true, 0, 0, int.MinValue, int.MinValue, false)]
    [InlineData(false, 0, 0, 20, 30, false)]
    [InlineData(false, 2, 1, 10, 10, true)]
    public void PenMovementUsesObservedPositionRatherThanPromotedMouseDeltas(
        bool pen, int dx, int dy, int x, int y, bool expected)
    {
        var input = new RawMouseInput(0, RawInputCode.Background, 0, 0, RawMouseButtons.None, 0, dx, dy, 0xFF515700);
        Assert.Equal(expected, Exp020BackgroundForm.IsMovement(pen, input, (x, y), (10, 10)));
        Assert.False(Exp020BackgroundForm.IsMovement(true, input, (x, y), null));
    }

    [Theory]
    [InlineData("pen", "Pen", true, true, true)]
    [InlineData("pen", "Mouse", true, true, false)]
    [InlineData("pen", "Touchpad", true, true, false)]
    [InlineData("pen", "None", false, true, false)]
    [InlineData("pen", "Pen", false, true, false)]
    [InlineData("pen", "Pen", true, false, false)]
    [InlineData("mouse", "Mouse", true, true, true)]
    [InlineData("touchpad", "Touchpad", true, true, true)]
    [InlineData("any", "Pen", true, true, true)]
    public void ForegroundTrialRequiresTheSelectedControllerAndForeground(
        string target, string actual, bool present, bool foreground, bool expected)
    {
        var trial = Trial("controller-a", control: false);
        var state = new ThreadManagerState(1, true, true, 1, present, actual, present ? "controller-a" : null, null);

        Exp010ForegroundForm.ValidateTarget(trial, target, state, foreground);

        Assert.Equal(expected, trial.ProvenanceValid);
        Assert.Equal(expected, trial.ProvenanceIssues.Count == 0);
    }

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
