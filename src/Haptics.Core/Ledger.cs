using System.Text.Json;

namespace Haptics.Core;

public static class OutputRoutes
{
    public const string ForegroundManager = "R1-foreground-manager";
    public const string RawThreadManager = "R2-raw-thread-manager";
    public const string CachedController = "R3a-cached-controller";
    public const string OverrideOnRawThread = "R3b-override-on-raw-thread";
    public const string DryRun = "dry-run";

    public static readonly IReadOnlyList<string> Background = [RawThreadManager, CachedController, OverrideOnRawThread];

    public static string Describe(string route) => route switch
    {
        ForegroundManager => "InputHapticsManager.GetForCurrentThread() on our focused window's UI thread (documented pattern).",
        RawThreadManager => "InputHapticsManager.GetForCurrentThread() on the thread that receives background WM_INPUT (RIDEV_INPUTSINK).",
        CachedController => "SimpleHapticsController captured from CurrentHapticsController during a foreground interaction, later used directly.",
        OverrideOnRawThread => "SetOverrideHapticsController(captured controller) on the raw-input thread's manager, send, then clear.",
        DryRun => "No hardware. Logs intent only. Never evidence of haptics.",
        _ => route,
    };
}

public sealed record ExperimentInfo(
    string Id,
    string Title,
    string Mode,
    string[] DependsOn,
    string[] Hypotheses,
    bool Implemented,
    bool NeedsHuman,
    string Summary);

public static class ExperimentCatalog
{
    public static readonly IReadOnlyList<ExperimentInfo> All =
    [
        new("EXP-000", "Runtime and build inventory (doctor)", "auto", [], ["H1"], true, false,
            "OS/SDK/API guards, IsSupported, IsHapticDevicePresent, waveform ids, raw input devices."),
        new("EXP-010", "Foreground physical haptic baseline", "physical", ["EXP-000"], ["H1"], true, true,
            "Our focused window sends waveforms on its UI thread; blinded pulse/no-pulse trials rated by the operator."),
        new("EXP-020", "Background Raw Input output route (critical gate, includes EXP-022 arms)", "physical", ["EXP-010"], ["H1", "H2"], true, true,
            "Another app is foreground; routes R2/R3a/R3b vs no-op controls, fired from background physical WM_INPUT."),
        new("EXP-021", "Input recency and thread timing", "physical", ["EXP-020"], ["H1"], false, true, "Not implemented yet."),
        new("EXP-022", "SetOverrideHapticsController within process", "physical", ["EXP-010"], ["H2"], true, true,
            "Folded into the EXP-020 session as arms R3a (cached controller) and R3b (override)."),
        new("EXP-023", "TryGetForThread ownership and safety", "auto", ["EXP-000"], ["H2"], true, false,
            "Own-process threads only: lookup, cross-thread property access, TryStopFeedback affinity."),
        new("EXP-030", "Passive raw input and device attribution", "physical", ["EXP-000"], ["H1"], true, true,
            "Counts background WM_INPUT per device while another app is foreground; callback cost."),
        new("EXP-031", "Synthetic vs physical input audit", "fixture", ["EXP-030"], ["H6"], false, false, "Not implemented (no SendInput anywhere yet)."),
        new("EXP-040", "UIA hit-test accuracy and cost (fixture)", "fixture", ["EXP-000"], ["H3"], true, false,
            "Isolated MTA UIA worker process hit-tests our fixture's controls and compares against fixture truth."),
        new("EXP-041", "UIA across real apps", "physical", ["EXP-040"], ["H4"], true, true,
            "Use the Lab's Live Observer in dry-run over real apps; coverage notes recorded by the operator."),
        new("EXP-042", "UIA/WinEvent reliability", "fixture", ["EXP-040"], ["H3"], false, false, "Not implemented yet."),
        new("EXP-043", "UIA hang/crash containment", "auto", ["EXP-040"], ["H3"], true, false,
            "Fault-injected worker hangs/crashes; client must time out, restart, and never deliver a late result."),
        new("EXP-050", "Chromium/Edge semantics", "fixture", ["EXP-040"], ["H4"], false, true, "fixtures/browser/index.html exists for manual use."),
        new("EXP-051", "Electron semantics", "physical", ["EXP-040"], ["H4"], false, true, "Not implemented yet."),
        new("EXP-052", "WebView2 semantics", "fixture", ["EXP-040"], ["H4"], false, false, "Not implemented yet."),
        new("EXP-060", "DPI / multi-monitor alignment", "physical", ["EXP-030", "EXP-040"], ["H3"], false, true, "Not implemented yet."),
        new("EXP-070", "Generic toolbar/ribbon hover (replay + live)", "replay", ["EXP-040"], ["H5"], true, false,
            "Golden traces through the real interpreter+policy; live physical use via Lab Live Observer once a route passes."),
        new("EXP-071", "Menu/list/tab boundaries", "replay", ["EXP-040"], ["H5"], false, false, "Partially covered by group-boundary rule tests."),
        new("EXP-072", "Slider detents", "replay", ["EXP-040"], ["H5"], false, true, "Rule implemented; fixture slider available; formal experiment pending."),
        new("EXP-073", "Drag and drop candidates", "fixture", ["EXP-040"], ["H5"], false, true, "Deliberately not implemented (truthfulness risk)."),
        new("EXP-074", "Scroll policy", "fixture", ["EXP-040"], ["H5"], false, true, "Scroll texture is OFF by default; not implemented."),
        new("EXP-075", "Native haptics double-buzz suppression", "physical", ["EXP-010"], ["H7"], false, true, "Suppression list exists; A/B pending."),
        new("EXP-080", "Latency, CPU, memory", "physical", ["EXP-020"], ["H5"], false, true, "Live Observer reports p50/p95 lookup and call latency."),
        new("EXP-081", "Privacy/security static scan", "auto", [], ["H8"], true, false,
            "Scans production assemblies' P/Invoke imports and type references for forbidden input/automation APIs."),
        new("EXP-082", "Fault tolerance / power events", "physical", ["EXP-010", "EXP-030"], ["H5"], false, true, "Not implemented yet."),
        new("EXP-090", "Human usefulness A/B (blind style comparison)", "physical", ["EXP-020"], ["H8"], true, true,
            "Showcase → Compare styles: Quiet / Focused / Everything in random 60 s blocks, rated helpful/annoying."),
    ];

