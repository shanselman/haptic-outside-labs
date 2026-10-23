using Haptics.Core;

namespace Haptics.Core.Tests;

public class PolicyTests
{
    private static SemanticEvent Enter(double t, string key = "1:a", ElementSnapshot? el = null, double speed = 0, long transition = 1, bool primary = false) =>
        new(SemanticEventKind.EnterControl, transition, t, t, 1, el?.ControlType ?? "Button", key, null, 0.9, ["test"], primary, speed,
            el ?? new ElementSnapshot { ProcessId = 1, ControlType = "Button", RuntimeIdHash = key });

    private static HapticPolicy Policy(PolicyConfig? cfg = null) =>
        new(cfg ?? new PolicyConfig { Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions }, _ => "app")
        {
            ConsentGiven = true,
            OutputRouteVerified = true,
        };

    [Fact]
    public void OffSuppressesEverything()
    {
        var p = Policy(new PolicyConfig { Mode = HapticsMode.Off, AppPolicy = AppPolicy.AllExceptExclusions });
        Assert.Equal("mode-off", p.Evaluate(Enter(0), 0).Reason);
    }

    [Fact]
    public void NoConsentSuppresses()
    {
        var p = Policy();
        p.ConsentGiven = false;
        Assert.Equal("no-consent", p.Evaluate(Enter(0), 0).Reason);
    }

    [Fact]
    public void UnverifiedRouteIsDryRunNeverEmit()
    {
        var p = Policy();
        p.OutputRouteVerified = false;
        var d = p.Evaluate(Enter(0), 0);
        Assert.Equal(PolicyOutcome.DryRun, d.Outcome);
        Assert.Equal("output-route-unverified", d.Reason);
    }

    [Fact]
    public void DisabledControlsAreSilentByDefault()
    {
        var el = new ElementSnapshot { ProcessId = 1, ControlType = "Button", IsEnabled = false, RuntimeIdHash = "d" };
        Assert.Equal("disabled-control", Policy().Evaluate(Enter(0, "1:d", el), 0).Reason);
    }

    [Fact]
    public void StaleIntentIsDroppedNotLateBuzzed()
    {
        Assert.Equal("stale-intent", Policy().Evaluate(Enter(0), 46).Reason);
    }

    [Fact]
    public void GlobalSpacingAndPerSecondCapHold()
    {
        var p = Policy();
        var emitted = 0;
        for (var i = 0; i < 100; i++)
        {
            var t = i * 50.0;
            if (p.Evaluate(Enter(t, $"1:k{i}", transition: i), t).Outcome == PolicyOutcome.Emit)
            {
                emitted++;
            }
        }

        // 5 s of 20 Hz candidates under a 4/s cap with 120 ms spacing.
        Assert.InRange(emitted, 15, 20);
    }

    [Fact]
    public void SameElementReentryCooldown()
    {
        var p = Policy();
        Assert.Equal(PolicyOutcome.Emit, p.Evaluate(Enter(0, "1:a", transition: 1), 0).Outcome);
        Assert.Equal("reentry-cooldown", p.Evaluate(Enter(300, "1:a", transition: 2), 300).Reason);
        Assert.Equal(PolicyOutcome.Emit, p.Evaluate(Enter(700, "1:a", transition: 3), 700).Outcome);
    }

    [Fact]
    public void SuccessAndErrorAreNeverGenericallyEmitted()
    {
        Assert.DoesNotContain(Enum.GetValues<SemanticEventKind>(), k =>
        {
            var p = Policy();
            var e = new SemanticEvent(k, 1, 0, 0, 1, "Button", "1:z", null, 1, [], false, 0, new ElementSnapshot { ProcessId = 1 });
            var d = p.Evaluate(e, 0);
            return d.Intent?.Kind is IntentKind.Success or IntentKind.Error;
        });
    }

    [Fact]
    public void IntensityIsScaledAndClamped()
    {
        var cfg = new PolicyConfig { Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions, IntensityScale = 0.9 };
        var p = Policy(cfg);
        var e = new SemanticEvent(SemanticEventKind.RangeLimit, 1, 0, 0, 1, "Slider", "1:s", null, 1, [], true, 0, new ElementSnapshot { ProcessId = 1, ControlType = "Slider" });
        var d = p.Evaluate(e, 0);
        Assert.Equal(PolicyOutcome.Emit, d.Outcome);
        Assert.Equal(1.0, d.Intent!.Intensity);
        Assert.Equal("Collide", d.Intent.Waveform);
    }
}
