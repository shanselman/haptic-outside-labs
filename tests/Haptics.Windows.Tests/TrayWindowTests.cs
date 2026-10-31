using Haptics.Core;
using Haptics.Tray;
using WpfButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using WpfInput = System.Windows.Input;

namespace Haptics.Windows.Tests;

/// <summary>The tray's settings window, driven through a fake host: no registry, no settings file, no haptics.</summary>
public class TrayWindowTests
{
    [Fact]
    public void CloseHoverToggleAppliesAccessibleChangesAndRefreshesWithoutApplying() => ShowcaseTests.OnStaThread(() =>
    {
        var host = new FakeHost();
        var w = new SettingsWindow(host, autoClose: false);
        var toggle = Assert.IsType<Wpf.Ui.Controls.ToggleSwitch>(w.FindName("CloseHoverSwitch"));
        Assert.True(toggle.IsChecked);
        Assert.Equal("Close button hover", System.Windows.Automation.AutomationProperties.GetName(toggle));
        Assert.Equal(0, host.Applied);

        Toggle(toggle);
        Assert.False(host.Settings.CloseButtonHover);
        Assert.Equal(1, host.Applied);
        Toggle(toggle);
        Assert.True(host.Settings.CloseButtonHover);
        Assert.Equal(2, host.Applied);
        host.Settings.CloseButtonHover = false;
        host.Raise();
        Assert.False(toggle.IsChecked);
        Assert.Equal(2, host.Applied);
    });

    [Fact]
    public void WindowShowsSettingsAndStatus() => ShowcaseTests.OnStaThread(() =>
    {
        var host = new FakeHost();
        var w = new SettingsWindow(host, autoClose: false);

        Assert.True(w.EnabledSwitch.IsChecked);
        Assert.Equal(Array.IndexOf(TraySettings.Styles, host.Settings.Style), w.StyleBox.SelectedIndex);
        Assert.Equal(host.Settings.IntensityScale, w.IntensitySlider.Value);
        Assert.Equal(TraySettings.IntensityName(host.Settings.IntensityScale), w.IntensityLabel.Text);
        Assert.Equal("Waiting for your haptic device", w.StatusText.Text);
        Assert.Contains("fallback", w.StatusHint.Text);
        Assert.Contains(host.DiscoveryStatus, w.StatsText.Text);
        host.DiscoveryStatus = "Paused — another Haptics Everywhere engine owns output.";
        host.Raise();
        Assert.Contains("Paused — another Haptics Everywhere engine owns output", w.StatsText.Text);
        Assert.Equal(256, ((System.Windows.Media.Imaging.BitmapSource)w.HeroImage.Source).PixelWidth);
        Assert.Equal(0, host.Applied); // Opening the window changes nothing.

        host.Primed = true;
        host.Raise();
        Assert.Equal("Ready: Mouse", w.StatusText.Text);
    });

    [Fact]
    public void ControlsChangeSettingsThroughTheHost() => ShowcaseTests.OnStaThread(() =>
    {
        var host = new FakeHost();
        var w = new SettingsWindow(host, autoClose: false);

        w.StyleBox.SelectedIndex = 2;
        Assert.Equal(TraySettings.Styles[2], host.Settings.Style);
        Assert.StartsWith("Every crossing", w.StyleHint.Text);
        w.IntensitySlider.Value = 0.3;
        Assert.Equal(0.3, host.Settings.IntensityScale);
        Assert.Equal("Low", w.IntensityLabel.Text);
        Toggle(w.BoostSwitch);
        Assert.Equal(1.0, host.Settings.TouchpadBoost);
        Toggle(w.CooperationSwitch);
        Assert.False(host.Settings.CooperationEnabled);
        Toggle(w.StartupSwitch);
        Assert.True(host.StartWithWindows);
        Assert.Equal(4, host.Applied);

        Assert.Equal(System.Windows.Visibility.Visible, w.ExcludeButton.Visibility);
        Click(w.ExcludeButton);
        Assert.Equal(["notepad"], host.Settings.ExcludedProcesses);
        Assert.Equal(System.Windows.Visibility.Collapsed, w.ExcludeButton.Visibility);
        Assert.Equal("notepad never buzzes", w.ExcludedSummary.Text);
        var row = Assert.IsType<System.Windows.Controls.Grid>(Assert.Single(w.ExcludedList.Children.Cast<object>()));
        Click(row.Children.OfType<Wpf.Ui.Controls.Button>().Single());
        Assert.Empty(host.Settings.ExcludedProcesses);
        Assert.Equal(System.Windows.Visibility.Visible, w.ExcludeButton.Visibility);

        Click(w.ShowcaseButton);
        Assert.Equal(1, host.Showcases);
        Toggle(w.EnabledSwitch);
        Assert.False(host.Settings.Enabled);
        Assert.Equal("Off", w.StatusText.Text);
    });

