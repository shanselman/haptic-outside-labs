using Haptics.Core;
using Haptics.Tray;

namespace Haptics.Windows.Tests;

public class TrayDefaultsTests
{
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
    public void SampleIsMoreAggressiveThanTheLabDefaults()
    {
        var traces = Path.Combine(ArtifactLocator.FindRepoRoot(AppContext.BaseDirectory)!, "tests", "traces");
        var lab = TraceReplay.Run(Path.Combine(traces, "toolbar-fast-flick.jsonl")).Emitted;
        var sample = TraceReplay.Run(Path.Combine(traces, "toolbar-fast-flick.jsonl"), new TraySettings().ToPolicy()).Emitted;
        Assert.True(sample > lab, $"sample {sample} vs lab {lab}");
    }

    [Fact]
    public void OffMeansOff()
    {
        var cfg = new TraySettings { Enabled = false }.ToPolicy();
        Assert.Equal(HapticsMode.Off, cfg.Mode);
    }
}
