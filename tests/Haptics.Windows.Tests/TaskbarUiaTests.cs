using System.Runtime.InteropServices;
using Haptics.Core;

namespace Haptics.Windows.Tests;

[Collection(nameof(DesktopUiaCollection))]
public class TaskbarUiaTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1499, true)]
    [InlineData(1500, false)]
    [InlineData(-1, false)]
    public void CachedTaskbarButtonsExpire(double ageMs, bool found)
    {
        var button = new ElementSnapshot
        {
            ControlType = "Button", ProcessId = 100, Bounds = new RectD(20, 0, 40, 40),
            IsEnabled = true, IsControlElement = true,
        };
        var host = new RectD(0, 0, 200, 40);
        Assert.Equal(found, UiaWorker.SelectTaskbarItem([button], host, 100, 30, 20, ageMs) is not null);
        Assert.Null(UiaWorker.SelectTaskbarItem([button with { IsPassword = true }], host, 100, 30, 20, 0));
        Assert.Null(UiaWorker.SelectTaskbarItem([button with { IsEnabled = false }], host, 100, 30, 20, 0));
        Assert.Null(UiaWorker.SelectTaskbarItem([button with { IsOffscreen = true }], host, 100, 30, 20, 0));
        Assert.Null(UiaWorker.SelectTaskbarItem([button], host, 200, 30, 20, 0));
        Assert.Null(UiaWorker.SelectTaskbarItem([button], new RectD(0, 0, 25, 40), 100, 30, 20, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskbarLookupsStayResponsive(bool observeTaskbar)
    {
        if (!Environment.UserInteractive || Environment.GetEnvironmentVariable("CI") is not null)
        {
            return; // Needs a real Windows taskbar on an interactive desktop.
        }

        var previous = SetThreadDpiAwarenessContext(-4); // Per-monitor v2: physical pixels, like the worker.
        var tray = FindWindow("Shell_TrayWnd", null);
        var found = tray != IntPtr.Zero && GetWindowRect(tray, out var rect) ? rect : default;
        SetThreadDpiAwarenessContext(previous);
        if (found.Right - found.Left < 200 || found.Bottom - found.Top < 10)
        {
            return;
        }

        using var client = new UiaClient(Path.Combine(AppContext.BaseDirectory, "haptics-lab.exe"), [UiaWorker.Arg],
            new UiaClientOptions { TimeoutMs = 3000 });
        client.ObserveTaskbar = () => observeTaskbar;
        var budget = new UiaClientOptions().TimeoutMs;
        var y = (found.Top + found.Bottom) / 2;
        var slowest = 0.0;
        var cachedButtons = 0;
        for (var i = 0; i < 19; i++)
        {
            var x = found.Left + (found.Right - found.Left) * (1 + i % 19) / 20;
            var response = await client.QueryAsync(x, y).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Null(response.ClientStatus);
            if (!observeTaskbar)
            {
                Assert.True(response.Ok, response.Error);
                Assert.Equal("hover-observation-disabled", response.Snapshot?.Source);
                Assert.False(response.Snapshot?.IsEnabled);
                Assert.True(response.Snapshot?.IsOffscreen);
            }
            else if (response.Ok)
            {
                Assert.Contains(response.Snapshot?.Source, new[] { "taskbar-cache", "taskbar-cache-pending" });
                if (response.Snapshot?.Source == "taskbar-cache")
                {
                    cachedButtons++;
                }
            }
            else
            {
                Assert.StartsWith("taskbar-scan-", response.Error);
            }
            slowest = Math.Max(slowest, response.LookupMs);
            await Task.Delay(100);
        }

        Assert.True(slowest < budget / 2.0, $"A taskbar lookup took {slowest:0} ms, over half the {budget} ms hang timeout.");
        Assert.Equal(0, client.Stats.Timeouts);
        Assert.Equal(0, client.Stats.Restarts);
        if (observeTaskbar)
        {
            Assert.True(cachedButtons > 0, "Everything did not discover any point-containing taskbar buttons.");
        }
    }

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
