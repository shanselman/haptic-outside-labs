using System.Text.Json;

namespace Haptics.Core.Tests;

public class DeviceBaselineTests
{
    [Fact]
    public void MissingIdentityCannotPassTheForegroundBaseline()
    {
        var result = new ExperimentResult
        {
            RunId = "legacy",
            ExperimentId = "EXP-010",
            Machine = new MachineInfo { MachineFingerprint = "machine" },
            Status = ExperimentStatus.PASS,
            EvidenceClass = EvidenceClass.PHYSICAL,
            Method = new() { ["intensity"] = 0.8 },
            Routes = new()
            {
                [$"{OutputRoutes.ForegroundManager}:Step"] = new ArmVerdict("Step", 6, 6, 0, 0, 6, 0, 6, 0, 1, 0, 0.001, 6, 0, 0,
                    ExperimentStatus.PASS, EvidenceClass.PHYSICAL, [], ""),
            },
        };
        Assert.False(EvidenceLedger.ForegroundBaselinePassed([new LoadedResult("legacy", result)], "machine", "Step", 1.0));
    }

    [Theory]
    [InlineData("controller-a", "controller-a", "machine", "Step", 1.0, true)]
    [InlineData("controller-a", "controller-b", "machine", "Step", 1.0, false)]
    [InlineData(null, "controller-a", "machine", "Step", 1.0, false)]
    [InlineData("controller-a", null, "machine", "Step", 1.0, false)]
    [InlineData("", "", "machine", "Step", 1.0, false)]
    [InlineData("none", "none", "machine", "Step", 1.0, false)]
    [InlineData("UNKNOWN", "UNKNOWN", "machine", "Step", 1.0, false)]
    [InlineData(" ", " ", "machine", "Step", 1.0, false)]
    [InlineData("controller-a", "controller-a", "other-machine", "Step", 1.0, false)]
    [InlineData("controller-a", "controller-a", "machine", "Click", 1.0, false)]
    [InlineData("controller-a", "controller-a", "machine", "Step", 0.5, false)]
    public void BaselineMatchesControllerMachineWaveformAndIntensity(string? recorded, string? target,
        string machine, string waveform, double intensity, bool expected)
    {
        var result = Baseline();
        result.Method["targetControllerIdHash"] = recorded;
        var loaded = new LoadedResult("run", result);
        Assert.Equal(expected, EvidenceLedger.ForegroundBaselinePassed([loaded], machine, waveform, intensity, target));

        var roundTrip = JsonSerializer.Deserialize<ExperimentResult>(JsonSerializer.Serialize(result, HapticsJson.Compact),
            HapticsJson.Compact)!;
        Assert.Equal(expected, EvidenceLedger.ForegroundBaselinePassed([new LoadedResult("run", roundTrip)],
            machine, waveform, intensity, target));
    }

    [Fact]
    public void LoadingLegacyEvidenceDoesNotRewriteItOrGrantABaseline()
    {
        var root = Path.Combine(Path.GetTempPath(), $"haptics-legacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "result.json");
            var original = JsonSerializer.Serialize(Baseline(), HapticsJson.Indented);
            File.WriteAllText(file, original);
            var results = EvidenceLedger.Load(root);
            Assert.Equal(ExperimentStatus.PASS, Assert.Single(results).Result.Status);
            Assert.False(EvidenceLedger.ForegroundBaselinePassed(results, "machine", "controller-a"));
            Assert.False(EvidenceLedger.ForegroundBaselinePassed(results, "machine", "Step", 1.0, "controller-a"));
            Assert.Equal(original, File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ExperimentResult Baseline() => new()
    {
        RunId = "baseline",
        ExperimentId = "EXP-010",
        Machine = new MachineInfo { MachineFingerprint = "machine" },
        Status = ExperimentStatus.PASS,
        EvidenceClass = EvidenceClass.PHYSICAL,
        Method = new() { ["intensity"] = 0.8 },
        Routes = new()
        {
            [$"{OutputRoutes.ForegroundManager}:Step"] = new ArmVerdict("Step", 6, 6, 0, 0, 6, 0, 6, 0, 1, 0, 0.001, 6, 0, 0,
                ExperimentStatus.PASS, EvidenceClass.PHYSICAL, [], ""),
        },
    };
}
