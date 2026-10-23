using System.Text.Json.Serialization;

namespace Haptics.Core;

/// <summary>
/// One call to a haptic output API. This records what the API said, never what the hand felt.
/// Physical confirmation lives on <see cref="TrialRecord.Answer"/>, deliberately on a different object.
/// </summary>
public sealed class HapticAttempt
{
    public required string Route { get; init; }
    public uint ThreadId { get; set; }
    public string Waveform { get; set; } = "";
    public ushort WaveformId { get; set; }
    public ushort FallbackId { get; set; }
    public double Intensity { get; set; }
    public bool ApiPresent { get; set; }
    public bool ManagerOk { get; set; }
    public bool ControllerPresent { get; set; }
    public string? DeviceType { get; set; }
    public string? ControllerIdHash { get; set; }
    public bool Called { get; set; }

    /// <summary>The API returned true (or a void call did not throw). This is NOT physical proof.</summary>
    public bool Accepted { get; set; }

    public string? Error { get; set; }
    public int? HResult { get; set; }
    public double CallStartMs { get; set; }
    public double CallEndMs { get; set; }
    public double? InputToCallMs { get; set; }

    public IReadOnlyList<string> Stages()
    {
        var s = new List<string>();
        if (ManagerOk)
        {
            s.Add(EvidenceLabels.GetManagerOk);
        }

        if (ControllerPresent)
        {
            s.Add(EvidenceLabels.CurrentControllerPresent);
        }

        if (Accepted)
        {
            s.Add(EvidenceLabels.SendReturnedTrue);
        }

        return s;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<TrialAnswer>))]
public enum TrialAnswer
{
    None,
    FeltYes,
    FeltNo,
    Uncertain,
}

/// <summary>A single blinded pulse-or-control trial with provenance and the operator's judgment.</summary>
public sealed class TrialRecord
{
    public int Index { get; init; }
    public required string Arm { get; init; }
    public bool IsControl { get; init; }
    public double FiredAtMs { get; set; }
    public HapticAttempt? Attempt { get; set; }
    public TrialAnswer Answer { get; set; } = TrialAnswer.None;
    public double? AnsweredAtMs { get; set; }
    public bool ProvenanceValid { get; set; } = true;
    public List<string> ProvenanceIssues { get; init; } = new();
    public Dictionary<string, object?> Context { get; init; } = new();

    public void Invalidate(string reason)
    {
        ProvenanceValid = false;
        ProvenanceIssues.Add(reason);
    }
}

public sealed record ScheduledTrial(int Index, string Arm, bool IsControl);

public static class TrialSchedule
{
    public const string ControlArm = "CONTROL";

    /// <summary>Balanced, seeded, shuffled schedule: <paramref name="perArm"/> pulses per arm plus <paramref name="controls"/> no-op controls.</summary>
    public static IReadOnlyList<ScheduledTrial> Build(IReadOnlyList<string> arms, int perArm, int controls, int seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(perArm);
        ArgumentOutOfRangeException.ThrowIfNegative(controls);
        var items = new List<(string Arm, bool Control)>();
        foreach (var arm in arms)
        {
            for (var i = 0; i < perArm; i++)
            {
                items.Add((arm, false));
            }
        }

        for (var i = 0; i < controls; i++)
        {
            items.Add((ControlArm, true));
        }

        var rng = new Random(seed);
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }

        return items.Select((t, i) => new ScheduledTrial(i + 1, t.Arm, t.Control)).ToList();
    }
}

public sealed class PhysicalCriteria
{
    public int MinPulseTrials { get; init; } = 5;
    public int MinControlTrials { get; init; } = 5;
    public double MinHitRate { get; init; } = 0.8;
    public double MaxFalseAlarmRate { get; init; } = 0.2;
    public double MaxPValue { get; init; } = 0.01;
    public double MaxUncertainFraction { get; init; } = 0.3;

    /// <summary>At or below this hit rate (with trustworthy controls) the route is considered "not felt".</summary>
    public double FailMaxHitRate { get; init; } = 0.2;
}

