using System.Text.Json;
using System.Text.Json.Nodes;
using Haptics.Core;

namespace Haptics.Core.Tests;

/// <summary>Keeps schemas/ in sync with the serialized contracts (lightweight structural check).</summary>
public class SchemaTests
{
    private static string Repo() => ArtifactLocator.FindRepoRoot(AppContext.BaseDirectory)!;

    [Fact]
    public void SerializedResultHasEveryRequiredSchemaProperty()
    {
        var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(Repo(), "schemas", "experiment-result.schema.json")))!;
        var required = schema["required"]!.AsArray().Select(n => (string)n!).ToList();
        var r = new ExperimentResult { RunId = "r", ExperimentId = "EXP-000" };
        r.Finalize();
        var json = JsonNode.Parse(JsonSerializer.Serialize(r, HapticsJson.Indented))!.AsObject();
        Assert.All(required, p => Assert.True(json.ContainsKey(p), $"missing {p}"));

        var props = schema["properties"]!.AsObject().Select(kv => kv.Key).ToHashSet();
        Assert.All(json.Select(kv => kv.Key), k => Assert.Contains(k, props));
    }

    [Fact]
    public void StatusAndClassEnumsMatchSchema()
    {
        var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(Repo(), "schemas", "experiment-result.schema.json")))!;
        var statuses = schema["properties"]!["status"]!["enum"]!.AsArray().Select(n => (string)n!).Order();
        var classes = schema["properties"]!["evidenceClass"]!["enum"]!.AsArray().Select(n => (string)n!).Order();
        Assert.Equal(Enum.GetNames<ExperimentStatus>().Order(), statuses);
        Assert.Equal(Enum.GetNames<EvidenceClass>().Order(), classes);
    }

    [Fact]
    public void EveryGoldenTraceLineHasAKnownType()
    {
        var known = new HashSet<string> { "comment", "process", "pointer", "element", "device", "foreground", "wheel", "tick", "coop" };
        foreach (var file in Directory.GetFiles(Path.Combine(Repo(), "tests", "traces"), "*.jsonl"))
        {
            foreach (var line in File.ReadLines(file).Where(l => l.Trim().Length > 0))
            {
                Assert.Contains((string)JsonNode.Parse(line)!["type"]!, known);
            }
        }
    }
}
