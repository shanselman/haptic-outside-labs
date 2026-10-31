using Haptics.Core;

namespace Haptics.Lab.Experiments;

/// <summary>EXP-070 (replay mode): run every golden trace through the real interpreter + policy.</summary>
public static class Exp070Replay
{
    /// <summary>Expected haptic counts per authored trace. Changing a rule must show up here as a diff.</summary>
    public static readonly IReadOnlyDictionary<string, (int Min, int Max)> Expectations = new Dictionary<string, (int, int)>
    {
        ["toolbar-slow-sweep.jsonl"] = (5, 5),
        ["toolbar-pixel-crawl.jsonl"] = (2, 2),
        ["toolbar-fast-flick.jsonl"] = (0, 1),
        ["boundary-jitter.jsonl"] = (1, 1),
        ["same-element-stationary.jsonl"] = (1, 1),
        ["menu-nested.jsonl"] = (3, 3),
        ["slider-discrete.jsonl"] = (10, 10),
        ["scroll-at-limit.jsonl"] = (0, 0),
        ["uia-stale-late.jsonl"] = (0, 0),
        ["device-disconnect.jsonl"] = (1, 1),
        ["native-duplicate-suppression.jsonl"] = (0, 0),
        ["security-password.jsonl"] = (0, 0),
        ["excluded-app.jsonl"] = (0, 0),
        ["generation-stale-context.jsonl"] = (1, 1),
        ["device-change-pending.jsonl"] = (0, 0),
        ["foreground-change-stationary.jsonl"] = (1, 1),
    };

    public static string Run(string? outDir, string? traceDir)
    {
        using var s = ExperimentSession.Start("EXP-070", "replay", outDir);
        var r = s.Result;
        traceDir ??= FindTraceDir();
        if (traceDir is null || !Directory.Exists(traceDir))
        {
            r.Status = ExperimentStatus.BLOCKED;
            r.Explanation = "tests/traces not found (run from the repository or pass --trace <dir>).";
            return s.Complete();
        }

        var files = Directory.GetFiles(traceDir, "*.jsonl").OrderBy(f => f).ToArray();
        var names = files.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        foreach (var missing in Expectations.Keys.Where(name => !names.Contains(name)))
        {
            s.Assert($"{missing}: expected trace exists", false, "expectation has no trace");
        }

        var rows = new List<Dictionary<string, object?>>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var output = TraceReplay.Run(file);
            var reasons = output.Decisions.GroupBy(d => d.Reason).ToDictionary(g => g.Key, g => (object?)g.Count());
            rows.Add(new() { ["trace"] = name, ["emitted"] = output.Emitted, ["waveforms"] = string.Join(",", output.Haptic.Select(h => h.Waveform)), ["reasons"] = reasons });
            if (Expectations.TryGetValue(name, out var exp))
            {
                s.Assert($"{name}: {exp.Min}..{exp.Max} haptics", output.Emitted >= exp.Min && output.Emitted <= exp.Max, $"emitted {output.Emitted}");
            }
            else
            {
                s.Assert($"{name}: trace has expectation", false, "trace has no expectation");
            }

            foreach (var d in output.Decisions)
            {
                s.Artifacts.Trace(new { trace = name, d.T, d.EventKind, d.ControlType, d.Outcome, d.Reason, d.Waveform });
            }
        }

        r.Observations["traces"] = rows;
        r.Status = r.Assertions.Count > 0 && r.Assertions.All(a => a.Passed) ? ExperimentStatus.PASS : ExperimentStatus.FAIL;
        r.EvidenceClass = EvidenceClass.OBSERVED;
        r.Explanation = $"{r.Assertions.Count(a => a.Passed)}/{r.Assertions.Count} golden trace checks passed (coverage and haptic decisions; logic only, no hardware).";
        r.AddLabel(EvidenceLabels.NotTestedOnHardware);
        r.Limitations.Add("Replay validates rules, not OS delivery, UIA timing, or tactile perception.");
        return s.Complete();
    }

    private static string? FindTraceDir()
    {
        var repo = ArtifactLocator.FindRepoRoot(AppContext.BaseDirectory) ?? ArtifactLocator.FindRepoRoot(Environment.CurrentDirectory);
        return repo is null ? null : Path.Combine(repo, "tests", "traces");
    }
}