/// <summary>Per-arm (route or waveform) verdict computed only from blinded operator answers.</summary>
public sealed record ArmVerdict(
    string Arm,
    int PulseTrials,
    int Hits,
    int Misses,
    int UncertainPulses,
    int ControlTrials,
    int FalseAlarms,
    int CorrectRejections,
    int UncertainControls,
    double HitRate,
    double FalseAlarmRate,
    double FisherP,
    int ApiAccepted,
    int ApiRejected,
    int InvalidTrialsExcluded,
    ExperimentStatus Status,
    EvidenceClass EvidenceClass,
    IReadOnlyList<string> Labels,
    string Reason);

public static class PhysicalEvaluator
{
    /// <summary>
    /// Decide a route/waveform arm from blinded trials. <paramref name="baselinePassed"/> must reflect independent
    /// knowledge that the hardware can buzz at all (e.g. EXP-010 PASS); without it "not felt" is INDETERMINATE, not FAIL.
    /// </summary>
    public static ArmVerdict Evaluate(string arm, IEnumerable<TrialRecord> allTrials, bool baselinePassed, PhysicalCriteria? criteria = null)
    {
        criteria ??= new PhysicalCriteria();
        var trials = allTrials.ToList();
        var invalid = trials.Count(t => !t.ProvenanceValid && (t.Arm == arm || t.IsControl));
        var valid = trials.Where(t => t.ProvenanceValid && t.Answer != TrialAnswer.None).ToList();
        var pulses = valid.Where(t => !t.IsControl && t.Arm == arm).ToList();
        var controls = valid.Where(t => t.IsControl).ToList();

        var hits = pulses.Count(t => t.Answer == TrialAnswer.FeltYes);
        var misses = pulses.Count(t => t.Answer == TrialAnswer.FeltNo);
        var uncP = pulses.Count(t => t.Answer == TrialAnswer.Uncertain);
        var fa = controls.Count(t => t.Answer == TrialAnswer.FeltYes);
        var cr = controls.Count(t => t.Answer == TrialAnswer.FeltNo);
        var uncC = controls.Count(t => t.Answer == TrialAnswer.Uncertain);
        var accepted = pulses.Count(t => t.Attempt?.Accepted == true);
        var rejected = pulses.Count(t => t.Attempt is { Called: true, Accepted: false });

        var answeredPulses = hits + misses;
        var answeredControls = fa + cr;
        var hitRate = answeredPulses == 0 ? 0 : (double)hits / answeredPulses;
        var faRate = answeredControls == 0 ? 0 : (double)fa / answeredControls;
        var p = FisherExact.OneSidedGreater(hits, misses, fa, cr);
        var labels = new List<string>();
        if (accepted > 0)
        {
            labels.Add(EvidenceLabels.OutputApiAccepted);
        }

        ArmVerdict Make(ExperimentStatus s, EvidenceClass c, string reason) => new(
            arm, pulses.Count, hits, misses, uncP, controls.Count, fa, cr, uncC, hitRate, faRate, p,
            accepted, rejected, invalid, s, c, labels, reason);

        var total = pulses.Count + controls.Count;
        var uncertainFraction = total == 0 ? 1 : (double)(uncP + uncC) / total;
        if (answeredPulses < criteria.MinPulseTrials || answeredControls < criteria.MinControlTrials)
        {
            labels.Add(EvidenceLabels.PhysicalUnconfirmed);
            return Make(ExperimentStatus.INDETERMINATE, accepted > 0 ? EvidenceClass.OBSERVED : EvidenceClass.NONE,
                $"Insufficient answered valid trials (pulses {answeredPulses}/{criteria.MinPulseTrials}, controls {answeredControls}/{criteria.MinControlTrials}).");
        }

        if (uncertainFraction > criteria.MaxUncertainFraction)
        {
            labels.Add(EvidenceLabels.PhysicalUnconfirmed);
            return Make(ExperimentStatus.INDETERMINATE, EvidenceClass.OBSERVED,
                $"Too many 'uncertain' answers ({uncertainFraction:P0}).");
        }

        if (hitRate >= criteria.MinHitRate && faRate <= criteria.MaxFalseAlarmRate && p <= criteria.MaxPValue)
        {
            labels.Add(EvidenceLabels.PhysicalConfirmed);
            return Make(ExperimentStatus.PASS, EvidenceClass.PHYSICAL,
                $"Pulses felt {hits}/{answeredPulses} vs controls {fa}/{answeredControls} (one-sided Fisher p={p:0.####}).");
        }

        if (accepted == 0)
        {
            labels.Add(EvidenceLabels.OutputApiRejected);
            return Make(baselinePassed ? ExperimentStatus.FAIL : ExperimentStatus.INDETERMINATE, EvidenceClass.OBSERVED,
                baselinePassed
                    ? "Output API never accepted a waveform on this route although the foreground baseline passed."
                    : "Output API never accepted a waveform; no physical baseline to compare against.");
        }

        if (hitRate <= criteria.FailMaxHitRate && faRate <= criteria.MaxFalseAlarmRate)
        {
            labels.Add(EvidenceLabels.PhysicalNotFelt);
            return Make(baselinePassed ? ExperimentStatus.FAIL : ExperimentStatus.INDETERMINATE, EvidenceClass.OBSERVED,
                baselinePassed
                    ? $"API accepted {accepted} calls but pulses were not felt ({hits}/{answeredPulses}) while the foreground baseline passed."
                    : $"API accepted {accepted} calls but pulses were not felt ({hits}/{answeredPulses}); no physical baseline, so not a FAIL.");
        }

        labels.Add(EvidenceLabels.PhysicalUnconfirmed);
        return Make(ExperimentStatus.INDETERMINATE, EvidenceClass.OBSERVED,
            $"Ambiguous discrimination: hits {hits}/{answeredPulses}, false alarms {fa}/{answeredControls}, p={p:0.####}.");
    }

