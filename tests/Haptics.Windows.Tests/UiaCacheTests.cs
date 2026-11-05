using System.Collections;
using System.Reflection;
using System.Windows.Automation;

namespace Haptics.Windows.Tests;

[Collection(nameof(DesktopUiaCollection))]
public class UiaCacheTests
{
    [Theory]
    [InlineData("safe")]
    [InlineData("disabled")]
    [InlineData("password")]
    public async Task DropdownArrowKeepsChildSafetyWithoutReadingGrandparent(string flag)
    {
        if (!Environment.UserInteractive || Environment.GetEnvironmentVariable("CI") is not null)
        {
            return;
        }

        var ready = new TaskCompletionSource<(System.Windows.Threading.Dispatcher Dispatcher, System.Windows.Point Point)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            try
            {
                var arrow = new DropdownArrow(flag) { Width = 60, Height = 30 };
                var owner = new DropdownOwner { Width = 180, Height = 70 };
                owner.Children.Add(arrow);
                var window = new System.Windows.Window
                {
                    Content = owner, Width = 260, Height = 180, Left = 80, Top = 80,
                    ShowActivated = false, Topmost = true,
                };
                window.Show();
                window.UpdateLayout();
                ready.SetResult((dispatcher, arrow.PointToScreen(new System.Windows.Point(30, 15))));
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
                [UiaWorker.Arg], new UiaClientOptions { TimeoutMs = 2000 });
            client.ObserveCaptions = static () => false;
            var response = await client.QueryAsync((int)ui.Point.X, (int)ui.Point.Y, focused: true)
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(response.Ok, response.Error);
            var snapshot = Assert.IsType<Haptics.Core.ElementSnapshot>(response.Snapshot);
            Assert.Equal("ComboBox", snapshot.ControlType);
            Assert.Equal(Environment.ProcessId, snapshot.ProcessId);
            Assert.Null(snapshot.ParentControlType);
            Assert.Null(snapshot.ParentBounds);
            Assert.Null(snapshot.ParentRuntimeIdHash);
            Assert.Equal(flag != "disabled", snapshot.IsEnabled);
            Assert.Equal(flag == "offscreen", snapshot.IsOffscreen);
            Assert.Equal(flag == "password", snapshot.IsPassword);
        }
        finally
        {
            ui.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "UIA fixture thread did not exit.");
        }
    }

    private sealed class DropdownOwner : System.Windows.Controls.StackPanel
    {
        protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new DropdownOwnerPeer(this);
    }

    private sealed class DropdownOwnerPeer(DropdownOwner owner) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(owner)
    {
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() =>
            System.Windows.Automation.Peers.AutomationControlType.ComboBox;
        protected override string GetClassNameCore() => nameof(DropdownOwner);
        protected override bool IsControlElementCore() => true;
    }

    private sealed class DropdownArrow(string flag) : System.Windows.Controls.Button
    {
        protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new DropdownArrowPeer(this, flag);
    }

    private sealed class DropdownArrowPeer(DropdownArrow owner, string flag) : System.Windows.Automation.Peers.ButtonAutomationPeer(owner)
    {
        protected override bool IsEnabledCore() => flag != "disabled";
        protected override bool IsOffscreenCore() => flag == "offscreen";
        protected override bool IsPasswordCore() => flag == "password";
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SliderThumbIsDistinctFromTrackWithStableOwnerAndRange(bool showcaseTheme, bool vertical)
    {
        if (!Environment.UserInteractive || Environment.GetEnvironmentVariable("CI") is not null)
        {
            return;
        }

        var ready = new TaskCompletionSource<(System.Windows.Threading.Dispatcher Dispatcher,
            System.Windows.Controls.Slider Slider)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            try
            {
                var slider = new System.Windows.Controls.Slider
                {
                    Minimum = 0, Maximum = 100, Value = 50, SmallChange = 1, LargeChange = 10,
                    Width = vertical ? 50 : 300, Height = vertical ? 300 : 50,
                    Orientation = vertical ? System.Windows.Controls.Orientation.Vertical : System.Windows.Controls.Orientation.Horizontal,
                };
                var window = new System.Windows.Window
                {
                    Content = slider, Width = 340, Height = 380, Left = 80, Top = 80,
                    ShowActivated = false, Topmost = true,
                };
                if (showcaseTheme)
                {
                    window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary
                        { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light });
                    window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                }
                window.Show();
                window.UpdateLayout();
                ready.SetResult((dispatcher, slider));
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
                [UiaWorker.Arg], new UiaClientOptions { TimeoutMs = 2000 });
            string? identity = null;
            foreach (var value in new[] { 20, 50, 80 })
            {
                var points = await ui.Dispatcher.InvokeAsync(() =>
                {
                    ui.Slider.Value = value;
                    ui.Slider.UpdateLayout();
                    var track = Assert.IsType<System.Windows.Controls.Primitives.Track>(
                        ui.Slider.Template.FindName("PART_Track", ui.Slider));
                    var thumb = track.Thumb;
                    return (Thumb: thumb.PointToScreen(new System.Windows.Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2)),
                        Rail: ui.Slider.PointToScreen(new System.Windows.Point(vertical ? 25 : 10, vertical ? 10 : 25)));
                });
                var thumbResponse = await client.QueryAsync((int)points.Thumb.X, (int)points.Thumb.Y, focused: true)
                    .WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(thumbResponse.Ok, thumbResponse.Error);
                var thumbSnapshot = Assert.IsType<Haptics.Core.ElementSnapshot>(thumbResponse.Snapshot);
                Assert.True(thumbSnapshot.IsSliderThumb,
                    $"Expected thumb at {points.Thumb}; got {thumbSnapshot.ControlType}/{thumbSnapshot.ParentControlType}");
                Assert.Equal(value, thumbSnapshot.Range?.Value);
                Assert.True(thumbSnapshot.SliderThumbBounds?.Contains(points.Thumb.X, points.Thumb.Y));
                identity ??= thumbSnapshot.ElementKey;
                Assert.Equal(identity, thumbSnapshot.ElementKey);
                var railResponse = await client.QueryAsync((int)points.Rail.X, (int)points.Rail.Y, focused: true)
                    .WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(railResponse.Ok, railResponse.Error);
                var railSnapshot = Assert.IsType<Haptics.Core.ElementSnapshot>(railResponse.Snapshot);
                Assert.Equal("Slider", railSnapshot.ControlType);
                Assert.Equal(identity, railSnapshot.ElementKey);
                Assert.False(railSnapshot.IsSliderThumb);
                Assert.Equal(value, railSnapshot.Range?.Value);
            }
        }
        finally
        {
            ui.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "UIA fixture thread did not exit.");
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ToggleButtonTextAndImageShareOwnerIdentityAndSafety(bool image, bool showcaseTheme)
    {
        if (!Environment.UserInteractive || Environment.GetEnvironmentVariable("CI") is not null)
        {
            return;
        }

        var ready = new TaskCompletionSource<(System.Windows.Threading.Dispatcher Dispatcher,
            System.Windows.Controls.Primitives.ToggleButton Button, System.Windows.Point[] Points)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            try
            {
                System.Windows.FrameworkElement content = image
                    ? new System.Windows.Controls.Image
                    {
                        Width = 24, Height = 24,
                        Source = new System.Windows.Media.DrawingImage(new System.Windows.Media.GeometryDrawing(
                            System.Windows.Media.Brushes.Black, null,
                            new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(0, 0, 24, 24)))),
                    }
                    : new System.Windows.Controls.TextBlock { Text = "B" };
                var button = new System.Windows.Controls.Primitives.ToggleButton
                {
                    Content = content, Width = 100, Height = 60, IsChecked = false,
                };
                var window = new System.Windows.Window
                {
                    Content = button, Width = 240, Height = 140, Left = 80, Top = 80,
                    ShowActivated = false, Topmost = true,
                };
                if (showcaseTheme)
                {
                    window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary
                        { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light });
                    window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                }
                window.Show();
                window.UpdateLayout();
                ready.SetResult((dispatcher, button, new[]
                {
                    button.PointToScreen(new System.Windows.Point(8, 30)),
                    content.PointToScreen(new System.Windows.Point(content.ActualWidth / 2, content.ActualHeight / 2)),
                }));
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
                [UiaWorker.Arg], new UiaClientOptions { TimeoutMs = 2000 });
            string? identity = null;
            foreach (var state in new[] { false, true, false })
            foreach (var enabled in new[] { true, false })
            {
                await ui.Dispatcher.InvokeAsync(() => { ui.Button.IsChecked = state; ui.Button.IsEnabled = enabled; });
                foreach (var point in ui.Points)
                {
                    var response = await client.QueryAsync((int)point.X, (int)point.Y, focused: true)
                        .WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.True(response.Ok, response.Error);
                    var snapshot = Assert.IsType<Haptics.Core.ElementSnapshot>(response.Snapshot);
                    Assert.Equal("Button", snapshot.ControlType);
                    Assert.True(snapshot.IsToggleControl);
                    Assert.Equal(state, snapshot.IsToggledOn);
                    Assert.Equal(enabled, snapshot.IsEnabled);
                    Assert.False(snapshot.IsFocusedLanding);
                    identity ??= snapshot.ElementKey;
                    Assert.Equal(identity, snapshot.ElementKey);
                }
            }
        }
        finally
        {
            ui.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "UIA fixture thread did not exit.");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WpfCheckboxGlyphAndLabelKeepTheSameToggleIdentityAndState(bool showcaseTheme)
    {
        if (!Environment.UserInteractive || Environment.GetEnvironmentVariable("CI") is not null)
        {
            return;
        }

        var ready = new TaskCompletionSource<(System.Windows.Threading.Dispatcher Dispatcher,
            System.Windows.Controls.CheckBox Checkbox, System.Windows.Point[] Points)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            try
            {
                var checkbox = new System.Windows.Controls.CheckBox
                {
                    Content = "Bold", Width = 120, Height = 32, IsChecked = false,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                };
                var window = new System.Windows.Window
                {
                    Content = checkbox, Width = 240, Height = 120, Left = 80, Top = 80,
                    ShowActivated = false, Topmost = true,
                };
                if (showcaseTheme)
                {
                    window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary
                        { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light });
                    window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                }
                window.Show();
                window.UpdateLayout();
                ready.SetResult((dispatcher, checkbox, new[] { 7, 24, 34 }.Select(x =>
                    checkbox.PointToScreen(new System.Windows.Point(x, 16))).ToArray()));
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
                [UiaWorker.Arg], new UiaClientOptions { TimeoutMs = 2000 });
            string? identity = null;
            foreach (var state in new[] { false, true, false, true, false })
            {
                await ui.Dispatcher.InvokeAsync(() => ui.Checkbox.IsChecked = state);
                foreach (var point in ui.Points)
                {
                    var response = await client.QueryAsync((int)point.X, (int)point.Y, focused: true)
                        .WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.True(response.Ok, response.Error);
                    var snapshot = Assert.IsType<Haptics.Core.ElementSnapshot>(response.Snapshot);
                    Assert.True(snapshot.IsToggleControl,
                        $"Expected checkbox at {point}; got {snapshot.ControlType}/{snapshot.ParentControlType}");
                    Assert.Equal(state, snapshot.IsToggledOn);
                    identity ??= snapshot.ElementKey;
                    Assert.Equal(identity, snapshot.ElementKey);
                }
            }
        }
        finally
        {
            ui.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "UIA fixture thread did not exit.");
        }
    }

    [Fact]
    public async Task WpfMenuAndTabHeadersKeepOneEligibleIdentityAcrossTheirSurface()
    {
        if (!Environment.UserInteractive || Environment.GetEnvironmentVariable("CI") is not null)
        {
            return;
        }

        var ready = new TaskCompletionSource<(System.Windows.Threading.Dispatcher Dispatcher,
            (string Role, System.Windows.Point Point)[] Points)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            try
            {
                var menu = new System.Windows.Controls.Menu();
                var launcher = new System.Windows.Controls.MenuItem { Header = "_File", Width = 100, Height = 32 };
                launcher.Items.Add(new System.Windows.Controls.MenuItem { Header = "_Open" });
                menu.Items.Add(launcher);
                var tabs = new System.Windows.Controls.TabControl();
                tabs.Items.Add(new System.Windows.Controls.TabItem { Header = "Home", IsSelected = true });
                var tab = new System.Windows.Controls.TabItem { Header = "Insert", Width = 100, Height = 32 };
                tabs.Items.Add(tab);
                var panel = new System.Windows.Controls.StackPanel();
                panel.Children.Add(menu);
                panel.Children.Add(tabs);
                var window = new System.Windows.Window
                {
                    Content = panel, Width = 340, Height = 180, Left = 80, Top = 80,
                    ShowActivated = false, Topmost = true,
                };
                window.Show();
                window.UpdateLayout();
                var points = new List<(string, System.Windows.Point)>();
                foreach (var (role, element) in new (string, System.Windows.FrameworkElement)[] { ("MenuItem", launcher), ("TabItem", tab) })
                {
                    foreach (var x in new[] { 5, 20, 45, 75, 95 })
                    {
                        points.Add((role, element.PointToScreen(new System.Windows.Point(x, 16))));
                    }
                }
                ready.SetResult((dispatcher, points.ToArray()));
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
                [UiaWorker.Arg], new UiaClientOptions { TimeoutMs = 2000 });
            var identities = new Dictionary<string, string>();
            var headers = new HashSet<string>();
            foreach (var (role, point) in ui.Points)
            {
                var response = await client.QueryAsync((int)point.X, (int)point.Y, focused: true).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(response.Ok, response.Error);
                var snapshot = Assert.IsType<Haptics.Core.ElementSnapshot>(response.Snapshot);
                Assert.True(snapshot.ControlType == role && snapshot.IsFocusedLanding,
                    $"Expected eligible {role} at {point}; got {snapshot.ControlType}/{snapshot.ParentControlType}, dropdown={snapshot.HasDropdown}");
                Assert.NotEqual(true, snapshot.IsSelected);
                if (identities.TryGetValue(role, out var identity))
                {
                    Assert.Equal(identity, snapshot.ElementKey);
                }
                identities[role] = snapshot.ElementKey;
                if (snapshot.IsHeaderOwner)
                {
                    Assert.Null(snapshot.ParentControlType);
                    headers.Add(role);
                }
            }
            Assert.Contains("MenuItem", headers);
            Assert.Contains("TabItem", headers);
        }
        finally
        {
            ui.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "UIA fixture thread did not exit.");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerReadsWpfSliderRangeAndDistinguishesInlineLinksFromLinkedButtons(bool fixture)
    {
        if (!Environment.UserInteractive || Environment.GetEnvironmentVariable("CI") is not null)
        {
            return; // Real UIA provider coverage needs an interactive desktop, like EXP-040.
        }

        var ready = new TaskCompletionSource<(System.Windows.Threading.Dispatcher Dispatcher,
            System.Windows.Point Slider, System.Windows.Point Button, System.Windows.Point Link,
            System.Windows.Point LinkedButton)>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                var linkText = new System.Windows.Controls.TextBlock { Width = 300, Height = 50 };
                linkText.Inlines.Add(new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Fixture link")));
                var linkedButton = new Wpf.Ui.Controls.HyperlinkButton
                {
                    Content = "Linked button", Width = 300, Height = 50,
                    NavigateUri = "https://example.invalid",
                };
                System.Windows.Automation.AutomationProperties.SetAutomationId(slider, "fixture-slider");
                System.Windows.Automation.AutomationProperties.SetAutomationId(button, "fixture-button");
                var panel = new System.Windows.Controls.StackPanel();
                panel.Children.Add(slider);
                panel.Children.Add(button);
                panel.Children.Add(linkText);
                panel.Children.Add(linkedButton);
                var window = new System.Windows.Window
                {
                    Content = panel, Width = 340, Height = 270, Left = 80, Top = 80,
                    ShowActivated = false, Topmost = true,
                };
                window.Show();
                window.UpdateLayout();
                ready.SetResult((dispatcher, slider.PointToScreen(new System.Windows.Point(150, 25)),
                    button.PointToScreen(new System.Windows.Point(150, 25)),
                    linkText.PointToScreen(new System.Windows.Point(10, 8)),
                    linkedButton.PointToScreen(new System.Windows.Point(150, 25))));
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
            var link = await client.QueryAsync((int)ui.Link.X, (int)ui.Link.Y, focused: true).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(link.Ok, link.Error);
            Assert.Equal("Hyperlink", link.Snapshot?.ControlType);
            Assert.True(link.Snapshot?.IsFocusedLanding);
            Assert.Null(link.Snapshot?.Range);
            var linkedButton = await client.QueryAsync((int)ui.LinkedButton.X, (int)ui.LinkedButton.Y, focused: true)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(linkedButton.Ok, linkedButton.Error);
            Assert.NotNull(linkedButton.Snapshot);
            Assert.False(linkedButton.Snapshot.IsFocusedLanding);
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

    [Fact]
    public void TaskbarQueriesDoNotReintroduceClientRefreshTimers()
    {
        Assert.Null(typeof(UiaWorker).GetMethod("HostedControlAt", BindingFlags.Static | BindingFlags.NonPublic));
        Assert.Null(typeof(UiaWorker).GetField("HostCache", BindingFlags.Static | BindingFlags.NonPublic));
        Assert.NotNull(typeof(UiaWorker).GetMethod("ScanTaskbar", BindingFlags.Static | BindingFlags.NonPublic));
        Assert.Null(typeof(UiaClient).GetField("_taskbarSample", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Null(typeof(UiaClient).GetField("_refreshTimer", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    private static AutomationProperty[] Properties(CacheRequest cache) =>
        ((IEnumerable)typeof(CacheRequest).GetField("_properties", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cache)!).Cast<AutomationProperty>().ToArray();

    private static AutomationPattern[] Patterns(CacheRequest cache) =>
        ((IEnumerable)typeof(CacheRequest).GetField("_patterns", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cache)!).Cast<AutomationPattern>().ToArray();
}
