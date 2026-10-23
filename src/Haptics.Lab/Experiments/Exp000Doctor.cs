using Haptics.Core;
using Haptics.Windows;

namespace Haptics.Lab.Experiments;

/// <summary>EXP-000: runtime/build inventory. Never sends a waveform.</summary>
public static class Exp000Doctor
{
    public static string Run(string? outDir)
    {
        using var s = ExperimentSession.Start("EXP-000", "auto", outDir);
        var r = s.Result;
        var stat = HapticsApi.ProbeStatic();
        foreach (var kv in stat)
        {
            r.Observations[kv.Key] = kv.Value;
        }

        var thread = HapticsApi.InspectCurrentThread();
        r.Observations["cliThreadManager"] = thread;
        var mice = RawInputDevices.EnumerateMice();
        r.Observations["rawInputMice"] = mice.Select(m => new { m.VidPid, m.Connection, m.NameHash }).ToList();
        r.Observations["rawInputMiceCount"] = mice.Count;
        r.Observations["microsoftMicePresent"] = mice.Count(m => m.VidPid?.StartsWith("VID_045E", StringComparison.Ordinal) == true);
        r.Observations["hapticsIntensitySetting"] = "UNKNOWN (not read; see Settings > Bluetooth & devices > Mouse)";
        r.Method["note"] = "Static guards + capability queries only. No waveform is sent by EXP-000.";

        var typePresent = HapticsApi.TypePresent;
        var supported = (bool)stat["isSupported"]!;
        var device = (bool)stat["isHapticDevicePresent"]!;
        s.Assert("doctor-ran-without-crash", true);
        s.Assert("api-guard-consistent", typePresent || !supported, "IsSupported must be false when the type is absent.");

        if (!typePresent)
        {
            r.Status = ExperimentStatus.BLOCKED;
            r.EvidenceClass = EvidenceClass.BUILD;
            r.Explanation = "InputHapticsManager is not present on this OS build. Haptic output is BLOCKED here (logic and UIA work can still run).";
        }
        else
        {
            r.Status = ExperimentStatus.PASS;
            r.EvidenceClass = EvidenceClass.BUILD;
            r.Explanation = $"API present; IsSupported={supported}; IsHapticDevicePresent={device}; {HapticsApi.Waveforms.Count} named waveforms; {mice.Count} raw-input mice. Haptic output itself is UNVERIFIED (doctor never sends).";
        }

        r.AddLabel(EvidenceLabels.NotTestedOnHardware);
        r.Limitations.Add("IsHapticDevicePresent can be true because of ANY haptic device (e.g. a haptic touchpad), not necessarily the target mouse.");
        r.Limitations.Add("A PASS here only means the API surface can be discovered without crashing.");
        r.NextDecision = "Run EXP-010 (foreground physical baseline) with the target mouse.";
        return s.Complete();
    }
}
