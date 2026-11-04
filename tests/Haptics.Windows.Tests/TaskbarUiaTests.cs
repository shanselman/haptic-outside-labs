using System.Runtime.InteropServices;

namespace Haptics.Windows.Tests;

[Collection(nameof(DesktopUiaCollection))]
public class TaskbarUiaTests
{
    [Fact]
    public async Task TaskbarLookupsReturnDisabledSnapshotsWithoutDiscovery()
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
        var budget = new UiaClientOptions().TimeoutMs;
        var y = (found.Top + found.Bottom) / 2;
        var slowest = 0.0;
        for (var i = 0; i < 19; i++)
        {
            var x = found.Left + (found.Right - found.Left) * (1 + i % 19) / 20;
            var response = await client.QueryAsync(x, y).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Null(response.ClientStatus);
            Assert.True(response.Ok, response.Error);
            Assert.Equal("hover-observation-disabled", response.Snapshot?.Source);
            Assert.False(response.Snapshot?.IsEnabled);
            Assert.True(response.Snapshot?.IsOffscreen);
            slowest = Math.Max(slowest, response.LookupMs);
            await Task.Delay(100);
        }

        Assert.True(slowest < budget / 2.0, $"A taskbar lookup took {slowest:0} ms, over half the {budget} ms hang timeout.");
        Assert.Equal(0, client.Stats.Timeouts);
        Assert.Equal(0, client.Stats.Restarts);
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