    public static ExperimentInfo? Find(string id) =>
        All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
}

public sealed record LoadedResult(string Directory, ExperimentResult Result);

public sealed record GateStatus(string Gate, string State, string Detail);

/// <summary>Reads local evidence and derives gate states. Evidence is tied to a machine fingerprint.</summary>
public static class EvidenceLedger
{
    public static IReadOnlyList<LoadedResult> Load(string? runsRoot = null)
    {
        runsRoot ??= ArtifactLocator.RunsRoot();
        if (!Directory.Exists(runsRoot))
        {
            return [];
        }

        var list = new List<LoadedResult>();
        foreach (var file in Directory.EnumerateFiles(runsRoot, "result.json", SearchOption.AllDirectories))
        {
            try
            {
                var r = JsonSerializer.Deserialize<ExperimentResult>(File.ReadAllText(file), HapticsJson.Indented);
                if (r is not null && !r.Example)
                {
                    list.Add(new LoadedResult(Path.GetDirectoryName(file)!, r));
                }
            }
            catch (JsonException)
            {
                // Corrupt or foreign file: ignore rather than let it unlock anything.
            }
        }

        return list.OrderBy(r => r.Result.StartedAtUtc ?? DateTimeOffset.MinValue).ToList();
    }

    public static LoadedResult? Latest(IEnumerable<LoadedResult> results, string experimentId, string? fingerprint = null) =>
        results.Where(r => r.Result.ExperimentId == experimentId && (fingerprint is null || r.Result.Machine.MachineFingerprint == fingerprint))
            .LastOrDefault();

    public static bool ForegroundBaselinePassed(IEnumerable<LoadedResult> results, string fingerprint) =>
        results.Any(r => r.Result.ExperimentId == "EXP-010" && r.Result.Machine.MachineFingerprint == fingerprint &&
                         r.Result.Status == ExperimentStatus.PASS && r.Result.EvidenceClass == EvidenceClass.PHYSICAL);

