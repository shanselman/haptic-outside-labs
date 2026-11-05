using Haptics.Core;
using CaptionButton = Haptics.Core.CaptionButton;

namespace Haptics.Windows.Tests;

public class RuntimeTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void NormalizedOwnerRetainsRestrictionsFromEitherSide(bool enabled, bool offscreen, bool password)
    {
        var owner = new ElementSnapshot { ProcessId = 100, ControlType = "ComboBox", IsEnabled = true };
        var fromPart = UiaWorker.NormalizeOwnerPart(owner, 100, enabled, offscreen, password);
        var fromOwner = UiaWorker.NormalizeOwnerPart(owner with
        {
            IsEnabled = enabled, IsOffscreen = offscreen, IsPassword = password,
        }, 100, true, false, false);
        Assert.NotNull(fromPart);
        Assert.Equal(fromPart, fromOwner);
        Assert.Equal(enabled, fromPart.IsEnabled);
        Assert.Equal(offscreen, fromPart.IsOffscreen);
        Assert.Equal(password, fromPart.IsPassword);
    }

    [Fact]
    public void NormalizedOwnerRejectsCrossProcessParts()
    {
        Assert.Null(UiaWorker.NormalizeOwnerPart(new ElementSnapshot { ProcessId = 100, ControlType = "ComboBox" },
            200, true, false, false));
    }

    [Theory]
    [InlineData("Shell_TrayWnd", true)]
    [InlineData("Shell_SecondaryTrayWnd", true)]
    [InlineData("GitHubCopilotCaptionHost", true)]
    [InlineData("Chrome_WidgetWin_1", false)]
    [InlineData("CabinetWClass", false)]
    [InlineData("Xaml_WindowedPopupClass", false)]
    public void DisabledShellHoverSkipsKnownHostsWithoutBlockingOrdinaryAppControls(string rootClass, bool skip)
    {
        Assert.Equal(skip, UiaWorker.SkipHoverObservation(
            new UiaRequest { ObserveCaptions = false }, rootClass));
        Assert.Equal(rootClass is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd",
            UiaWorker.SkipHoverObservation(new UiaRequest(), rootClass));
        Assert.False(UiaWorker.SkipHoverObservation(new UiaRequest { ObserveTaskbar = true }, rootClass));
    }

    [Theory]
    [InlineData("PinnedActionToolbarButton")]
    [InlineData("PageActionView")]
    [InlineData("ExtensionsToolbarButton")]
    [InlineData("AvatarToolbarButton")]
    [InlineData("BrowserAppMenuButton")]
    public void BrowserToolbarRecognitionRequiresNativeHostRoleAndStructuralClass(string className)
    {
        var candidate = new ElementSnapshot { ControlType = "Button", ParentControlType = "Pane", ClassName = className, IsToggle = true };
        Assert.True(UiaWorker.IsBrowserToolbarCommand("Chrome_WidgetWin_1", candidate));
        Assert.True(UiaWorker.IsBrowserToolbarCommand("Chrome_WidgetWin_1", candidate with { ParentControlType = "ToolBar" }));
        Assert.True(UiaWorker.IsBrowserToolbarCommand("Chrome_WidgetWin_1", candidate with { ParentControlType = null, IsButtonOwner = true }));
        Assert.False(UiaWorker.IsBrowserToolbarCommand("OtherHost", candidate));
        Assert.False(UiaWorker.IsBrowserToolbarCommand("Chrome_WidgetWin_1", candidate with { ControlType = "CheckBox" }));
        Assert.False(UiaWorker.IsBrowserToolbarCommand("Chrome_WidgetWin_1", candidate with { ParentControlType = "Document" }));
        Assert.False(UiaWorker.IsBrowserToolbarCommand("Chrome_WidgetWin_1", candidate with { ParentControlType = "Group" }));
        Assert.False(UiaWorker.IsBrowserToolbarCommand("Chrome_WidgetWin_1", candidate with { ClassName = "FormattingToggleButton" }));
        Assert.False(UiaWorker.IsBrowserToolbarCommand("Chrome_WidgetWin_1", candidate with { ClassName = "prefix" + className }));
    }

    [Theory]
    [InlineData(20, CaptionButton.Close)]
    [InlineData(8, CaptionButton.Minimize)]
    [InlineData(9, CaptionButton.Maximize)]
    [InlineData(1, CaptionButton.None)]
    [InlineData(-1, CaptionButton.None)]
    public void CaptionClassificationUsesNativeHitTestResults(int hit, CaptionButton expected) =>
        Assert.Equal(expected, UiaWorker.CaptionButtonFromHitTest(hit));

    [Theory]
    [InlineData(2293, 430, CaptionButton.Close)]
    [InlineData(2243, 430, CaptionButton.Maximize)]
    [InlineData(2196, 430, CaptionButton.Minimize)]
    [InlineData(2293, 449, CaptionButton.None)]
    public void ExplorerClientHitStillUsesOnlyReportedCaptionRectangles(int x, int y, CaptionButton expected)
    {
        var snapshot = new ElementSnapshot { ControlType = "Button", ParentControlType = "Window", QueryX = x, QueryY = y };
        var region = UiaWorker.CaptionSearchRegion(
            new Native.RECT { Left = 800, Top = 413, Right = 2320, Bottom = 1269 },
            new Native.RECT { Left = 1367, Top = 0, Right = 1513, Bottom = 30 });
        Assert.True(UiaWorker.NeedsCaptionQuery(snapshot, region, false));
        Assert.Equal(CaptionButton.None, UiaWorker.CaptionButtonFromHitTest(1));
        var info = new Native.TITLEBARINFOEX { State = new uint[6], Bounds = new Native.RECT[6] };
        info.Bounds[5] = new Native.RECT { Left = 2266, Top = 414, Right = 2313, Bottom = 444 };
        info.Bounds[3] = new Native.RECT { Left = 2220, Top = 414, Right = 2266, Bottom = 444 };
        info.Bounds[2] = new Native.RECT { Left = 2173, Top = 414, Right = 2220, Bottom = 444 };
        Assert.Equal(expected, UiaWorker.CaptionButtonFromInfo(info, x, y));
        if (expected != CaptionButton.None)
        {
            var caption = UiaWorker.NativeCaptionSnapshot(snapshot, 123, 100, "CabinetWClass", expected, info, true, true);
            Assert.False(caption.IsFocusedLanding);
            Assert.True(caption.Bounds?.Contains(x, y));
        }
    }

    [Fact]
    public void CaptionSearchAvoidsOrdinaryContentWithoutUsingTheRegionAsAHitBox()
    {
        var region = UiaWorker.CaptionSearchRegion(
            new Native.RECT { Left = -8, Top = -8, Right = 2568, Bottom = 1400 },
            new Native.RECT { Left = 2421, Top = 0, Right = 2567, Bottom = 30 });
        var snapshot = new ElementSnapshot { ControlType = "Pane", QueryX = 2538, QueryY = 36 };
        Assert.True(UiaWorker.NeedsCaptionQuery(snapshot, region, false));
        Assert.False(UiaWorker.NeedsCaptionQuery(snapshot with { QueryX = 100, QueryY = 300 }, region, false));
        Assert.False(UiaWorker.NeedsCaptionQuery(snapshot, null, false));
        Assert.Null(UiaWorker.CaptionSearchRegion(default, default));
        Assert.True(UiaWorker.NeedsCaptionQuery(snapshot with { ControlType = "TitleBar" }, null, false));
        Assert.True(UiaWorker.NeedsCaptionQuery(snapshot with { ParentControlType = "TitleBar" }, null, false));
        Assert.True(UiaWorker.NeedsCaptionQuery(snapshot, null, true));
        Assert.Equal(CaptionButton.None, UiaWorker.CaptionButtonFromInfo(
            new Native.TITLEBARINFOEX { State = new uint[6], Bounds = new Native.RECT[6] }, 2538, 36));
    }

    [Theory]
    [InlineData("GitHubCopilotCaptionHost", "Tauri Window", 100u, 100u, true)]
    [InlineData("GitHubCopilotCaptionHost", "Tauri Window", 100u, 200u, false)]
    [InlineData("GitHubCopilotCaptionHost", "Tauri Window", 0u, 0u, false)]
    [InlineData("GitHubCopilotCaptionHost", "OtherOwner", 100u, 100u, false)]
    [InlineData("Dialog", "Tauri Window", 100u, 100u, false)]
    [InlineData("OtherCaptionHost", "Tauri Window", 100u, 100u, false)]
    public void CaptionOwnerResolutionIsLimitedToTheObservedSameProcessHelper(
        string hostClass, string ownerClass, uint hostPid, uint ownerPid, bool expected) =>
        Assert.Equal(expected, UiaWorker.CaptionOwnerAllowed(hostClass, ownerClass, hostPid, ownerPid));

    [Fact]
    public void NativeCaptionCanIdentifyCloseWithoutAnAccessibleButtonOrInventedBounds()
    {
        var info = new Native.TITLEBARINFOEX { State = new uint[6], Bounds = new Native.RECT[6] };
        var pane = new ElementSnapshot
        {
            ControlType = "Pane", ProcessId = 200, RuntimeIdHash = "content", IsOffscreen = true,
            QueryX = 100, QueryY = 20, Bounds = new RectD(0, 0, 1000, 800), Scale = 1.5,
        };
        var close = UiaWorker.NativeCaptionSnapshot(pane, 123, 100, "NativeHost", CaptionButton.Close, info, true, true);
        Assert.Equal("native-caption", close.Source);
        Assert.Equal(100, close.ProcessId);
        Assert.Equal("Button", close.ControlType);
        Assert.Null(close.Bounds);
        Assert.False(close.IsOffscreen);
        Assert.False(close.IsFocusedLanding);
        var alternate = UiaWorker.NativeCaptionSnapshot(pane with { ControlType = "TitleBar", RuntimeIdHash = "caption" },
            123, 100, "NativeHost", CaptionButton.Close, info, true, true);
        Assert.Equal(close.ElementKey, alternate.ElementKey);
        Assert.Null(UiaWorker.NativeCaptionSnapshot(pane with { ControlType = "Button", ParentControlType = "TitleBar", IsOffscreen = false },
            123, 100, "NativeHost", CaptionButton.Close, info, true, true).Bounds);
        var policy = new HapticPolicy(PolicyConfig.Sample(EffectProfile.Focused, 0.55), _ => "app")
        {
            ConsentGiven = true, OutputRouteVerified = true,
        };
        var e = new SemanticEvent(SemanticEventKind.EnterControl, 1, 0, 0, 100, "Button",
            close.ElementKey, null, 1, [], false, 0, close);
        Assert.Equal("focused-target-not-enabled", policy.Evaluate(e, 0).Reason);

        info.Bounds[5] = new Native.RECT { Left = 80, Top = 0, Right = 120, Bottom = 40 };
        Assert.Equal(new RectD(80, 0, 40, 40), UiaWorker.NativeCaptionSnapshot(pane, 123, 100,
            "NativeHost", CaptionButton.Close, info, true, true).Bounds);
        Assert.True(UiaWorker.NativeCaptionSnapshot(pane with { IsPassword = true }, 123, 100,
            "NativeHost", CaptionButton.Close, info, true, true).IsPassword);
        Assert.False(UiaWorker.NativeCaptionSnapshot(pane, 123, 100,
            "NativeHost", CaptionButton.Close, info, false, true).IsEnabled);
        Assert.True(UiaWorker.NativeCaptionSnapshot(pane, 123, 100,
            "NativeHost", CaptionButton.Close, info, true, false).IsOffscreen);
        info.State[5] = 0x0001;
        Assert.False(UiaWorker.NativeCaptionSnapshot(pane, 123, 100,
            "NativeHost", CaptionButton.Close, info, true, true).IsEnabled);
        foreach (var state in new uint[] { 0x8000, 0x10000 })
        {
            info.State[5] = state;
            Assert.True(UiaWorker.NativeCaptionSnapshot(pane, 123, 100,
                "NativeHost", CaptionButton.Close, info, true, true).IsOffscreen);
        }
    }

    [Fact]
    public void ToggleMetadataNeverInventsOffForUnknownState()
    {
        Assert.Equal(false, UiaWorker.ToggleOn(System.Windows.Automation.ToggleState.Off));
        Assert.Equal(true, UiaWorker.ToggleOn(System.Windows.Automation.ToggleState.On));
        Assert.Null(UiaWorker.ToggleOn(System.Windows.Automation.ToggleState.Indeterminate));
        Assert.Null(UiaWorker.ToggleOn(System.Windows.Automation.AutomationElement.NotSupported));
    }

    [Fact]
    public void CaptionInfoUsesReportedRectanglesAndAvailabilityNotButtonOrder()
    {
        var info = new Native.TITLEBARINFOEX { State = new uint[6], Bounds = new Native.RECT[6] };
        info.Bounds[5] = new Native.RECT { Left = -300, Top = 20, Right = -250, Bottom = 60 };
        info.Bounds[2] = new Native.RECT { Left = -200, Top = 20, Right = -150, Bottom = 60 };
        Assert.Equal(CaptionButton.Close, UiaWorker.CaptionButtonFromInfo(info, -275, 30));
        Assert.Equal(CaptionButton.Minimize, UiaWorker.CaptionButtonFromInfo(info, -175, 30));
        Assert.Equal(CaptionButton.None, UiaWorker.CaptionButtonFromInfo(info, -240, 30));
        foreach (var state in new uint[] { 1, 0x8000, 0x10000 })
        {
            info.State[5] = state;
            Assert.Equal(CaptionButton.None, UiaWorker.CaptionButtonFromInfo(info, -275, 30));
        }
    }

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
