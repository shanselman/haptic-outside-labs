using System.Text.Json;
using System.Text.Json.Nodes;

namespace Haptics.Core;

public interface IClock
{
    double NowMs { get; }
}

public sealed class MonotonicClock : IClock
{
    public static readonly MonotonicClock Instance = new();

    public double NowMs => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
}

public sealed class FakeClock : IClock
{
    public double NowMs { get; set; }
}

/// <summary>
/// Privacy-safe authored traces (JSONL). One object per line with a "type" of
/// pointer | element | process | device | foreground | wheel | coop | tick | comment. See tests/traces/README.md.
/// </summary>
public static class TraceReplay
{
    public sealed record ReplayDecision(double T, string EventKind, string ControlType, PolicyOutcome Outcome, string Reason, string? Waveform);

    public sealed record ReplayDeviceChange(double T, long Handle, bool Arrived);

    public sealed record ReplayOutput(IReadOnlyList<ReplayDecision> Decisions, IReadOnlyList<string> Notes)
    {
        public IReadOnlyList<PointerObservation> Pointers { get; init; } = [];

        public IReadOnlyList<ReplayDeviceChange> DeviceChanges { get; init; } = [];

        public int Emitted => Decisions.Count(d => d.Outcome == PolicyOutcome.Emit);

        public IEnumerable<ReplayDecision> Haptic => Decisions.Where(d => d.Outcome == PolicyOutcome.Emit);
    }

    public static ReplayOutput Run(string path, PolicyConfig? config = null) => Run(File.ReadLines(path), config);

    public static ReplayOutput Run(IEnumerable<string> lines, PolicyConfig? config = null)
    {
        config ??= new PolicyConfig { Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions };
        var names = new Dictionary<int, string>();
        var interpreter = new SemanticInterpreter(new InterpreterConfig { LandingSpeedPxPerSec = config.LandingSpeedPxPerSec });
        var policy = new HapticPolicy(config, pid => names.TryGetValue(pid, out var n) ? n : null)
        {
            ConsentGiven = true,
            OutputRouteVerified = true,
        };
        var decisions = new List<ReplayDecision>();
        var notes = new List<string>();
        var pointers = new List<PointerObservation>();
        var deviceChanges = new List<ReplayDeviceChange>();
        int? foregroundOverride = null;
        double lastT = 0;

        void Handle(InterpretResult r, double t)
        {
            notes.AddRange(r.Notes.Select(n => $"{t}:{n}"));
            foreach (var e in r.Events)
            {
                var d = policy.Evaluate(e, t);
                decisions.Add(new ReplayDecision(t, e.Kind.ToString(), e.ControlType, d.Outcome, d.Reason, d.Intent?.Waveform));
            }
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var o = JsonNode.Parse(line)!.AsObject();
            var type = (string?)o["type"] ?? throw new InvalidDataException($"Trace line missing type: {line}");
            var t = (double?)o["t"] ?? lastT;
            if (type != "comment" && type != "process")
            {
                // A stopped pointer sends no input: simulate the engine's ~10 ms tick so dwell landings resolve on time.
                var skipIntermediate = type == "tick" && ((bool?)o["skipIntermediate"] ?? false);
                for (var tt = lastT + 10; !skipIntermediate && tt < t && interpreter.HasPendingArrival; tt += 10)
                {
                    Handle(interpreter.OnTick(tt), tt);
                }

                Handle(interpreter.OnTick(t), t);
                lastT = t;
            }

            switch (type)
            {
                case "comment":
                case "tick":
                    break;
                case "wheel":
                    // Scroll texture is off by default (SPEC §8.3); a wheel delta alone never proves content moved.
                    notes.Add($"{t}:wheel-ignored:scroll-texture-disabled");
                    break;
                case "process":
                    names[(int)o["pid"]!] = (string)o["name"]!;
                    break;
                case "pointer":
                    var p = new PointerObservation(t, (int)o["x"]!, (int)o["y"]!, ReadEnum(o, "origin", InputOrigin.Replay),
                        (bool?)o["primary"] ?? false, (int?)o["fg"] ?? foregroundOverride ?? 0, (long?)o["handle"] ?? 0);
                    p = p with { InputClass = ReadEnum(o, "inputClass", p.InputClass) };
                    pointers.Add(p);
                    Handle(interpreter.OnPointer(p), t);
                    break;
                case "foreground":
                    foregroundOverride = (int)o["pid"]!;
                    if (interpreter.LastPointer is { } last)
                    {
                        Handle(interpreter.OnPointer(last with { TimestampMs = t, ForegroundPid = foregroundOverride.Value }), t);
                    }

                    break;
                case "element":
                    var snap = ParseElement(o, t, interpreter.LastPointer) with
                    {
                        Generation = (long?)o["generation"] ?? interpreter.Generation,
                    };
                    Handle(interpreter.OnSnapshot(snap, t), t);
                    break;
                case "coop":
                    var coop = interpreter.Cooperative((string)o["waveform"]!, (int?)o["pid"] ?? 0, t);
                    var cd = policy.Evaluate(coop, t);
                    decisions.Add(new ReplayDecision(t, coop.Kind.ToString(), coop.ControlType, cd.Outcome, cd.Reason, cd.Intent?.Waveform));
                    break;
                case "device":
                    var available = (bool?)o["arrived"] ?? (bool?)o["available"] ?? true;
                    deviceChanges.Add(new ReplayDeviceChange(t, (long?)o["handle"] ?? 0, available));
                    policy.DeviceAvailable = available;
                    if (!available)
                    {
                        Handle(interpreter.OnDeviceUnavailable(t), t);
                    }

                    break;
                default:
                    throw new InvalidDataException($"Unknown trace event type '{type}'.");
            }
        }

        for (var tt = lastT + 10; tt <= lastT + 1000 && interpreter.HasPendingArrival; tt += 10)
        {
            Handle(interpreter.OnTick(tt), tt);
        }

        return new ReplayOutput(decisions, notes) { Pointers = pointers, DeviceChanges = deviceChanges };
    }

