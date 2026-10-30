using System.Collections;
using System.Reflection;
using System.Windows.Automation;

namespace Haptics.Windows.Tests;

public class UiaCacheTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerReadsWpfSliderRangeButNotButtonRange(bool fixture)
    {
        if (!Environment.UserInteractive || Environment.GetEnvironmentVariable("CI") is not null)
        {
            return; // Real UIA provider coverage needs an interactive desktop, like EXP-040.
        }

        var ready = new TaskCompletionSource<(System.Windows.Threading.Dispatcher Dispatcher,
            System.Windows.Point Slider, System.Windows.Point Button)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            try
            {
                var slider = new System.Windows.Controls.Slider
                {
                    Minimum = 0, Maximum = 100, Value = 5, SmallChange = 1, LargeChange = 10,
                    Width = 300, Height = 50,
                };
                var button = new System.Windows.Controls.Button { Width = 300, Height = 50 };
                System.Windows.Automation.AutomationProperties.SetAutomationId(slider, "fixture-slider");
                System.Windows.Automation.AutomationProperties.SetAutomationId(button, "fixture-button");
                var panel = new System.Windows.Controls.StackPanel();
                panel.Children.Add(slider);
                panel.Children.Add(button);
                var window = new System.Windows.Window
                {
                    Content = panel, Width = 340, Height = 160, Left = 80, Top = 80,
                    ShowActivated = false, Topmost = true,
                };
                window.Show();
                window.UpdateLayout();
                ready.SetResult((dispatcher, slider.PointToScreen(new System.Windows.Point(150, 25)),
                    button.PointToScreen(new System.Windows.Point(150, 25))));
                System.Windows.Threading.Dispatcher.Run();
                window.Close();
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var ui = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var client = new UiaClient(Path.Combine(AppContext.BaseDirectory, "haptics-lab.exe"),
                [UiaWorker.Arg], new UiaClientOptions { IncludeAutomationId = fixture, TimeoutMs = 2000 });
            var slider = await client.QueryAsync((int)ui.Slider.X, (int)ui.Slider.Y).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(slider.Ok, slider.Error);
            Assert.Equal("Slider", slider.Snapshot?.ControlType);
            Assert.Equal(new Haptics.Core.RangeInfo(5, 0, 100, 1, 10), slider.Snapshot?.Range);
            Assert.Equal(fixture ? "fixture-slider" : null, slider.Snapshot?.AutomationId);
            var button = await client.QueryAsync((int)ui.Button.X, (int)ui.Button.Y).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(button.Ok, button.Error);
            Assert.Equal("Button", button.Snapshot?.ControlType);
            Assert.Null(button.Snapshot?.Range);
            Assert.Equal(fixture ? "fixture-button" : null, button.Snapshot?.AutomationId);
            Assert.NotNull(button.Snapshot?.ParentControlType);
            Assert.NotNull(button.Snapshot?.ParentBounds);
        }
        finally
        {
            ui.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "UIA fixture thread did not exit.");
        }
    }

    [Fact]
    public void ProductionCacheDoesNotAcquireAutomationIdOrRangeValues()
    {
        var cache = BuildCache(false);
        Assert.DoesNotContain(AutomationElement.AutomationIdProperty, Properties(cache));
        Assert.DoesNotContain(AutomationElement.IsRangeValuePatternAvailableProperty, Properties(cache));
        Assert.DoesNotContain(RangeValuePattern.Pattern, Patterns(cache));
        Assert.DoesNotContain(RangeValuePattern.ValueProperty, Properties(cache));
        Assert.DoesNotContain(RangeValuePattern.MinimumProperty, Properties(cache));
        Assert.DoesNotContain(RangeValuePattern.MaximumProperty, Properties(cache));
        Assert.DoesNotContain(RangeValuePattern.SmallChangeProperty, Properties(cache));
        Assert.DoesNotContain(RangeValuePattern.LargeChangeProperty, Properties(cache));
    }

    [Fact]
    public void FixtureCacheAddsOnlyAutomationIdToProductionProperties()
    {
        var production = Properties(BuildCache(false));
        var fixture = Properties(BuildCache(true));
        Assert.Contains(AutomationElement.AutomationIdProperty, fixture);
        Assert.Equal(new[] { AutomationElement.AutomationIdProperty }, fixture.Except(production));
    }

    [Fact]
    public void ProductionCacheKeepsStructuralParentAndSelectionMetadata()
    {
        var properties = Properties(BuildCache(false));
        Assert.Contains(AutomationElement.ControlTypeProperty, properties);
        Assert.Contains(AutomationElement.BoundingRectangleProperty, properties);
        Assert.Contains(AutomationElement.RuntimeIdProperty, properties);
        Assert.Contains(AutomationElement.ClassNameProperty, properties);
        Assert.Contains(AutomationElement.IsSelectionItemPatternAvailableProperty, properties);
        Assert.Contains(SelectionItemPattern.IsSelectedProperty, properties);
    }

    [Fact]
    public void SliderCacheAcquiresOnlyReadOnlyRangeMetadata()
    {
        var method = typeof(UiaWorker).GetMethod("BuildRangeCacheRequest", BindingFlags.Static | BindingFlags.NonPublic)!;
        var cache = (CacheRequest)method.Invoke(null, null)!;
        Assert.Equal(AutomationElementMode.None, cache.AutomationElementMode);
        Assert.Empty(Patterns(cache));
        Assert.Equal(new[]
        {
            AutomationElement.RuntimeIdProperty,
            AutomationElement.IsRangeValuePatternAvailableProperty,
            RangeValuePattern.ValueProperty, RangeValuePattern.MinimumProperty, RangeValuePattern.MaximumProperty,
            RangeValuePattern.SmallChangeProperty, RangeValuePattern.LargeChangeProperty,
        }, Properties(cache));
    }

    private static CacheRequest BuildCache(bool includeAutomationId)
    {
        var method = typeof(UiaWorker).GetMethod("BuildCacheRequest", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (CacheRequest)method.Invoke(null, method.GetParameters().Length == 0 ? null : [includeAutomationId])!;
    }

    private static AutomationProperty[] Properties(CacheRequest cache) =>
        ((IEnumerable)typeof(CacheRequest).GetField("_properties", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cache)!).Cast<AutomationProperty>().ToArray();

    private static AutomationPattern[] Patterns(CacheRequest cache) =>
        ((IEnumerable)typeof(CacheRequest).GetField("_patterns", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cache)!).Cast<AutomationPattern>().ToArray();
}
