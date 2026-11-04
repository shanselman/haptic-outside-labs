using System.Text.Json;
using Haptics.Core;
using Haptics.Tray;

namespace Haptics.Windows.Tests;

public class TrayDefaultsTests
{
    [Theory]
    [InlineData("{}", false)]
    [InlineData("""{"experimentalPenEnabled":true}""", true)]
    [InlineData("""{"experimentalPenEnabled":false}""", false)]
    public void ExperimentalPenIsOffByDefaultAndPersistsExplicitChoice(string json, bool expected)
    {
        var settings = JsonSerializer.Deserialize<TraySettings>(json, HapticsJson.Indented)!;
        Assert.Equal(expected, settings.ExperimentalPenEnabled);
        var saved = JsonSerializer.SerializeToElement(settings, HapticsJson.Indented);
        Assert.Equal(expected, saved.GetProperty("experimentalPenEnabled").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TraySharesInlineLinksAndKeepsOptionsEverythingOnly(bool moreIsBetter)
    {
        var trace = Path.Combine(ArtifactLocator.FindRepoRoot(AppContext.BaseDirectory)!, "tests", "traces",
            "style-inclusion-checkbox-repeat.jsonl");
        var focused = TraceReplay.Run(trace, new TraySettings { Style = EffectProfile.Focused, MoreIsBetter = moreIsBetter }.ToPolicy());
        var everything = TraceReplay.Run(trace, new TraySettings { Style = EffectProfile.Everything, MoreIsBetter = moreIsBetter }.ToPolicy());
        Assert.Equal(5, focused.Emitted);
        Assert.Equal(6, everything.Emitted);
        Assert.Equal(focused.Haptic.Select(d => (d.T, d.EventKind, d.ControlType)),
            everything.Haptic.Where(d => d.ControlType != "ListItem").Select(d => (d.T, d.EventKind, d.ControlType)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrayUsesSharedQuietSubsetAndFocusedSliderHover(bool moreIsBetter)
    {
        var trace = Path.Combine(ArtifactLocator.FindRepoRoot(AppContext.BaseDirectory)!, "tests", "traces", "quiet-subset-focused.jsonl");
        var focused = TraceReplay.Run(trace, new TraySettings { Style = EffectProfile.Focused, MoreIsBetter = moreIsBetter }.ToPolicy());
        var quiet = TraceReplay.Run(trace, new TraySettings { Style = EffectProfile.Quiet, MoreIsBetter = moreIsBetter }.ToPolicy());
        Assert.Equal(["Hover", "Hover", "Hover", "Step", "Collide", "Step"], focused.Haptic.Select(d => d.Waveform));
        Assert.Equal(["Hover", "Step", "Collide", "Step"], quiet.Haptic.Select(d => d.Waveform));
        Assert.Single(focused.Haptic, d => d.ControlType == "Slider" && d.Waveform == "Hover");
        var scenarios = focused.Haptic.Select(d => (d.T, d.EventKind, d.ControlType)).ToHashSet();
        Assert.All(quiet.Haptic, d => Assert.Contains((d.T, d.EventKind, d.ControlType), scenarios));
    }

    [Fact]
    public void LegacyTaskbarSettingsAreIgnoredAndNeverPersisted()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<TraySettings>(
            """{"taskbarHover":true,"closeButtonHover":false}""", HapticsJson.Compact)!;
        var cfg = settings.ToPolicy();
        Assert.False(cfg.CloseButtonHoverEnabled);
        Assert.False(JsonSerializer.SerializeToElement(settings, HapticsJson.Compact).TryGetProperty("taskbarHover", out _));
        Assert.False(JsonSerializer.SerializeToElement(cfg, HapticsJson.Compact).TryGetProperty("taskbarHoverEnabled", out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ToPolicyMapsCloseButtonHover(bool enabled)
    {
        var cfg = new TraySettings { CloseButtonHover = enabled }.ToPolicy();
        Assert.Equal(enabled, cfg.CloseButtonHoverEnabled);
    }

    [Fact]
    public void FocusedCaptionReplayIsSilentRegardlessOfCloseSetting()
    {
        var traces = Path.Combine(ArtifactLocator.FindRepoRoot(AppContext.BaseDirectory)!, "tests", "traces");
        var path = Path.Combine(traces, "caption-close-hover.jsonl");
        var on = TraceReplay.Run(path, new TraySettings().ToPolicy());
        Assert.Empty(on.Haptic);
        Assert.DoesNotContain(on.Decisions, d => d.Reason == "coalesced-with-group-boundary");

        var off = TraceReplay.Run(path, new TraySettings { CloseButtonHover = false }.ToPolicy());
        Assert.Empty(off.Haptic);
    }

    [Fact]
    public void CloseButtonHoverDefaultsOn()
    {
        var json = JsonSerializer.SerializeToElement(new TraySettings(), HapticsJson.Indented);
        Assert.True(json.GetProperty("closeButtonHover").GetBoolean());
    }

    [Theory]
    [InlineData("{}", true)]
    [InlineData("""{"enabled":false,"style":"Quiet","intensityScale":0.55,"cooperationEnabled":false}""", true)]
    [InlineData("""{"closeButtonHover":false}""", false)]
    [InlineData("""{"closeButtonHover":true}""", true)]
    public void ExistingSettingsDefaultCloseHoverOnAndPreserveExplicitChoice(string json, bool expected)
    {
        var settings = JsonSerializer.Deserialize<TraySettings>(json, HapticsJson.Indented)!;
        var saved = JsonSerializer.SerializeToElement(settings, HapticsJson.Indented);
        Assert.Equal(expected, saved.GetProperty("closeButtonHover").GetBoolean());
    }

    [Fact]
    public void QuietStyleKeepsRecognizedCloseSilentEvenWithCloseHoverOn()
    {
        var settings = new TraySettings { Style = EffectProfile.Quiet };
        Assert.True(settings.CloseButtonHover);
        var cfg = settings.ToPolicy();
        Assert.True(cfg.CloseButtonHoverEnabled);
        Assert.False(cfg.HoverEnabled);
        var traces = Path.Combine(ArtifactLocator.FindRepoRoot(AppContext.BaseDirectory)!, "tests", "traces");
        var replay = TraceReplay.Run(Path.Combine(traces, "caption-close-hover.jsonl"), cfg);
        Assert.Empty(replay.Haptic);
        Assert.Contains(replay.Notes, n => n.EndsWith("skip:focused-target-not-enabled", StringComparison.Ordinal));
    }

    [Fact]
    public void SampleDefaultsAreOnForAllAppsButStillSilentOnPasswordsAndExclusions()
    {
        var cfg = new TraySettings().ToPolicy();
        Assert.Equal(HapticsMode.On, cfg.Mode);
        Assert.Equal(AppPolicy.AllExceptExclusions, cfg.AppPolicy);
        Assert.True(cfg.HoverEnabled);
        Assert.Contains("KeePass", cfg.ExcludedProcesses);

        var traces = Path.Combine(ArtifactLocator.FindRepoRoot(AppContext.BaseDirectory)!, "tests", "traces");
        Assert.Equal(0, TraceReplay.Run(Path.Combine(traces, "security-password.jsonl"), cfg).Emitted);
        Assert.Equal(0, TraceReplay.Run(Path.Combine(traces, "excluded-app.jsonl"), new TraySettings().ToPolicy()).Emitted);
        Assert.Equal(2, TraceReplay.Run(Path.Combine(traces, "focused-navigation-hover.jsonl"), new TraySettings().ToPolicy()).Emitted);
    }

    [Fact]
    public void EverythingIncludesToolbarControlsButStillSuppressesFastCrossings()
    {
        var traces = Path.Combine(ArtifactLocator.FindRepoRoot(AppContext.BaseDirectory)!, "tests", "traces");
        var lab = TraceReplay.Run(Path.Combine(traces, "toolbar-fast-flick.jsonl")).Emitted;
        var sample = TraceReplay.Run(Path.Combine(traces, "toolbar-fast-flick.jsonl"), new TraySettings { Style = EffectProfile.Everything }.ToPolicy()).Emitted;
        Assert.Equal(0, lab);
        Assert.InRange(sample, 0, 1);
        Assert.Equal(5, TraceReplay.Run(Path.Combine(traces, "toolbar-slow-sweep.jsonl"),
            new TraySettings { Style = EffectProfile.Everything }.ToPolicy()).Emitted);
    }

    [Fact]
    public void MoreIsBetterKeepsDwellAndSafetyWhileRaisingRateLimits()
    {
        var traces = Path.Combine(ArtifactLocator.FindRepoRoot(AppContext.BaseDirectory)!, "tests", "traces");
        var fastSweep = Path.Combine(traces, "toolbar-fast-flick.jsonl");
        var settings = new TraySettings { MoreIsBetter = true };
        var config = settings.ToPolicy();
        Assert.Equal(0, config.LandingSpeedPxPerSec);
        Assert.Equal(0, config.DensityLandsPer2s);
        Assert.Equal(25, config.GlobalMaxPulsesPerSecond);
        Assert.Equal(1500, config.MaxPulsesPerMinute);
        Assert.Equal(40, config.MinimumGlobalSpacingMs);
        Assert.Equal(40, config.SameElementReentryCooldownMs);

        var focused = TraceReplay.Run(fastSweep, new TraySettings().ToPolicy());
        Assert.Equal(0, focused.Emitted);
        var replay = TraceReplay.Run(fastSweep, config);
        Assert.Equal(0, replay.Emitted);
        Assert.InRange(TraceReplay.Run(fastSweep,
            new TraySettings { Style = EffectProfile.Everything, MoreIsBetter = true }.ToPolicy()).Emitted, 0, 1);

        Assert.Equal(0, TraceReplay.Run(Path.Combine(traces, "security-password.jsonl"), config).Emitted);
        Assert.Equal(0, TraceReplay.Run(Path.Combine(traces, "excluded-app.jsonl"), config).Emitted);
        Assert.Equal(0, TraceReplay.Run(Path.Combine(traces, "security-password.jsonl"),
            new TraySettings { Enabled = false, MoreIsBetter = true }.ToPolicy()).Emitted);
        Assert.Empty(TraceReplay.Run(Path.Combine(traces, "caption-close-hover.jsonl"),
            new TraySettings { Style = EffectProfile.Quiet, MoreIsBetter = true }.ToPolicy()).Haptic);
    }

    [Fact]
    public void MoreIsBetterDefaultsOffAndExistingSettingsStayFocused()
    {
        var settings = new TraySettings();
        Assert.False(settings.MoreIsBetter);
        var config = settings.ToPolicy();
        Assert.Equal(EffectProfile.Focused, config.Profile);
        Assert.Equal(1200, config.LandingSpeedPxPerSec);
        Assert.Equal(0, config.DensityLandsPer2s);
        Assert.Equal(12, config.GlobalMaxPulsesPerSecond);
        Assert.Equal(600, config.MaxPulsesPerMinute);
        Assert.Equal(40, config.MinimumGlobalSpacingMs);
        Assert.Equal(200, config.SameElementReentryCooldownMs);

        var oldSettings = JsonSerializer.Deserialize<TraySettings>("""{"style":"Focused"}""", HapticsJson.Indented)!;
        Assert.False(oldSettings.MoreIsBetter);
        Assert.True(JsonSerializer.SerializeToElement(new TraySettings { MoreIsBetter = true }, HapticsJson.Indented)
            .GetProperty("moreIsBetter").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrayGroupLinksRemainFocusedOnlyWhileInlineLinksStayQuiet(bool moreIsBetter)
    {
        var trace = Path.Combine(ArtifactLocator.FindRepoRoot(AppContext.BaseDirectory)!, "tests", "traces",
            "focused-group-link-filter.jsonl");
        foreach (var style in new[] { EffectProfile.Quiet, EffectProfile.Focused, EffectProfile.Everything })
        {
            var output = TraceReplay.Run(trace, new TraySettings { Style = style, MoreIsBetter = moreIsBetter }.ToPolicy());
            Assert.Equal(style switch
            {
                EffectProfile.Quiet => 1,
                EffectProfile.Focused => 2,
                _ => 4,
            }, output.Emitted);
            Assert.All(output.Haptic, d => Assert.Equal("Hover", d.Waveform));
        }
    }

    [Fact]
    public void MoreIsBetterAllowsTwentyFivePulsesPerSecond()
    {
        var config = new TraySettings { MoreIsBetter = true }.ToPolicy();
        var policy = new HapticPolicy(config, _ => "app")
        {
            ConsentGiven = true,
            OutputRouteVerified = true,
        };
        var emitted = 0;
        for (var i = 0; i < 50; i++)
        {
            var t = 40 + i * 20.0;
            var key = $"fast-{i}";
            var element = new ElementSnapshot { ProcessId = 1, ControlType = "ComboBox", RuntimeIdHash = key };
            var e = new SemanticEvent(SemanticEventKind.EnterControl, i + 1, t, t, 1, "ComboBox", key, null, 0.9, [], false, 0, element);
            if (policy.Evaluate(e, t).Outcome == PolicyOutcome.Emit)
            {
                emitted++;
            }
        }

        Assert.Equal(25, emitted);
    }

    [Fact]
    public void OffMeansOff()
    {
        var cfg = new TraySettings { Enabled = false }.ToPolicy();
        Assert.Equal(HapticsMode.Off, cfg.Mode);
    }
}
