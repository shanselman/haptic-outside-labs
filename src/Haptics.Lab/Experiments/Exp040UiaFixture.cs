using System.Diagnostics;
using System.Text.Json;
using Haptics.Core;
using Haptics.Windows;

namespace Haptics.Lab.Experiments;

/// <summary>
/// EXP-040: launch the fixture in its own process, then hit-test the centre of every known control through
/// the isolated UIA worker (no pointer movement needed: UIA accepts a screen point) and compare to truth.
/// </summary>
public static class Exp040UiaFixture
{
    public static string Run(string? outDir)
    {
        using var s = ExperimentSession.Start("EXP-040", "fixture", outDir);
        var r = s.Result;
        var truthPath = Path.Combine(s.Artifacts.RunDirectory, "fixture-truth.json");
        using var fixture = StartFixture(truthPath);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(truthPath) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }

            if (!File.Exists(truthPath))
            {
                r.Status = ExperimentStatus.BLOCKED;
                r.Explanation = "Fixture did not publish ground truth (no interactive desktop?).";
                return s.Complete();
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(truthPath));
            var fixturePid = doc.RootElement.GetProperty("pid").GetInt32();
            var items = doc.RootElement.GetProperty("items").Deserialize<List<FixtureTruthItem>>(HapticsJson.Indented)!;
            using var uia = UiaClient.ForCurrentProcess(new UiaClientOptions { TimeoutMs = 2000, IncludeAutomationId = true });

            // Warm-up (process start + first cross-process connection).
            uia.QueryAsync(items[0].Rect[0] + 2, items[0].Rect[1] + 2).Wait(5000);

            var rows = new List<Dictionary<string, object?>>();
            var lookups = new List<double>();
            var correct = 0;
            var wellFormed = 0;
            var occluded = 0;
            foreach (var item in items)
            {
                // Probe the centre first, then a few interior points, using the first one where the fixture is the top window.
                var probe = ProbePoints(item.Rect).FirstOrDefault(p => DesktopContext.TopLevelAt(p.X, p.Y).Pid == fixturePid);
                if (probe == default)
                {
                    occluded++;
                    var c = ProbePoints(item.Rect).First();
                    var top = DesktopContext.TopLevelAt(c.X, c.Y);
                    rows.Add(new Dictionary<string, object?> { ["key"] = item.Key, ["expected"] = item.ExpectedControlType, ["occluded"] = true, ["topPid"] = top.Pid, ["topName"] = DesktopContext.ProcessName(top.Pid), ["match"] = false });
                    continue;
                }

                var (cx, cy) = probe;
                var resp = uia.QueryAsync(cx, cy).GetAwaiter().GetResult();
                var snap = resp.Snapshot;
                lookups.Add(resp.LookupMs);
                var typeOk = snap?.ControlType == item.ExpectedControlType;
                var pidOk = snap?.ProcessId == fixturePid;
                var idOk = item.AutomationId is null || snap?.AutomationId == item.AutomationId;
                var boundsOk = snap?.Bounds is { } b && b.Contains(cx, cy);
                var enabledOk = snap is not null && snap.IsEnabled == item.ExpectEnabled;
                var passwordOk = snap is not null && snap.IsPassword == item.ExpectPassword;
                var interactiveReported = snap is not null && SemanticVocabulary.InteractiveTypes.Contains(snap.ControlType);
                var ok = item.Interactive
                    ? typeOk && pidOk && idOk && boundsOk && enabledOk
                    : !interactiveReported && passwordOk && pidOk;
                if (item.WellFormed)
                {
                    wellFormed++;
                    if (ok)
                    {
                        correct++;
                    }
                }

                rows.Add(new Dictionary<string, object?>
                {
                    ["key"] = item.Key, ["expected"] = item.ExpectedControlType, ["observed"] = snap?.ControlType,
                    ["framework"] = snap?.FrameworkId, ["parent"] = snap?.ParentControlType, ["typeOk"] = typeOk, ["pidOk"] = pidOk,
                    ["automationIdOk"] = idOk, ["boundsContainPoint"] = boundsOk, ["enabledOk"] = enabledOk, ["passwordOk"] = passwordOk,
                    ["range"] = snap?.Range, ["lookupMs"] = Math.Round(resp.LookupMs, 2), ["error"] = resp.Error ?? resp.ClientStatus, ["match"] = ok,
                });
                s.Artifacts.Trace(new { type = "uia-hit", item.Key, cx, cy, snapshot = snap, resp.LookupMs });
            }

