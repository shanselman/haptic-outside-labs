using Haptics.Lab;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;
using WpfControls = System.Windows.Controls;

namespace Haptics.Windows.Tests;

public class ShowcaseTests
{
    [Fact]
    public void SetupExplainsPrimingAndActivityDoesNotClaimPhysicalEvidence()
    {
        OnStaThread(() =>
        {
            using var window = new ShowcaseWindow();
            var controls = Descendants(window).ToList();
            var text = controls.OfType<WpfControls.TextBlock>().Select(c => c.Text)
                .Concat(controls.OfType<System.Windows.Documents.Run>().Select(r => r.Text)).ToList();
            Assert.Contains("Start here", text);
            Assert.Contains(text, t => t.Contains("1. Connect:", StringComparison.Ordinal));
            Assert.Contains(text, t => t.Contains("2. Feel:", StringComparison.Ordinal));
            Assert.Contains(text, t => t.Contains("3. Explore:", StringComparison.Ordinal));
            Assert.Equal("Engine activity", window.ActivityExpander.Header);
            Assert.Contains(text, t => t.Contains("not proof of a felt pulse", StringComparison.Ordinal));
            Assert.DoesNotContain("What you just felt", text);
            Assert.Equal("Haptic intensity", AutomationProperties.GetName(window.IntensitySlider));
            window.IntensitySlider.Value = 3;
            Assert.Equal("30%", window.IntensityLabel.Text);
            Assert.Equal(1, window.StyleBox.SelectedIndex);
            window.StyleBox.SelectedIndex = 0;
            Assert.Equal("Structure only", window.StyleHint.Text);
            Assert.Equal(7, window.VocabularyPanel.Children.Count);
            Assert.NotNull(window.InspectorHost.Child);
            Assert.NotNull(window.LabHost.Child);
            Assert.Null(window.ResultPath);
        });
    }

    [Fact]
    public void AccessibilityTogglesAndBlindingDoNotNeedClicks() => OnStaThread(() =>
    {
        using var window = new ShowcaseWindow();
        var peer = new ToggleButtonAutomationPeer(window.BoostSwitch);
        ((IToggleProvider)peer).Toggle();
        Assert.False(window.BoostSwitch.IsChecked);
        ((IToggleProvider)peer).Toggle();
        Assert.True(window.BoostSwitch.IsChecked);

        window.SetComparisonPresentation(true);
        Assert.Equal(Visibility.Hidden, window.FeelSettings.Visibility);
        Assert.False(window.VocabularyPanel.IsEnabled);
        Assert.False(window.InspectorTab.IsEnabled);
        Assert.False(window.LabTab.IsEnabled);
        Assert.Equal(Visibility.Hidden, window.ActivityList.Visibility);
        Assert.Equal("Stop comparison", window.CompareButton.Content);
        Assert.True(window.EngineSwitch.IsEnabled); // Off and Stop remain available.
        window.SetComparisonPresentation(false);
        Assert.Equal(Visibility.Visible, window.FeelSettings.Visibility);
        Assert.True(window.VocabularyPanel.IsEnabled);
        Assert.True(window.InspectorTab.IsEnabled);
        Assert.True(window.LabTab.IsEnabled);
        Assert.Equal(Visibility.Visible, window.ActivityList.Visibility);
        Assert.Equal("Compare styles", window.CompareButton.Content);
    });

    [Fact]
    public void FailedComparisonCreationReportsTheFilesystemErrorWithoutStarting()
    {
        var root = Haptics.Core.ArtifactLocator.FindRepoRoot(AppContext.BaseDirectory)!;
        var directory = Path.Combine(root, "artifacts", "showcase-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var blocked = Path.Combine(directory, "blocked-run");
            File.WriteAllText(blocked, "A file cannot also be the run directory.");
            OnStaThread(() =>
            {
                using var window = new ShowcaseWindow();
                var errors = new List<(string Title, string Message)>();
                window.ReportOperationError = (title, message) => errors.Add((title, message));
                window.StyleBox.SelectedIndex = 2;
                window.IntensitySlider.Value = 3;
                Assert.False(window.TryStartComparison(() => ExperimentSession.Start("EXP-090", "physical", blocked)));
                Assert.Equal("Couldn't start the comparison", Assert.Single(errors).Title);
                Assert.Contains("no result was reported", errors[0].Message);
                Assert.Contains(blocked, errors[0].Message);
                Assert.Null(window.ResultPath);
                Assert.Equal(Visibility.Visible, window.FeelSettings.Visibility);
                Assert.Equal("Compare styles", window.CompareButton.Content);
                Assert.True(window.InspectorTab.IsEnabled);
                Assert.True(window.VocabularyPanel.IsEnabled);
                Assert.Equal(2, window.StyleBox.SelectedIndex);
                Assert.Equal(3, window.IntensitySlider.Value);
                Assert.True(window.EngineSwitch.IsChecked);
                Assert.False(File.Exists(Path.Combine(directory, "result.json")));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeStartupFailureRollsBackAndReportsWithoutStartingWorkers(bool wrapped) => OnStaThread(() =>
    {
        using var window = new ShowcaseWindow();
        var errors = new List<(string Title, string Message)>();
        window.ReportOperationError = (title, message) => errors.Add((title, message));
        ObservationPipeline? attempted = null;
        Assert.False(window.TryStartEngine(pipeline =>
        {
            attempted = pipeline;
            var error = new System.ComponentModel.Win32Exception(5, "Test startup denied");
            throw wrapped ? new InvalidOperationException("Message window thread failed to start.", error) : error;
        }));
        Assert.NotNull(attempted);
        Assert.False(attempted.Running);
        Assert.False(attempted.Policy.ConsentGiven);
        Assert.False(attempted.Policy.OutputRouteVerified);
        Assert.False(window.EngineSwitch.IsChecked);
        Assert.Equal("Showcase engine off", window.StatusText.Text);
        Assert.Equal("Couldn't start the Showcase engine", Assert.Single(errors).Title);
        Assert.Contains("Test startup denied", errors[0].Message);
        Assert.Contains("engine is off", errors[0].Message);
        Assert.Null(window.ResultPath);
    });

    [Fact]
    public void UnexpectedOperationErrorsStillPropagateAfterEngineRollback() => OnStaThread(() =>
    {
        using var window = new ShowcaseWindow();
        var reported = false;
        window.ReportOperationError = (_, _) => reported = true;
        ObservationPipeline? attempted = null;
        var error = new InvalidOperationException("Unexpected handler bug");
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => window.TryStartEngine(pipeline =>
        {
            attempted = pipeline;
            throw error;
        })));
        Assert.NotNull(attempted);
        Assert.False(attempted.Policy.ConsentGiven);
        Assert.False(window.EngineSwitch.IsChecked);
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => window.TryStartComparison(() => throw error)));
        Assert.False(reported);
        Assert.Null(window.ResultPath);
    });

