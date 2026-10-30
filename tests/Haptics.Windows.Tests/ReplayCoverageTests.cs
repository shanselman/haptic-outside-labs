using System.Text.Json;
using Haptics.Core;
using Haptics.Lab.Experiments;

namespace Haptics.Windows.Tests;

public class ReplayCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"haptics-replay-{Guid.NewGuid():N}");
    private string Traces => Path.Combine(_root, "traces");

    public ReplayCoverageTests()
    {
        Directory.CreateDirectory(Traces);
        var repo = ArtifactLocator.FindRepoRoot(AppContext.BaseDirectory)!;
        foreach (var file in Directory.GetFiles(Path.Combine(repo, "tests", "traces"), "*.jsonl"))
        {
            File.Copy(file, Path.Combine(Traces, Path.GetFileName(file)));
        }
    }

    [Fact]
    public void EveryGoldenTraceHasAnExpectationAndPasses()
    {
        var result = Run();
        Assert.Equal(ExperimentStatus.PASS, result.Status);
        Assert.Equal(Exp070Replay.Expectations.Count,
            result.Assertions.Count(a => a.Name.EndsWith("haptics", StringComparison.Ordinal)));
        Assert.All(result.Assertions, a => Assert.True(a.Passed, $"{a.Name}: {a.Detail}"));
    }

    [Fact]
    public void UnlistedTraceFailsEvenWhenItEmitsNothing()
    {
        File.WriteAllText(Path.Combine(Traces, "unlisted.jsonl"), """{"type":"comment","text":"negative control"}""");
        var result = Run();
        Assert.Equal(ExperimentStatus.FAIL, result.Status);
        Assert.Contains(result.Assertions, a => !a.Passed && a.Name.Contains("unlisted.jsonl", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingExpectedTraceFailsEvenWhenRemainingTracesPass()
    {
        var missing = Exp070Replay.Expectations.Keys.First();
        File.Delete(Path.Combine(Traces, missing));
        var result = Run();
        Assert.Equal(ExperimentStatus.FAIL, result.Status);
        Assert.Contains(result.Assertions, a => !a.Passed && a.Name.Contains(missing, StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyTraceDirectoryFails()
    {
        foreach (var file in Directory.GetFiles(Traces, "*.jsonl"))
        {
            File.Delete(file);
        }

        Assert.Equal(ExperimentStatus.FAIL, Run().Status);
    }

    private ExperimentResult Run()
    {
        var resultPath = Exp070Replay.Run(Path.Combine(_root, "run"), Traces);
        return JsonSerializer.Deserialize<ExperimentResult>(File.ReadAllText(resultPath), HapticsJson.Compact)!;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