            var accuracy = wellFormed == 0 ? 0 : (double)correct / wellFormed;
            lookups.Sort();
            r.Observations["occludedItems"] = occluded;
            r.Observations["matrix"] = rows;
            r.Observations["accuracy"] = Math.Round(accuracy, 4);
            r.Observations["correct"] = correct;
            r.Observations["wellFormed"] = wellFormed;
            r.Observations["uiaStats"] = uia.Stats;
            r.LatencyMs["uiaLookupP50"] = Pct(lookups, 0.5);
            r.LatencyMs["uiaLookupP95"] = Pct(lookups, 0.95);
            r.LatencyMs["uiaLookupMax"] = lookups.LastOrDefault();
            r.Method["worker"] = "Separate process, windowless MTA thread, CacheRequest (TreeScope.Element), one parent hop for group context.";
            r.Method["privacy"] = "No Name/Value/Text properties requested. AutomationId only requested for our own fixture.";
            r.NegativeControls.Add("opaque_canvas: custom-painted 'buttons' must NOT be reported as interactive.");
            r.NegativeControls.Add("txt_password: must be flagged IsPassword and never treated as a haptic target.");

            var opaque = rows.First(x => (string)x["key"]! == "opaque_canvas");
            var password = rows.First(x => (string)x["key"]! == "txt_password");
            if (occluded > items.Count / 4 || opaque.ContainsKey("occluded") || password.ContainsKey("occluded"))
            {
                r.Status = ExperimentStatus.INDETERMINATE;
                r.EvidenceClass = EvidenceClass.OBSERVED;
                r.Explanation = $"Fixture was covered by another window at {occluded}/{items.Count} probe points; cannot judge UIA accuracy. Close or move overlapping windows and rerun.";
                return s.Complete();
            }

            s.Assert("accuracy>=0.95 on well-formed fixture controls", accuracy >= 0.95, $"{correct}/{wellFormed}");
            s.Assert("opaque canvas not interactive", (bool)opaque["match"]!);
            s.Assert("password flagged", (bool)password["passwordOk"]!);
            s.Assert("lookup p95 <= 20ms (engineering target)", Pct(lookups, 0.95) <= 20, $"p95={Pct(lookups, 0.95):0.0}ms");

            r.Status = r.Assertions.All(a => a.Passed) ? ExperimentStatus.PASS : ExperimentStatus.FAIL;
            r.EvidenceClass = EvidenceClass.OBSERVED;
            r.Explanation = $"UIA identified {correct}/{wellFormed} fixture controls correctly; lookup p50={Pct(lookups, 0.5):0.0}ms p95={Pct(lookups, 0.95):0.0}ms.";
            r.Limitations.Add("Fixture is WinForms only (Win32 + MSAA proxies). WPF/WinUI/Chromium fixtures are future work (EXP-050/052).");
            r.Limitations.Add("Lookup latency is for our own lightweight fixture; real apps will differ (EXP-041).");
            r.NextDecision = "Use the Live Observer (dry-run) over real apps to start the EXP-041 coverage map.";
            return s.Complete();
        }
        finally
        {
            try
            {
                if (!fixture.HasExited)
                {
                    fixture.Kill();
                }
            }
            catch
            {
                // Already gone.
            }
        }
    }

    internal static Process StartFixture(string truthPath)
    {
        // ShellExecute (not a bare CreateProcess) so the fixture window is allowed to come to the front.
        var exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
        }

        psi.ArgumentList.Add("fixture");
        psi.ArgumentList.Add("--truth");
        psi.ArgumentList.Add(truthPath);
        return Process.Start(psi)!;
    }

    private static IEnumerable<(int X, int Y)> ProbePoints(int[] r)
    {
        foreach (var (fx, fy) in new[] { (0.5, 0.5), (0.25, 0.5), (0.75, 0.5), (0.5, 0.3), (0.5, 0.7) })
        {
            yield return (r[0] + (int)(r[2] * fx), r[1] + (int)(r[3] * fy));
        }
    }

    private static double Pct(List<double> sorted, double p) =>
        sorted.Count == 0 ? double.NaN : Math.Round(sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)], 2);
}
