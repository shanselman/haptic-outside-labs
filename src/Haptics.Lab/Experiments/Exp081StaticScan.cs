using Haptics.Core;

namespace Haptics.Lab.Experiments;

/// <summary>EXP-081 (static part): production assemblies must not import input/focus/injection/automation-action APIs.</summary>
public static class Exp081StaticScan
{
    public static string Run(string? outDir)
    {
        using var s = ExperimentSession.Start("EXP-081", "auto", outDir);
        var r = s.Result;
        var dir = AppContext.BaseDirectory;
        var targets = new[] { "Haptics.Core.dll", "Haptics.Windows.dll", "haptics-lab.dll" }
            .Select(f => Path.Combine(dir, f))
            .Concat(FindTray())
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var all = new List<ApiViolation>();
        foreach (var t in targets)
        {
            var v = ForbiddenApiScanner.Scan(t);
            all.AddRange(v);
            s.Assert($"{Path.GetFileName(t)} has no forbidden APIs", v.Count == 0, v.Count == 0 ? null : string.Join("; ", v.Select(x => x.Name)));
        }

        r.Observations["scanned"] = targets.Select(Path.GetFileName).ToList();
        r.Observations["violations"] = all;
        r.Observations["forbiddenImports"] = ForbiddenApiScanner.ForbiddenImports.Order().ToList();
        r.Observations["forbiddenTypes"] = ForbiddenApiScanner.ForbiddenTypes.Order().ToList();
        r.Status = targets.Count > 0 && all.Count == 0 ? ExperimentStatus.PASS : ExperimentStatus.FAIL;
        r.EvidenceClass = EvidenceClass.OBSERVED;
        r.Explanation = all.Count == 0
            ? $"{targets.Count} assemblies scanned; no pointer/keyboard synthesis, focus, hook, injection, settings, or UIA action APIs referenced."
            : $"{all.Count} forbidden API references found.";
        r.Limitations.Add("Static metadata scan: catches direct P/Invokes and type references, not reflection/dynamic invocation.");
        r.Limitations.Add("Runtime privacy checks (password suppression, exclusions) are covered by replay traces and unit tests.");
        return s.Complete();
    }

    private static IEnumerable<string> FindTray()
    {
        var repo = ArtifactLocator.FindRepoRoot(AppContext.BaseDirectory);
        if (repo is null)
        {
            return [];
        }

        var bin = Path.Combine(repo, "src", "Haptics.Tray", "bin");
        return Directory.Exists(bin) ? Directory.GetFiles(bin, "haptics-tray.dll", SearchOption.AllDirectories) : [];
    }
}