    private static T ReadEnum<T>(JsonObject o, string key, T fallback) where T : struct, Enum =>
        o[key] is not { } node ? fallback :
        Enum.TryParse<T>((string)node!, ignoreCase: true, out var value) && Enum.IsDefined(value) ? value :
        throw new InvalidDataException($"Unknown trace {key} '{node}'.");

    private static RectD? Rect(JsonNode? n) =>
        n is JsonArray b && b.Count == 4 ? new RectD((double)b[0]!, (double)b[1]!, (double)b[2]!, (double)b[3]!) : null;

    private static ElementSnapshot ParseElement(JsonObject o, double t, PointerObservation? last)
    {
        var bounds = Rect(o["bounds"]);

        RangeInfo? range = null;
        if (o["range"] is JsonObject r)
        {
            range = new RangeInfo((double)r["value"]!, (double)r["min"]!, (double)r["max"]!,
                (double?)r["small"] ?? 1, (double?)r["large"] ?? 0);
        }

        var qx = (int?)o["x"] ?? last?.X ?? 0;
        var qy = (int?)o["y"] ?? last?.Y ?? 0;
        return new ElementSnapshot
        {
            Source = "replay",
            ProcessId = (int?)o["pid"] ?? 0,
            ControlType = (string?)o["controlType"] ?? "Unknown",
            RuntimeIdHash = (string?)o["rid"],
            Bounds = bounds,
            IsEnabled = (bool?)o["enabled"] ?? true,
            IsOffscreen = (bool?)o["offscreen"] ?? false,
            IsPassword = (bool?)o["password"] ?? false,
            ParentControlType = (string?)o["parentType"],
            ParentRuntimeIdHash = (string?)o["parentRid"],
            ParentBounds = Rect(o["parentBounds"]),
            IsSelected = (bool?)o["selected"],
            Scale = (double?)o["scale"] ?? 1.0,
            Range = range,
            RequestedAtMs = (double?)o["req"] ?? t,
            ReceivedAtMs = t,
            QueryX = qx,
            QueryY = qy,
            Error = (string?)o["error"],
        };
    }
}

/// <summary>Writes run artifacts into a local, git-ignored folder and computes digests.</summary>
public sealed class RunArtifacts : IDisposable
{
    private StreamWriter? _trace;
    private bool _closed;

    private RunArtifacts(string directory, string runId)
    {
        Directory.CreateDirectory(directory);
        RunDirectory = directory;
        RunId = runId;
    }

    public string RunDirectory { get; }

    public string RunId { get; }

    public static RunArtifacts Create(string experimentId, string? outDir = null)
    {
        var runId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{experimentId}-{Guid.NewGuid().ToString("N")[..6]}";
        var dir = outDir ?? Path.Combine(ArtifactLocator.RunsRoot(), runId);
        return new RunArtifacts(dir, runId);
    }

    public void Trace(object evt)
    {
        if (_closed)
        {
            return; // Never reopen (and truncate) a trace after the result was written.
        }

        _trace ??= new StreamWriter(Path.Combine(RunDirectory, "trace.jsonl"), append: false) { AutoFlush = true };
        lock (_trace)
        {
            _trace.WriteLine(JsonSerializer.Serialize(evt, HapticsJson.Compact));
        }
    }

    public string WriteText(string name, string content)
    {
        var path = Path.Combine(RunDirectory, name);
        File.WriteAllText(path, content);
        return path;
    }

    public string WriteResult(ExperimentResult result)
    {
        if (_closed)
        {
            throw new InvalidOperationException("Result already written for this run; runs are immutable.");
        }

        _closed = true;
        _trace?.Flush();
        _trace?.Dispose();
        _trace = null;
        result.Finalize();
        foreach (var file in Directory.GetFiles(RunDirectory).Where(f => !f.EndsWith("result.json", StringComparison.OrdinalIgnoreCase)))
        {
            var rel = Path.GetFileName(file);
            var existing = result.Artifacts.FirstOrDefault(a => a.Path == rel);
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
            if (existing is null)
            {
                result.Artifacts.Add(new ArtifactRef { Path = rel, Sha256 = sha });
            }
            else
            {
                existing.Sha256 = sha;
            }
        }

        var path = Path.Combine(RunDirectory, "result.json");
        File.WriteAllText(path, JsonSerializer.Serialize(result, HapticsJson.Indented));
        return path;
    }

    public void Dispose()
    {
        _trace?.Dispose();
        _trace = null;
    }
}

public static class ArtifactLocator
{
    /// <summary>
    /// When running inside this repository, evidence goes to &lt;repo&gt;/artifacts (git-ignored).
    /// Otherwise it goes to %LOCALAPPDATA%\HapticsEverywhere\artifacts. Override with HAPTICS_ARTIFACTS.
    /// </summary>
    public static string Root()
    {
        var env = Environment.GetEnvironmentVariable("HAPTICS_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        var repo = FindRepoRoot(AppContext.BaseDirectory) ?? FindRepoRoot(Environment.CurrentDirectory);
        return repo is not null
            ? Path.Combine(repo, "artifacts")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HapticsEverywhere", "artifacts");
    }

    public static string RunsRoot() => Path.Combine(Root(), "runs");

    public static string? FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "HapticsEverywhere.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
