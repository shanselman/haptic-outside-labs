using Haptics.Core;
using Haptics.Windows;
using Windows.Devices.Haptics;

namespace Haptics.Lab.Experiments;

/// <summary>
/// EXP-023: TryGetForThread semantics, strictly within our own process. Uses TryStopFeedback (harmless)
/// instead of sending waveforms, so nothing vibrates. Never touches another application's threads.
/// </summary>
public static class Exp023ThreadAffinity
{
    public static string Run(string? outDir)
    {
        using var s = ExperimentSession.Start("EXP-023", "auto", outDir);
        var r = s.Result;
        if (!HapticsApi.TypePresent)
        {
            r.Status = ExperimentStatus.BLOCKED;
            r.Explanation = "InputHapticsManager not present.";
            return s.Complete();
        }

        var table = new List<Dictionary<string, object?>>();
        using var a = new MessageWindowThread("exp023-thread-a", MessageWindowKind.MessageOnly, registerRawMouse: false).Start();
        var aState = a.InvokeAsync(HapticsApi.InspectCurrentThread).GetAwaiter().GetResult();
        table.Add(Row("A.GetForCurrentThread", aState.ManagerOk, $"thread={aState.ThreadId} managerThread={aState.ManagerThreadId} controller={aState.ControllerPresent}"));
        s.Assert("manager-thread-id-matches-owner", !aState.ManagerOk || aState.ManagerThreadId == aState.ThreadId,
            $"owner={aState.ThreadId} reported={aState.ManagerThreadId}");

        var b = new Thread(() =>
        {
            table.Add(Try("B.TryGetForThread(A)", () =>
            {
                var m = InputHapticsManager.TryGetForThread(a.ThreadId);
                return m is null ? "null" : $"object (ThreadId={m.ThreadId})";
            }));
            table.Add(Try("B.read A-manager.CurrentHapticsControllerDeviceType", () =>
            {
                var m = InputHapticsManager.TryGetForThread(a.ThreadId);
                return m is null ? "n/a (null)" : m.CurrentHapticsControllerDeviceType.ToString();
            }));
            table.Add(Try("B.call A-manager.TryStopFeedback()", () =>
            {
                var m = InputHapticsManager.TryGetForThread(a.ThreadId);
                return m is null ? "n/a (null)" : $"returned {m.TryStopFeedback()}";
            }));
            table.Add(Try("B.TryGetForThread(B) before B creates one", () =>
                InputHapticsManager.TryGetForThread(Native32.GetCurrentThreadId()) is null ? "null" : "object"));
            table.Add(Try("B.GetForCurrentThread()", () => $"ThreadId={InputHapticsManager.GetForCurrentThread().ThreadId}"));
            table.Add(Try("B.TryGetForThread(B) after", () =>
                InputHapticsManager.TryGetForThread(Native32.GetCurrentThreadId()) is null ? "null" : "object"));
            table.Add(Try("B.TryGetForThread(0)", () => InputHapticsManager.TryGetForThread(0) is null ? "null" : "object"));
            table.Add(Try("B.TryGetForThread(0xFFFFFFF0)", () => InputHapticsManager.TryGetForThread(0xFFFFFFF0) is null ? "null" : "object"));
        });
        b.SetApartmentState(ApartmentState.MTA);
        b.Start();
        b.Join(TimeSpan.FromSeconds(10));

        r.Observations["table"] = table;
        r.Method["scope"] = "Own process only. Thread A: dedicated message-loop thread. Thread B: MTA worker. No waveform sent.";
        r.Status = ExperimentStatus.PASS;
        r.EvidenceClass = EvidenceClass.OBSERVED;
        r.Explanation = "Thread-ownership behaviour recorded for own-process threads (see observations.table). This does not authorise cross-process use.";
        r.Limitations.Add("Results describe this OS build only. TryGetForThread returning an object is not permission to drive another thread's device.");
        r.NextDecision = "Use the observed affinity rules when choosing EXP-020 routes.";
        return s.Complete();
    }

    private static Dictionary<string, object?> Row(string op, bool ok, string detail) => new() { ["operation"] = op, ["ok"] = ok, ["detail"] = detail };

    private static Dictionary<string, object?> Try(string op, Func<string> f)
    {
        try
        {
            return Row(op, true, f());
        }
        catch (Exception ex)
        {
            return Row(op, false, HapticsApi.Describe(ex));
        }
    }

    private static class Native32
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();
    }
}
