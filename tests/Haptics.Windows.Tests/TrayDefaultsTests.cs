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
    [InlineData(true)]
    [InlineData(false)]
    public void ToPolicyMapsCloseButtonHover(bool enabled)
    {
        var cfg = new TraySettings { CloseButtonHover = enabled }.ToPolicy();
        Assert.Equal(enabled, cfg.CloseButtonHoverEnabled);
    }

    [Fact]
    public void CloseHoverSettingControlsCaptionReplay()
    {
        var traces = Path.Combine(ArtifactLocator.FindRepoRoot(AppContext.BaseDirectory)!, "tests", "traces");
        var path = Path.Combine(traces, "caption-close-hover.jsonl");
        var on = TraceReplay.Run(path, new TraySettings().ToPolicy());
        Assert.Equal(["Hover", "Hover", "Hover"], on.Haptic.Select(d => d.Waveform));
        Assert.Equal(["Button", "Button", "Button"], on.Haptic.Select(d => d.ControlType));
        Assert.DoesNotContain(on.Decisions, d => d.Reason == "coalesced-with-group-boundary");
        Assert.Contains(on.Decisions, d => d.Reason == "native-suppression:windows-snap-titlebar-drag");

        var off = TraceReplay.Run(path, new TraySettings { CloseButtonHover = false }.ToPolicy());
        Assert.Equal(["Hover"], off.Haptic.Select(d => d.Waveform));
        Assert.Equal(3, off.Decisions.Count(d => d.Reason == "rule-disabled:close-hover"));
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
        Assert.Contains(replay.Decisions, d => d.Reason == "rule-disabled:close-hover");
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
        Assert.Equal(5, TraceReplay.Run(Path.Combine(traces, "toolbar-slow-sweep.jsonl"), new TraySettings().ToPolicy()).Emitted);
    }

    [Fact]
    public void EverythingStyleIsMoreAggressiveThanTheLabDefaults()
    {
        var traces = Path.Combine(ArtifactLocator.FindRepoRoot(AppContext.BaseDirectory)!, "tests", "traces");
        var lab = TraceReplay.Run(Path.Combine(traces, "toolbar-fast-flick.jsonl")).Emitted;
        var sample = TraceReplay.Run(Path.Combine(traces, "toolbar-fast-flick.jsonl"), new TraySettings { Style = EffectProfile.Everything }.ToPolicy()).Emitted;
        Assert.True(sample >= lab + 3, $"sample {sample} vs lab {lab}");
    }

    [Fact]
    public void MoreIsBetterEmitsOnFastSweepsWithoutChangingSafetySuppressions()
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
        Assert.Equal(1, focused.Emitted);
        var replay = TraceReplay.Run(fastSweep, config);
        Assert.Equal(5, replay.Emitted);

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
        Assert.Equal(10, config.DensityLandsPer2s);
        Assert.Equal(12, config.GlobalMaxPulsesPerSecond);
        Assert.Equal(600, config.MaxPulsesPerMinute);
        Assert.Equal(40, config.MinimumGlobalSpacingMs);
        Assert.Equal(200, config.SameElementReentryCooldownMs);

        var oldSettings = JsonSerializer.Deserialize<TraySettings>("""{"style":"Focused"}""", HapticsJson.Indented)!;
        Assert.False(oldSettings.MoreIsBetter);
        Assert.True(JsonSerializer.SerializeToElement(new TraySettings { MoreIsBetter = true }, HapticsJson.Indented)
            .GetProperty("moreIsBetter").GetBoolean());
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
            var element = new ElementSnapshot { ProcessId = 1, ControlType = "Button", RuntimeIdHash = key };
            var e = new SemanticEvent(SemanticEventKind.EnterControl, i + 1, t, t, 1, "Button", key, null, 0.9, [], false, 0, element);
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
