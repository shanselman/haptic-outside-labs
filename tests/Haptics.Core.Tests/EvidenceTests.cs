using Haptics.Core;

namespace Haptics.Core.Tests;

public class EvidenceTests
{
    private static TrialRecord Pulse(int i, TrialAnswer a, bool accepted = true, string arm = "R2") =>
        new() { Index = i, Arm = arm, Answer = a, Attempt = new HapticAttempt { Route = arm, Called = true, Accepted = accepted } };

    private static TrialRecord Control(int i, TrialAnswer a) =>
        new() { Index = i, Arm = TrialSchedule.ControlArm, IsControl = true, Answer = a };

    [Fact]
    public void ApiReturnAloneCanNeverProducePhysicalConfirmed()
    {
        var r = new ExperimentResult { RunId = "t", ExperimentId = "EXP-020", RequiresPhysicalConfirmation = true };
        r.Status = ExperimentStatus.PASS;
        r.EvidenceClass = EvidenceClass.OBSERVED;
        r.AddLabel(EvidenceLabels.SendReturnedTrue);
        r.AddLabel(EvidenceLabels.PhysicalConfirmed);

        var corrections = r.Finalize();

        Assert.Equal(ExperimentStatus.INDETERMINATE, r.Status);
        Assert.DoesNotContain(EvidenceLabels.PhysicalConfirmed, r.Labels);
        Assert.Contains(EvidenceLabels.PhysicalUnconfirmed, r.Labels);
        Assert.NotEmpty(corrections);
    }

    [Fact]
    public void AcceptedApiCallsWithNoAnswersAreIndeterminateNotPass()
    {
        var trials = Enumerable.Range(0, 10).Select(i => Pulse(i, TrialAnswer.None)).ToList();
        var v = PhysicalEvaluator.Evaluate("R2", trials, baselinePassed: true);
        Assert.Equal(ExperimentStatus.INDETERMINATE, v.Status);
        Assert.NotEqual(EvidenceClass.PHYSICAL, v.EvidenceClass);
    }

    [Fact]
    public void ExampleRecordsCanNeverPass()
    {
        var r = new ExperimentResult { RunId = "x", ExperimentId = "EXP-020", Example = true, Status = ExperimentStatus.PASS, EvidenceClass = EvidenceClass.PHYSICAL };
        r.Finalize();
        Assert.Equal(ExperimentStatus.NOT_RUN, r.Status);
    }

    [Fact]
    public void FailedAssertionBlocksPass()
    {
        var r = new ExperimentResult { RunId = "x", ExperimentId = "EXP-040", Status = ExperimentStatus.PASS };
        r.Assertions.Add(new Assertion { Name = "a", Passed = false });
        r.Finalize();
        Assert.Equal(ExperimentStatus.FAIL, r.Status);
    }

    [Fact]
    public void ClearDiscriminationIsPhysicalPass()
    {
        var trials = new List<TrialRecord>();
        for (var i = 0; i < 8; i++) trials.Add(Pulse(i, TrialAnswer.FeltYes));
        for (var i = 0; i < 8; i++) trials.Add(Control(100 + i, TrialAnswer.FeltNo));
        var v = PhysicalEvaluator.Evaluate("R2", trials, baselinePassed: false);
        Assert.Equal(ExperimentStatus.PASS, v.Status);
        Assert.Equal(EvidenceClass.PHYSICAL, v.EvidenceClass);
        Assert.Contains(EvidenceLabels.PhysicalConfirmed, v.Labels);
        Assert.True(v.FisherP < 0.001);
    }

    [Fact]
    public void FeelingEverythingIncludingControlsIsNotAPass()
    {
        var trials = new List<TrialRecord>();
        for (var i = 0; i < 8; i++) trials.Add(Pulse(i, TrialAnswer.FeltYes));
        for (var i = 0; i < 8; i++) trials.Add(Control(100 + i, TrialAnswer.FeltYes));
        var v = PhysicalEvaluator.Evaluate("R2", trials, baselinePassed: true);
        Assert.Equal(ExperimentStatus.INDETERMINATE, v.Status);
    }

