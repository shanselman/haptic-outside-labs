using System.Diagnostics;
using Haptics.Core;
using Haptics.Windows;

namespace Haptics.Lab.Experiments;

/// <summary>EXP-043: a hostile/hung UIA provider must never freeze the caller or deliver a late result.</summary>
public static class Exp043UiaContainment
{
    public static string Run(string? outDir)
    {
        using var s = ExperimentSession.Start("EXP-043", "auto", outDir);
        var r = s.Result;
        const double timeout = 200;
        using var uia = UiaClient.ForCurrentProcess(new UiaClientOptions { TimeoutMs = timeout, AllowFaultInjection = true });
        var steps = new List<Dictionary<string, object?>>();
        var delivered = new System.Collections.Concurrent.ConcurrentQueue<long>();
        uia.Response += (response, _, _) => delivered.Enqueue(response.Id);

        (UiaResponse Resp, double Ms) Do(string cmd, int arg = 0)
        {
            var sw = Stopwatch.StartNew();
            var resp = uia.QueryAsync(arg, 0, cmd).GetAwaiter().GetResult();
            var ms = sw.Elapsed.TotalMilliseconds;
            steps.Add(new() { ["id"] = resp.Id, ["cmd"] = cmd, ["arg"] = arg, ["ok"] = resp.Ok, ["status"] = resp.ClientStatus, ["error"] = resp.Error, ["elapsedMs"] = Math.Round(ms, 1), ["workerPid"] = resp.WorkerPid });
            return (resp, ms);
        }

        var warm = Do("ping");
        s.Assert("worker starts and answers ping", warm.Resp.Ok);
        var pid1 = warm.Resp.WorkerPid;

        var hang = Do("hang");
        s.Assert("hang is cut off by the watchdog", hang.Resp.ClientStatus == "timeout", $"status={hang.Resp.ClientStatus}");
        s.Assert("caller unblocked within timeout + 150ms", hang.Ms <= timeout + 150, $"{hang.Ms:0}ms");

        var after = Do("ping");
        s.Assert("worker restarted after hang", after.Resp.Ok && after.Resp.WorkerPid != pid1, $"pid {pid1} -> {after.Resp.WorkerPid}");

        var slow = Do("slow", 600);
        s.Assert("slow provider result is discarded, not delivered late", slow.Resp.ClientStatus == "timeout");
        var afterSlow = Do("ping");
        s.Assert("recovers after slow provider", afterSlow.Resp.Ok);

        var crash = Do("crash");
        s.Assert("crash reported as failure", !crash.Resp.Ok);
        var afterCrash = Do("ping");
        s.Assert("recovers after crash", afterCrash.Resp.Ok);

        Thread.Sleep(800);
        var stats = uia.Stats;
        var expected = new[] { warm.Resp.Id, after.Resp.Id, afterSlow.Resp.Id, afterCrash.Resp.Id };
        var actual = delivered.ToArray();
        s.Assert("only the four successful ping responses delivered, once and in order",
            actual.SequenceEqual(expected) && stats.Responses == expected.Length,
            $"expected={string.Join(',', expected)} actual={string.Join(',', actual)} responses={stats.Responses}");
        s.Assert("failed requests never delivered as responses",
            !actual.Contains(hang.Resp.Id) && !actual.Contains(slow.Resp.Id) && !actual.Contains(crash.Resp.Id));
        r.Observations["deliveredResponseIds"] = actual;
        r.Observations["steps"] = steps;
        r.Observations["uiaStats"] = stats;
        r.Method["faultInjection"] = "Worker started with --allow-fault-injection (lab only). hang = sleep forever; slow = sleep 600ms; crash = exit(3).";
        r.Status = r.Assertions.All(a => a.Passed) ? ExperimentStatus.PASS : ExperimentStatus.FAIL;
        r.EvidenceClass = EvidenceClass.OBSERVED;
        r.Explanation = $"Timeout={timeout}ms. Hang cut off after {hang.Ms:0}ms; worker restarted {stats.Restarts}x; caller never blocked past the deadline.";
        r.Limitations.Add("Faults are simulated inside our own worker; a real provider hang blocks inside UIAutomationCore, which the process boundary also contains.");
        return s.Complete();
    }
}