    [Fact]
    public void MovingOverTheWindowPrimesAndUsingItKeepsItOpen() => ShowcaseTests.OnStaThread(() =>
    {
        var host = new FakeHost();
        var w = new SettingsWindow(host, autoClose: true);
        Assert.True(w.Topmost);

        w.RaiseEvent(new WpfInput.MouseEventArgs(WpfInput.Mouse.PrimaryDevice, 0) { RoutedEvent = WpfInput.Mouse.PreviewMouseMoveEvent });
        w.RaiseEvent(new WpfInput.MouseEventArgs(WpfInput.Mouse.PrimaryDevice, 0) { RoutedEvent = WpfInput.Mouse.PreviewMouseMoveEvent });
        Assert.Equal(1, host.Primes); // Throttled: a wiggle is many moves.

        host.Primed = true;
        host.Raise();
        Assert.Contains("closes by itself", w.StatusHint.Text);

        w.RaiseEvent(new WpfInput.MouseButtonEventArgs(WpfInput.Mouse.PrimaryDevice, 0, WpfInput.MouseButton.Left) { RoutedEvent = WpfInput.Mouse.PreviewMouseDownEvent });
        Assert.False(w.Topmost);
        Assert.DoesNotContain("closes by itself", w.StatusHint.Text);
    });

    [Fact]
    public void IconsComeFromTheEmbeddedAppIcon() => ShowcaseTests.OnStaThread(() =>
    {
        foreach (var state in Enum.GetValues<TrayState>())
        {
            using var icon = AppIcon.ForTray(state);
            Assert.Equal(SystemInformation.SmallIconSize, icon.Size);
        }

        Assert.Equal(16, AppIcon.Frame(16).PixelWidth);
        Assert.Equal(256, AppIcon.Frame(4096).PixelWidth); // Falls back to the largest frame.
    });

    [Fact]
    public void TrayStatusDistinguishesPausedOutputFromOffAndReady()
    {
        Assert.Equal("Paused — another engine owns output", TrayContext.StatusDescription(true, true, true, "Mouse"));
        Assert.Equal("Off", TrayContext.StatusDescription(false, false, false, "None"));
        Assert.Equal("On (Mouse)", TrayContext.StatusDescription(true, false, true, "Mouse"));
    }

    [Fact]
    public void SymbolsFitInOneUtf16Char()
    {
        // WPF-UI draws a symbol as (char)value, so newer icons above U+FFFF (e.g. HapticStrong24) render as junk.
        var dir = Path.Combine(ArtifactLocator.FindRepoRoot(AppContext.BaseDirectory)!, "src", "Haptics.Tray");
        var source = File.ReadAllText(Path.Combine(dir, "SettingsWindow.xaml")) + File.ReadAllText(Path.Combine(dir, "SettingsWindow.xaml.cs"));
        var names = System.Text.RegularExpressions.Regex.Matches(source, @"(?:SymbolIcon |SymbolRegular\.)(\w+)").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.True((int)Enum.Parse<Wpf.Ui.Controls.SymbolRegular>(n) <= 0xFFFF, n));
    }

    // What Narrator does: the UIA toggle flips IsChecked without raising Click.
    private static void Toggle(System.Windows.Controls.Primitives.ToggleButton toggle) =>
        ((System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(toggle)).Toggle();

    private static void Click(System.Windows.UIElement element) =>
        element.RaiseEvent(new System.Windows.RoutedEventArgs(WpfButtonBase.ClickEvent));

    private sealed class FakeHost : ITrayHost
    {
        public event EventHandler? Changed;

        public TraySettings Settings { get; } = new();

        public bool Primed { get; set; }

        public string Devices => "Mouse";

        public string DiscoveryStatus { get; set; } = "Automatic lookup unavailable; manual priming remains available.";

        public string? LastApp => "notepad";

        public bool StartWithWindows { get; set; }

        public PipelineStats? Stats => null;

        public double? LandingSpeed => null;

        public int Applied { get; private set; }

        public int Primes { get; private set; }

        public int Showcases { get; private set; }

        public void Apply()
        {
            Applied++;
            Raise();
        }

        public void TryPrime() => Primes++;

        public void OpenShowcase() => Showcases++;

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