    [Fact]
    public void NotFeltIsFailOnlyWhenBaselinePassed()
    {
        var trials = new List<TrialRecord>();
        for (var i = 0; i < 8; i++) trials.Add(Pulse(i, TrialAnswer.FeltNo));
        for (var i = 0; i < 8; i++) trials.Add(Control(100 + i, TrialAnswer.FeltNo));

        var withBaseline = PhysicalEvaluator.Evaluate("R2", trials, baselinePassed: true);
        var withoutBaseline = PhysicalEvaluator.Evaluate("R2", trials, baselinePassed: false);

        Assert.Equal(ExperimentStatus.FAIL, withBaseline.Status);
        Assert.Contains(EvidenceLabels.PhysicalNotFelt, withBaseline.Labels);
        Assert.Equal(ExperimentStatus.INDETERMINATE, withoutBaseline.Status);
    }

    [Fact]
    public void InvalidProvenanceTrialsAreExcluded()
    {
        var trials = new List<TrialRecord>();
        for (var i = 0; i < 8; i++)
        {
            var t = Pulse(i, TrialAnswer.FeltYes);
            t.Invalidate("lab-was-foreground");
            trials.Add(t);
        }

        for (var i = 0; i < 8; i++) trials.Add(Control(100 + i, TrialAnswer.FeltNo));
        var v = PhysicalEvaluator.Evaluate("R2", trials, baselinePassed: true);
        Assert.Equal(ExperimentStatus.INDETERMINATE, v.Status);
        Assert.Equal(8, v.InvalidTrialsExcluded);
    }

    [Fact]
    public void TooManyUncertainAnswersIsIndeterminate()
    {
        var trials = new List<TrialRecord>();
        for (var i = 0; i < 6; i++) trials.Add(Pulse(i, TrialAnswer.FeltYes));
        for (var i = 0; i < 6; i++) trials.Add(Pulse(10 + i, TrialAnswer.Uncertain));
        for (var i = 0; i < 6; i++) trials.Add(Control(100 + i, TrialAnswer.FeltNo));
        var v = PhysicalEvaluator.Evaluate("R2", trials, baselinePassed: true);
        Assert.Equal(ExperimentStatus.INDETERMINATE, v.Status);
    }

    [Fact]
    public void CombineAnyPassAllFail()
    {
        ArmVerdict V(ExperimentStatus s, EvidenceClass c) => new("a", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, s, c, [], "");
        Assert.Equal(ExperimentStatus.PASS, PhysicalEvaluator.Combine([V(ExperimentStatus.FAIL, EvidenceClass.OBSERVED), V(ExperimentStatus.PASS, EvidenceClass.PHYSICAL)]).Status);
        Assert.Equal(ExperimentStatus.FAIL, PhysicalEvaluator.Combine([V(ExperimentStatus.FAIL, EvidenceClass.OBSERVED), V(ExperimentStatus.FAIL, EvidenceClass.OBSERVED)]).Status);
        Assert.Equal(ExperimentStatus.INDETERMINATE, PhysicalEvaluator.Combine([V(ExperimentStatus.FAIL, EvidenceClass.OBSERVED), V(ExperimentStatus.INDETERMINATE, EvidenceClass.OBSERVED)]).Status);
        Assert.Equal(ExperimentStatus.NOT_RUN, PhysicalEvaluator.Combine([]).Status);
    }

    [Theory]
    [InlineData(8, 0, 0, 8, 0.0000777)]
    [InlineData(5, 0, 0, 5, 0.003968)]
    [InlineData(0, 5, 0, 5, 1.0)]
    [InlineData(3, 2, 2, 3, 0.5)]
    public void FisherExactMatchesReferenceValues(int a, int b, int c, int d, double expected)
    {
        Assert.Equal(expected, FisherExact.OneSidedGreater(a, b, c, d), 4);
    }

    [Fact]
    public void ScheduleIsBalancedAndSeeded()
    {
        var s1 = TrialSchedule.Build(["R2", "R3a"], 6, 6, 42);
        var s2 = TrialSchedule.Build(["R2", "R3a"], 6, 6, 42);
        Assert.Equal(18, s1.Count);
        Assert.Equal(6, s1.Count(t => t.Arm == "R2"));
        Assert.Equal(6, s1.Count(t => t.IsControl));
        Assert.Equal(s1.Select(t => t.Arm), s2.Select(t => t.Arm));
        Assert.Equal(Enumerable.Range(1, 18), s1.Select(t => t.Index));
    }