    /// <summary>
    /// Stricter baseline for scoring a background "not felt" as FAIL: EXP-010 must have physically passed for the
    /// same waveform at an intensity no higher than the one being tested.
    /// </summary>
    public static bool ForegroundBaselinePassed(IEnumerable<LoadedResult> results, string fingerprint, string waveform, double intensity) =>
        results.Any(r => r.Result.ExperimentId == "EXP-010" && r.Result.Machine.MachineFingerprint == fingerprint &&
                         r.Result.Routes is { } routes &&
                         routes.TryGetValue($"{OutputRoutes.ForegroundManager}:{waveform}", out var v) &&
                         v.Status == ExperimentStatus.PASS && v.EvidenceClass == EvidenceClass.PHYSICAL &&
                         MethodDouble(r.Result.Method, "intensity") is { } i && i <= intensity + 1e-9);

    private static double? MethodDouble(Dictionary<string, object?> method, string key) =>
        method.TryGetValue(key, out var o) ? o switch
        {
            double d => d,
            JsonElement { ValueKind: JsonValueKind.Number } e => e.GetDouble(),
            _ => null,
        } : null;

    /// <summary>Background routes that have a physically confirmed PASS on this machine (most recent verdict wins).</summary>
    public static IReadOnlyList<string> VerifiedBackgroundRoutes(IEnumerable<LoadedResult> results, string fingerprint)
    {
        var latestByRoute = new Dictionary<string, ArmVerdict>();
        foreach (var r in results.Where(r => r.Result.ExperimentId == "EXP-020" && r.Result.Machine.MachineFingerprint == fingerprint))
        {
            foreach (var (route, verdict) in r.Result.Routes ?? new Dictionary<string, ArmVerdict>())
            {
                if (verdict.Status is ExperimentStatus.PASS or ExperimentStatus.FAIL)
                {
                    latestByRoute[route] = verdict;
                }
            }
        }

        return latestByRoute.Where(kv => kv.Value.Status == ExperimentStatus.PASS && kv.Value.EvidenceClass == EvidenceClass.PHYSICAL)
            .Select(kv => kv.Key)
            .OrderBy(r => OutputRoutes.Background.ToList().IndexOf(r))
            .ToList();
    }

    public static IReadOnlyList<GateStatus> Gates(IReadOnlyList<LoadedResult> results, string fingerprint)
    {
        var gates = new List<GateStatus>();
        var fg = Latest(results, "EXP-010", fingerprint);
        gates.Add(new GateStatus("A1 Foreground actuation", fg is null ? "Unverified" : fg.Result.Status.ToString(),
            fg?.Result.Explanation ?? "Run EXP-010 with the target mouse."));

        var routes = VerifiedBackgroundRoutes(results, fingerprint);
        var bg = Latest(results, "EXP-020", fingerprint);
        var bgState = routes.Count > 0 ? "Background confirmed" : bg is null ? (fg?.Result.Status == ExperimentStatus.PASS ? "Foreground only" : "Unverified") : bg.Result.Status.ToString();
        gates.Add(new GateStatus("A2 Background actuation (another app foreground)", bgState,
            routes.Count > 0 ? $"Verified routes: {string.Join(", ", routes)}" : bg?.Result.Explanation ?? "Run EXP-020 after EXP-010."));

        var raw = Latest(results, "EXP-030", fingerprint);
        var uia = Latest(results, "EXP-040", fingerprint);
        gates.Add(new GateStatus("B Observation (raw input + UIA)",
            $"raw={raw?.Result.Status.ToString() ?? "NOT_RUN"}, uia={uia?.Result.Status.ToString() ?? "NOT_RUN"}",
            "EXP-030 needs a human moving the mouse; EXP-040 is automated against the fixture."));
        gates.Add(new GateStatus("C Composition (latency, duplicates, cost)", "NOT_RUN", "Needs a passing A2 route plus EXP-070/075/080."));
        gates.Add(new GateStatus("D Value (human A/B)", "NOT_RUN", "EXP-090 after C."));
        return gates;
    }

    public static ExperimentInfo? NextExperiment(IReadOnlyList<LoadedResult> results, string fingerprint)
    {
        foreach (var e in ExperimentCatalog.All.Where(e => e.Implemented && e.Id != "EXP-022" && e.Id != "EXP-041"))
        {
            var latest = Latest(results, e.Id, fingerprint);
            if (latest?.Result.Status == ExperimentStatus.PASS)
            {
                continue;
            }

            return e;
        }

        return null;
    }
}
