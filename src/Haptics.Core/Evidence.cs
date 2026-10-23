using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haptics.Core;

/// <summary>Experiment outcome vocabulary (SPEC §6.2). Never collapse these into a single green check.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExperimentStatus>))]
public enum ExperimentStatus
{
    NOT_RUN,
    PASS,
    FAIL,
    INDETERMINATE,
    BLOCKED,
    SKIPPED_BY_POLICY,
}

/// <summary>Strength of evidence a result carries (SPEC §2). Only PHYSICAL proves a usable haptic.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EvidenceClass>))]
public enum EvidenceClass
{
    NONE,
    DOC,
    BUILD,
    OBSERVED,
    PHYSICAL,
}

/// <summary>Well-known labels that keep API-level and physical-level claims apart.</summary>
public static class EvidenceLabels
{
    public const string RawReceived = "RAW_RECEIVED";
    public const string GetManagerOk = "GET_MANAGER_OK";
    public const string CurrentControllerPresent = "CURRENT_CONTROLLER_PRESENT";
    public const string SendReturnedTrue = "SEND_RETURNED_TRUE";
    public const string PhysicalConfirmed = "PHYSICAL_CONFIRMED";
    public const string OutputApiAccepted = "OUTPUT_API_ACCEPTED";
    public const string OutputApiRejected = "OUTPUT_API_REJECTED";
    public const string PhysicalUnconfirmed = "PHYSICAL_UNCONFIRMED";
    public const string PhysicalNotFelt = "PHYSICAL_NOT_FELT";
    public const string NotTestedOnHardware = "NOT_TESTED_ON_HARDWARE";
    public const string SyntheticInputOnly = "SYNTHETIC_INPUT_ONLY";
}

public sealed class MachineInfo
{
    public string OsDescription { get; set; } = "UNKNOWN-UNTIL-MEASURED";
    public string OsBuild { get; set; } = "UNKNOWN-UNTIL-MEASURED";
    public string? OsDisplayVersion { get; set; }
    public string OsArchitecture { get; set; } = "UNKNOWN-UNTIL-MEASURED";
    public string ProcessArchitecture { get; set; } = "UNKNOWN-UNTIL-MEASURED";
    public string DotnetRuntime { get; set; } = "UNKNOWN-UNTIL-MEASURED";
    public string? WindowsSdkProjection { get; set; }
    public string? DeviceModel { get; set; }

    /// <summary>Salted hash of machine name + OS build. Lets evidence be tied to "this machine" without naming it.</summary>
    public string MachineFingerprint { get; set; } = "UNKNOWN";

    public string SessionType { get; set; } = "interactive";
}

public sealed class Assertion
{
    public required string Name { get; init; }
    public required bool Passed { get; init; }
    public string? Detail { get; init; }
}

public sealed class ArtifactRef
{
    public required string Path { get; init; }
    public string? Sha256 { get; set; }
    public string? Description { get; init; }
}

/// <summary>
/// One immutable experiment run (SPEC §13.2). Use <see cref="Finalize"/> before writing; it enforces the
/// rule that nothing reaches PASS on a physical claim without PHYSICAL-class evidence.
/// </summary>
public sealed class ExperimentResult
{
    public int SchemaVersion { get; init; } = 1;
    public bool Example { get; init; }
    public required string RunId { get; init; }
    public required string ExperimentId { get; init; }
    public string ExperimentSpecVersion { get; init; } = "1.0";
    public string[] HypothesisIds { get; init; } = [];
    public string CodeCommit { get; set; } = "unknown";
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public MachineInfo Machine { get; set; } = new();
    public string Mode { get; set; } = "auto";

    /// <summary>True when the experiment's claim is about something physical (a mouse actually buzzing).</summary>
    public bool RequiresPhysicalConfirmation { get; init; }

    public Dictionary<string, object?> Method { get; init; } = new();
    public Dictionary<string, object?> Observations { get; init; } = new();
    public Dictionary<string, double?> LatencyMs { get; init; } = new();
    public List<Assertion> Assertions { get; init; } = new();
    public List<string> NegativeControls { get; init; } = new();
    public List<ArtifactRef> Artifacts { get; init; } = new();
    public Dictionary<string, ArmVerdict>? Routes { get; set; }
    public ExperimentStatus Status { get; set; } = ExperimentStatus.NOT_RUN;
    public EvidenceClass EvidenceClass { get; set; } = EvidenceClass.NONE;
    public List<string> Labels { get; init; } = new();
    public string Explanation { get; set; } = "";
    public List<string> Limitations { get; init; } = new();
    public string? NextDecision { get; set; }

    public void AddLabel(string label)
    {
        if (!Labels.Contains(label))
        {
            Labels.Add(label);
        }
    }

    /// <summary>
    /// Enforce evidence invariants. Returns the list of corrections applied (empty when already honest).
    /// </summary>
    public IReadOnlyList<string> Finalize()
    {
        var corrections = new List<string>();
        if (RequiresPhysicalConfirmation && Status == ExperimentStatus.PASS && EvidenceClass != EvidenceClass.PHYSICAL)
        {
            Status = ExperimentStatus.INDETERMINATE;
            AddLabel(EvidenceLabels.PhysicalUnconfirmed);
            corrections.Add("PASS downgraded to INDETERMINATE: physical claim lacks PHYSICAL evidence.");
        }

        if (Labels.Contains(EvidenceLabels.PhysicalConfirmed) && EvidenceClass != EvidenceClass.PHYSICAL)
        {
            Labels.Remove(EvidenceLabels.PhysicalConfirmed);
            AddLabel(EvidenceLabels.PhysicalUnconfirmed);
            corrections.Add("PHYSICAL_CONFIRMED label removed: evidence class is not PHYSICAL.");
        }

        if (Example && Status == ExperimentStatus.PASS)
        {
            Status = ExperimentStatus.NOT_RUN;
            corrections.Add("Example records can never PASS.");
        }

        if (Status == ExperimentStatus.PASS && Assertions.Any(a => !a.Passed))
        {
            Status = ExperimentStatus.FAIL;
            corrections.Add("PASS downgraded to FAIL: at least one assertion failed.");
        }

        if (corrections.Count > 0)
        {
            Limitations.AddRange(corrections);
        }

        return corrections;
    }
}

public static class HapticsJson
{
    public static readonly JsonSerializerOptions Indented = Create(indented: true);
    public static readonly JsonSerializerOptions Compact = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = indented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}