    [Fact]
    public void RunArtifactsAreImmutableAfterResult()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hapt-run-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var a = RunArtifacts.Create("EXP-020", dir);
            a.Trace(new { n = 1 });
            a.WriteResult(new ExperimentResult { RunId = a.RunId, ExperimentId = "EXP-020" });
            a.Trace(new { n = 2 });
            Assert.Single(File.ReadAllLines(Path.Combine(dir, "trace.jsonl")));
            Assert.Throws<InvalidOperationException>(() => a.WriteResult(new ExperimentResult { RunId = a.RunId, ExperimentId = "EXP-020" }));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void BaselineMustMatchWaveformAndIntensity()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hapt-baseline-" + Guid.NewGuid().ToString("N"));
        try
        {
            var r = new ExperimentResult
            {
                RunId = "b", ExperimentId = "EXP-010", Status = ExperimentStatus.PASS, EvidenceClass = EvidenceClass.PHYSICAL,
                Machine = new MachineInfo { MachineFingerprint = "me" },
                Routes = new()
                {
                    [$"{OutputRoutes.ForegroundManager}:Step"] = new ArmVerdict("Step", 6, 6, 0, 0, 6, 0, 6, 0, 1, 0, 0.001, 6, 0, 0, ExperimentStatus.PASS, EvidenceClass.PHYSICAL, [], ""),
                    [$"{OutputRoutes.ForegroundManager}:Click"] = new ArmVerdict("Click", 6, 1, 5, 0, 6, 0, 6, 0, 0.17, 0, 0.5, 6, 0, 0, ExperimentStatus.INDETERMINATE, EvidenceClass.OBSERVED, [], ""),
                },
            };
            r.Method["intensity"] = 0.8;
            r.Method["targetControllerIdHash"] = "target-controller";
            Directory.CreateDirectory(Path.Combine(dir, "b"));
            File.WriteAllText(Path.Combine(dir, "b", "result.json"), System.Text.Json.JsonSerializer.Serialize(r, HapticsJson.Indented));
            var results = EvidenceLedger.Load(dir);

            Assert.True(EvidenceLedger.ForegroundBaselinePassed(results, "me", "target-controller"));
            Assert.True(EvidenceLedger.ForegroundBaselinePassed(results, "me", "Step", 1.0, "target-controller"));
            Assert.False(EvidenceLedger.ForegroundBaselinePassed(results, "me", "Step", 0.5, "target-controller"));
            Assert.False(EvidenceLedger.ForegroundBaselinePassed(results, "me", "Click", 1.0, "target-controller"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LedgerOnlyUnlocksRoutesWithPhysicalPassOnSameMachine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hapt-ledger-" + Guid.NewGuid().ToString("N"));
        try
        {
            void Write(string fp, string route, ExperimentStatus s, EvidenceClass c, string name)
            {
                var r = new ExperimentResult
                {
                    RunId = name, ExperimentId = "EXP-020", StartedAtUtc = DateTimeOffset.UtcNow,
                    Machine = new MachineInfo { MachineFingerprint = fp },
                    Routes = new() { [route] = new ArmVerdict(route, 8, 8, 0, 0, 8, 0, 8, 0, 1, 0, 0.0001, 8, 0, 0, s, c, [], "") },
                };
                Directory.CreateDirectory(Path.Combine(dir, name));
                File.WriteAllText(Path.Combine(dir, name, "result.json"), System.Text.Json.JsonSerializer.Serialize(r, HapticsJson.Indented));
            }

            Write("me", OutputRoutes.RawThreadManager, ExperimentStatus.PASS, EvidenceClass.PHYSICAL, "a");
            Write("me", OutputRoutes.CachedController, ExperimentStatus.PASS, EvidenceClass.OBSERVED, "b");
            Write("other", OutputRoutes.OverrideOnRawThread, ExperimentStatus.PASS, EvidenceClass.PHYSICAL, "c");

            var results = EvidenceLedger.Load(dir);
            var routes = EvidenceLedger.VerifiedBackgroundRoutes(results, "me");
            Assert.Equal([OutputRoutes.RawThreadManager], routes);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