    [Fact]
    public void DemoKeepsRealAccessibleSemantics() => OnStaThread(() =>
    {
        var demo = new ShowcaseDemo();
        var sliderPeer = new SliderAutomationPeer(demo.DemoSlider);
        Assert.Equal(AutomationControlType.Slider, sliderPeer.GetAutomationControlType());
        var range = Assert.IsAssignableFrom<IRangeValueProvider>(sliderPeer.GetPattern(PatternInterface.RangeValue));
        Assert.Equal(0, range.Minimum);
        Assert.Equal(100, range.Maximum);
        Assert.Equal(50, range.Value);
        Assert.Equal(AutomationControlType.Tab, new TabControlAutomationPeer(demo.DemoTabs).GetAutomationControlType());
        Assert.Equal(0, demo.DemoTabs.SelectedIndex);
        Assert.True(new PasswordBoxAutomationPeer(demo.DemoPassword).IsPassword());

        var controls = Descendants(demo).ToList();
        Assert.Contains(controls, c => c is WpfControls.Menu);
        Assert.Contains(controls, c => c is WpfControls.ToolBar);
        Assert.Equal(4, controls.OfType<WpfControls.GroupBox>().Count());
        Assert.Equal(3, controls.OfType<WpfControls.RadioButton>().Count());
        Assert.Equal(2, controls.OfType<WpfControls.CheckBox>().Count());
        Assert.Equal(3, controls.OfType<WpfControls.Button>().Count(b => b.Width == 64 && b.Height == 88));
        Assert.Equal(4, controls.OfType<WpfControls.Button>().Count(b => b.Width == 9 && b.Height == 9));
    });

    [Theory]
    [InlineData(1440, 940, false)]
    [InlineData(1440, 940, true)]
    [InlineData(900, 720, false)]
    [InlineData(900, 720, true)]
    public void LayoutAndThemeWorkWithoutShowingAWindow(int width, int height, bool dark) => OnStaThread(() =>
    {
        using var window = new ShowcaseWindow();
        // Local dictionaries only: a WPF Application belongs to the real executable, never to a test thread.
        window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary
        {
            Theme = dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light,
        });
        window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        window.Width = width;
        window.UpdateLayoutForWidth();
        var content = (FrameworkElement)window.Content;
        content.Measure(new System.Windows.Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        Assert.True(window.ExperienceGrid.ActualHeight > 200);
        Assert.True(window.DemoContent.ActualWidth > 300);
        Assert.Equal(width < 1200 ? 1 : 0, WpfControls.Grid.GetRow(window.ActivityCard));
        Assert.Equal(width < 1200 ? 2 : 1, WpfControls.Grid.GetColumnSpan(window.ActivityCard));
        Assert.True(window.StyleBox.ActualWidth >= 130);
        Assert.Equal(256, ((System.Windows.Media.Imaging.BitmapSource)window.HeroImage.Source).PixelWidth);

        Assert.Equal(width >= 1200, window.ActivityExpander.IsExpanded);
    });

    [Fact]
    public void SymbolsFitInOneUtf16Char()
    {
        var dir = Path.Combine(Haptics.Core.ArtifactLocator.FindRepoRoot(AppContext.BaseDirectory)!, "src", "Haptics.Lab");
        var source = File.ReadAllText(Path.Combine(dir, "ShowcaseWindow.xaml"));
        var names = System.Text.RegularExpressions.Regex.Matches(source, @"(?:\{ui:SymbolIcon |Symbol="")(\w+)").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.True((int)Enum.Parse<Wpf.Ui.Controls.SymbolRegular>(n) <= 0xFFFF, n));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    internal static void OnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "STA test timed out.");
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
