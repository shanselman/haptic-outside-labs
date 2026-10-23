using Haptics.Core;
using Haptics.Windows;

namespace Haptics.Lab;

/// <summary>Creates a run folder and a result pre-populated with machine/provenance data.</summary>
public sealed class ExperimentSession : IDisposable
{
    private ExperimentSession(ExperimentInfo info, RunArtifacts artifacts, string mode)
    {
        Info = info;
        Artifacts = artifacts;
        Result = new ExperimentResult
        {
            RunId = artifacts.RunId,
            ExperimentId = info.Id,
            HypothesisIds = info.Hypotheses,
            RequiresPhysicalConfirmation = info.Id is "EXP-010" or "EXP-020",
            StartedAtUtc = DateTimeOffset.UtcNow,
            Machine = EnvironmentProbe.Capture(),
            CodeCommit = EnvironmentProbe.CodeCommit(),
            Mode = mode,
        };
    }

    public ExperimentInfo Info { get; }

    public RunArtifacts Artifacts { get; }

    public ExperimentResult Result { get; }

    public static ExperimentSession Start(string experimentId, string mode, string? outDir)
    {
        var info = ExperimentCatalog.Find(experimentId) ?? throw new ArgumentException($"Unknown experiment {experimentId}");
        return new ExperimentSession(info, RunArtifacts.Create(info.Id, outDir), mode);
    }

    public void Assert(string name, bool passed, string? detail = null) =>
        Result.Assertions.Add(new Assertion { Name = name, Passed = passed, Detail = detail });

    /// <summary>Finalize, write result.json + summary.md, and return the result path.</summary>
    public string Complete()
    {
        Result.EndedAtUtc = DateTimeOffset.UtcNow;
        if (Result.Status == ExperimentStatus.NOT_RUN)
        {
            Result.Status = ExperimentStatus.INDETERMINATE;
        }

        Result.Finalize();
        Artifacts.WriteText("summary.md", Report.RunSummaryMarkdown(Result));
        return Artifacts.WriteResult(Result);
    }

    public void Dispose() => Artifacts.Dispose();
}

public sealed class CliOptions
{
    private readonly Dictionary<string, string?> _named = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positional { get; } = new();

    public static CliOptions Parse(IEnumerable<string> args)
    {
        var o = new CliOptions();
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var key = a[2..];
                string? value = null;
                var eq = key.IndexOf('=');
                if (eq >= 0)
                {
                    value = key[(eq + 1)..];
                    key = key[..eq];
                }
                else if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = list[++i];
                }

                o._named[key] = value ?? "true";
            }
            else
            {
                o.Positional.Add(a);
            }
        }

        return o;
    }

    public string? Get(string key) => _named.TryGetValue(key, out var v) ? v : null;

    public bool Flag(string key) => _named.TryGetValue(key, out var v) && v is not "false";

    public int Int(string key, int fallback) => int.TryParse(Get(key), out var v) ? v : fallback;

    public double Double(string key, double fallback) =>
        double.TryParse(Get(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
