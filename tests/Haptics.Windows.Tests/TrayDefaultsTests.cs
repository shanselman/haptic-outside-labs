using System.Text.Json;
using Haptics.Core;
using Haptics.Tray;

namespace Haptics.Windows.Tests;

public class TrayDefaultsTests
{
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
    public void OffMeansOff()
    {
        var cfg = new TraySettings { Enabled = false }.ToPolicy();
        Assert.Equal(HapticsMode.Off, cfg.Mode);
    }
}