    /// <summary>Combine arm verdicts: any PASS ⇒ PASS; all FAIL ⇒ FAIL; otherwise INDETERMINATE.</summary>
    public static (ExperimentStatus Status, EvidenceClass Class) Combine(IEnumerable<ArmVerdict> verdicts)
    {
        var list = verdicts.ToList();
        if (list.Count == 0)
        {
            return (ExperimentStatus.NOT_RUN, EvidenceClass.NONE);
        }

        if (list.Any(v => v.Status == ExperimentStatus.PASS))
        {
            return (ExperimentStatus.PASS, EvidenceClass.PHYSICAL);
        }

        var cls = list.Max(v => v.EvidenceClass);
        if (list.All(v => v.Status == ExperimentStatus.FAIL))
        {
            return (ExperimentStatus.FAIL, cls);
        }

        return (ExperimentStatus.INDETERMINATE, cls);
    }
}

public static class FisherExact
{
    /// <summary>
    /// One-sided Fisher exact test for the 2x2 table [[a,b],[c,d]] (rows: pulse/control, cols: felt/not felt).
    /// Returns P(X ≥ a) under the hypergeometric null that "felt" is independent of whether a pulse was sent.
    /// </summary>
    public static double OneSidedGreater(int a, int b, int c, int d)
    {
        if (a < 0 || b < 0 || c < 0 || d < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(a), "Counts must be non-negative.");
        }

        var row1 = a + b;
        var col1 = a + c;
        var n = a + b + c + d;
        if (n == 0 || row1 == 0 || col1 == 0)
        {
            return 1.0;
        }

        var maxA = Math.Min(row1, col1);
        double p = 0;
        for (var x = a; x <= maxA; x++)
        {
            var y = col1 - x;
            if (y < 0 || y > n - row1)
            {
                continue;
            }

            p += Math.Exp(LogChoose(row1, x) + LogChoose(n - row1, y) - LogChoose(n, col1));
        }

        return Math.Min(1.0, p);
    }

    private static double LogChoose(int n, int k) => LogFactorial(n) - LogFactorial(k) - LogFactorial(n - k);

    private static double LogFactorial(int n)
    {
        double s = 0;
        for (var i = 2; i <= n; i++)
        {
            s += Math.Log(i);
        }

        return s;
    }
}
