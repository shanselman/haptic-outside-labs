using System.Net;
using System.Text;
using Haptics.Core;
using Haptics.Windows;

namespace Haptics.Lab;

public static class Report
{
    public static string RunSummaryMarkdown(ExperimentResult r)
    {
        var sb = new StringBuilder();
        var info = ExperimentCatalog.Find(r.ExperimentId);
        sb.AppendLine($"# {r.ExperimentId} — {info?.Title}");
        sb.AppendLine();
        sb.AppendLine($"- **Status:** `{r.Status}`  **Evidence class:** `{r.EvidenceClass}`");
        if (r.Labels.Count > 0)
        {
            sb.AppendLine($"- **Labels:** {string.Join(", ", r.Labels.Select(l => $"`{l}`"))}");
        }

        sb.AppendLine($"- **Run:** `{r.RunId}`  **Commit:** `{r.CodeCommit}`  **Mode:** {r.Mode}");
        sb.AppendLine($"- **Machine:** {r.Machine.DeviceModel} · Windows {r.Machine.OsDisplayVersion} build {r.Machine.OsBuild} · {r.Machine.OsArchitecture} (process {r.Machine.ProcessArchitecture}) · fingerprint `{r.Machine.MachineFingerprint}`");
        sb.AppendLine();
        sb.AppendLine(r.Explanation);
        if (r.Routes is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("| Arm / route | Status | Class | Felt (pulses) | Felt (controls) | Fisher p | API accepted | Reason |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var (k, v) in r.Routes)
            {
                sb.AppendLine($"| `{k}` | {v.Status} | {v.EvidenceClass} | {v.Hits}/{v.Hits + v.Misses} | {v.FalseAlarms}/{v.FalseAlarms + v.CorrectRejections} | {v.FisherP:0.####} | {v.ApiAccepted} | {v.Reason} |");
            }
        }

        if (r.Assertions.Count > 0)
        {
            sb.AppendLine();
            foreach (var a in r.Assertions)
            {
                sb.AppendLine($"- [{(a.Passed ? "x" : " ")}] {a.Name}{(a.Detail is null ? "" : $" — {a.Detail}")}");
            }
        }

        var lat = r.LatencyMs.Where(kv => kv.Value is not null).ToList();
        if (lat.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Latency (ms): " + string.Join(", ", lat.Select(kv => $"{kv.Key}={kv.Value:0.##}")));
        }

        if (r.Limitations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Limitations:");
            foreach (var l in r.Limitations)
            {
                sb.AppendLine($"- {l}");
            }
        }

        if (r.NextDecision is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Next: {r.NextDecision}");
        }

        return sb.ToString();
    }

    /// <summary>A compact block to paste into docs/EVIDENCE_LEDGER.md (promotion stays a human decision).</summary>
    public static string LedgerEntry(ExperimentResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### {r.EndedAtUtc:yyyy-MM-dd} · {r.ExperimentId} · `{r.Status}` [{r.EvidenceClass}]");
        sb.AppendLine();
        sb.AppendLine($"- Run `{r.RunId}`, commit `{r.CodeCommit}`, machine `{r.Machine.MachineFingerprint}` ({r.Machine.DeviceModel}, build {r.Machine.OsBuild}, {r.Machine.ProcessArchitecture})");
        if (r.Method.TryGetValue("targetRawDevice", out var dev) && dev is not null)
        {
            sb.AppendLine($"- Target device: {dev}");
        }

        sb.AppendLine($"- {r.Explanation}");
        if (r.Routes is { Count: > 0 })
        {
            foreach (var (k, v) in r.Routes)
            {
                sb.AppendLine($"  - `{k}`: {v.Status} — felt {v.Hits}/{v.Hits + v.Misses}, controls {v.FalseAlarms}/{v.FalseAlarms + v.CorrectRejections}, p={v.FisherP:0.####}");
            }
        }

        sb.AppendLine($"- Labels: {string.Join(", ", r.Labels)}");
        sb.AppendLine("- Promotion decision: _(human: does this change a hypothesis state in docs/HYPOTHESES.yaml? why?)_");
        return sb.ToString();
    }

    public static (string Markdown, string Html) Write()
    {
        var results = EvidenceLedger.Load();
        var fp = EnvironmentProbe.CurrentFingerprint();
        var gates = EvidenceLedger.Gates(results, fp);
        var physical = results.Any(r => r.Result.EvidenceClass == EvidenceClass.PHYSICAL);
        var md = new StringBuilder();
        md.AppendLine("# Haptics Lab report");
        md.AppendLine();
        md.AppendLine($"Generated {DateTimeOffset.Now:yyyy-MM-dd HH:mm} for machine `{fp}`. {results.Count} local runs.");
        if (!physical)
        {
            md.AppendLine();
            md.AppendLine("> **NOT_TESTED_ON_HARDWARE** — no run on any machine has PHYSICAL evidence yet. Nothing below proves a mouse buzzed.");
        }

        md.AppendLine();
        md.AppendLine("## Gates");
        md.AppendLine();
        md.AppendLine("| Gate | State | Detail |");
        md.AppendLine("|---|---|---|");
        foreach (var g in gates)
        {
            md.AppendLine($"| {g.Gate} | **{g.State}** | {g.Detail} |");
        }

        md.AppendLine();
        md.AppendLine("## Latest result per experiment (this machine)");
        md.AppendLine();
        md.AppendLine("| Experiment | Status | Class | When (UTC) | Explanation |");
        md.AppendLine("|---|---|---|---|---|");
        foreach (var e in ExperimentCatalog.All)
        {
            var l = EvidenceLedger.Latest(results, e.Id, fp);
            md.AppendLine(l is null
                ? $"| {e.Id} {e.Title} | NOT_RUN | — | — | {(e.Implemented ? "implemented" : "not implemented")} |"
                : $"| {e.Id} {e.Title} | {l.Result.Status} | {l.Result.EvidenceClass} | {l.Result.EndedAtUtc:yyyy-MM-dd HH:mm} | {l.Result.Explanation.Replace("|", "/")} |");
        }

        var root = ArtifactLocator.Root();
        Directory.CreateDirectory(root);
        var mdPath = Path.Combine(root, "report.md");
        File.WriteAllText(mdPath, md.ToString());

        var html = new StringBuilder();
        html.AppendLine("<!doctype html><meta charset=utf-8><title>Haptics Lab report</title>");
        html.AppendLine("<style>body{font:14px Segoe UI,sans-serif;margin:2em;max-width:1200px}table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:4px 8px;vertical-align:top}.PASS{color:#107c10}.FAIL{color:#c42b1c}.INDETERMINATE{color:#005a9e}.BLOCKED{color:#8a5000}.banner{background:#fff4ce;padding:8px;border:1px solid #e0c060}</style>");
        html.AppendLine($"<h1>Haptics Lab report</h1><p>Machine <code>{fp}</code>, {results.Count} runs.</p>");
        if (!physical)
        {
            html.AppendLine("<p class=banner><b>NOT_TESTED_ON_HARDWARE</b> — no PHYSICAL evidence yet.</p>");
        }

        html.AppendLine("<h2>Gates</h2><table><tr><th>Gate<th>State<th>Detail");
        foreach (var g in gates)
        {
            html.AppendLine($"<tr><td>{E(g.Gate)}<td><b>{E(g.State)}</b><td>{E(g.Detail)}");
        }

        html.AppendLine("</table><h2>Runs</h2><table><tr><th>When<th>Experiment<th>Status<th>Class<th>Explanation");
        foreach (var l in results.AsEnumerable().Reverse())
        {
            html.AppendLine($"<tr><td>{l.Result.EndedAtUtc:yyyy-MM-dd HH:mm}<td>{E(l.Result.ExperimentId)}<td class={l.Result.Status}>{l.Result.Status}<td>{l.Result.EvidenceClass}<td>{E(l.Result.Explanation)}");
        }

        html.AppendLine("</table>");
        var htmlPath = Path.Combine(root, "report.html");
        File.WriteAllText(htmlPath, html.ToString());
        return (mdPath, htmlPath);

        static string E(string s) => WebUtility.HtmlEncode(s);
    }
}
