using Haptics.Core;

namespace Haptics.Windows.Tests;

public class RuntimeTests
{
    private static string LabExe()
    {
        var p = Path.Combine(AppContext.BaseDirectory, "haptics-lab.exe");
        Assert.True(File.Exists(p), $"missing {p}");
        return p;
    }

    [Fact]
    public void ApiGuardsNeverThrow()
    {
        var stat = HapticsApi.ProbeStatic();
        Assert.True(stat.ContainsKey("isSupported"));
        var st = HapticsApi.InspectCurrentThread();
        Assert.NotEqual(0u, st.ThreadId);
        if (!HapticsApi.TypePresent)
        {
            Assert.False(HapticsApi.SafeIsSupported());
        }
    }

    [Fact]
    public void DiscoveryControllerTypeIsProbedSeparatelyFromManager()
    {
        var stat = HapticsApi.ProbeStatic();
        Assert.Equal(HapticsApi.TypePresent, (bool)stat["typePresent.InputHapticsManager"]!);
        Assert.Equal(HapticsApi.ControllerTypePresent, (bool)stat["typePresent.SimpleHapticsController"]!);
    }

    [Fact]
    public void ControllerRoutesWithoutAControllerAreRejectedNotClaimed()
    {
        var a = HapticsApi.SendViaController(OutputRoutes.CachedController, null, "Click", 1.0);
        Assert.False(a.Accepted);
        Assert.Equal("no-captured-controller", a.Error);
        Assert.DoesNotContain(EvidenceLabels.PhysicalConfirmed, a.Stages());
    }

    [Fact]
    public async Task MessageThreadRunsPostedWorkOnItsOwnThread()
    {
        using var t = new MessageWindowThread("test", MessageWindowKind.MessageOnly, registerRawMouse: false).Start();
        var observed = await t.InvokeAsync(() => HapticsApi.InspectCurrentThread().ThreadId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(t.ThreadId, observed);
    }

    [Fact]
    public void RawInputRegistersOnInteractiveDesktop()
    {
        if (!Environment.UserInteractive || Environment.GetEnvironmentVariable("CI") is not null)
        {
            return; // Hosted CI has no meaningful input desktop; covered by EXP-030 on real hardware.
        }

        using var t = new MessageWindowThread("raw", MessageWindowKind.MessageOnly, registerRawMouse: true).Start();
        Assert.True(t.RawInputRegistered, $"win32 error {t.RawInputRegistrationError}");
    }

    [Fact]
    public async Task UiaWorkerHangIsContainedAndRestarted()
    {
        using var c = new UiaClient(LabExe(), [UiaWorker.Arg, UiaWorker.FaultInjectionArg], new UiaClientOptions { TimeoutMs = 300, AllowFaultInjection = true });
        var ping = await c.QueryAsync(0, 0, "ping");
        Assert.True(ping.Ok);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hang = await c.QueryAsync(0, 0, "hang");
        Assert.Equal("timeout", hang.ClientStatus);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"{sw.ElapsedMilliseconds}ms");
        var after = await c.QueryAsync(0, 0, "ping");
        Assert.True(after.Ok);
        Assert.NotEqual(ping.WorkerPid, after.WorkerPid);
    }

    [Fact]
    public async Task WorkerStartupDoesNotCountAgainstQueryTimeout()
    {
        // 40 ms is far less than a cold .NET process start; the first query must still succeed.
        using var c = new UiaClient(LabExe(), [UiaWorker.Arg], new UiaClientOptions { TimeoutMs = 40 });
        var ping = await c.QueryAsync(0, 0, "ping");
        Assert.True(ping.Ok, ping.ClientStatus ?? ping.Error);
        Assert.Equal(0, c.Stats.Timeouts);
    }

    [Fact]
    public async Task FaultCommandsAreRefusedWithoutOptIn()
    {
        using var c = new UiaClient(LabExe(), [UiaWorker.Arg], new UiaClientOptions { TimeoutMs = 2000 });
        var r = await c.QueryAsync(0, 0, "hang");
        Assert.False(r.Ok);
        Assert.Equal("unsupported-command", r.Error);
    }

    [Fact]
    public async Task LatestWinsCoalescingSupersedesOlderRequests()
    {
        using var c = new UiaClient(LabExe(), [UiaWorker.Arg, UiaWorker.FaultInjectionArg], new UiaClientOptions { TimeoutMs = 3000, AllowFaultInjection = true });
        await c.QueryAsync(0, 0, "ping");
        var slow = c.QueryAsync(400, 0, "slow");
        var a = c.QueryAsync(0, 0, "ping");
        var b = c.QueryAsync(0, 0, "ping");
        Assert.Equal("superseded", (await a).ClientStatus);
        Assert.True((await b).Ok);
        Assert.True((await slow).Ok);
    }

    [Fact]
    public void PipelineInDryRunNeverEmits()
    {
        var cfg = new PolicyConfig { Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions };
        using var p = new ObservationPipeline(cfg, new PipelineOptions
        {
            Route = OutputRoutes.DryRun,
            UiaFactory = o => new UiaClient(LabExe(), [UiaWorker.Arg], o),
        });
        p.Policy.ConsentGiven = true;
        Assert.False(p.Policy.OutputRouteVerified);
        p.Start();
        Thread.Sleep(300);
        p.Stop();
        Assert.Equal(0, p.Stats().Emits);
    }

    [Fact]
    public void EnvironmentFingerprintIsStableAndOpaque()
    {
        var a = EnvironmentProbe.Capture();
        Assert.Equal(a.MachineFingerprint, EnvironmentProbe.CurrentFingerprint());
        Assert.DoesNotContain(Environment.MachineName, a.MachineFingerprint, StringComparison.OrdinalIgnoreCase);
    }
}
