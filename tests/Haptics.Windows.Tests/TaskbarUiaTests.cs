using System.Runtime.InteropServices;

namespace Haptics.Windows.Tests;

/// <summary>Runs alone: other tests' windows make explorer redraw taskbar buttons, which slows taskbar UIA.</summary>
[CollectionDefinition(nameof(TaskbarUiaTests), DisableParallelization = true)]
public sealed class TaskbarCollection;

[Collection(nameof(TaskbarUiaTests))]
public class TaskbarUiaTests
{
    [Fact]
    public async Task TaskbarLookupsStayWithinTheHangTimeoutAcrossHostRefreshes()
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

        // Before the fix every ~1.5 s host refresh blocked a lookup for ~300 ms, past the production 250 ms hang timeout,
        // which killed the worker and quarantined the foreground app. A generous client timeout keeps test load from
        // flaking; the worker-reported lookup time is what must stay under the production budget. The first scan can
        // take seconds while explorer still reflows buttons for windows other tests closed, so measure once it lands.
        using var client = new UiaClient(Path.Combine(AppContext.BaseDirectory, "haptics-lab.exe"), [UiaWorker.Arg],
            new UiaClientOptions { TimeoutMs = 3000 });
        var budget = new UiaClientOptions().TimeoutMs;
        var y = (found.Top + found.Bottom) / 2;
        long? firstButton = null;
        var slowest = 0.0;
        var started = Environment.TickCount64;
        for (var i = 0; Environment.TickCount64 - (firstButton ?? started) < (firstButton is null ? 30_000 : 4000); i++)
        {
            var x = found.Left + (found.Right - found.Left) * (1 + i % 19) / 20;
            var response = await client.QueryAsync(x, y).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Null(response.ClientStatus);
            Assert.True(response.Ok, response.Error);
            if (firstButton is not null)
            {
                slowest = Math.Max(slowest, response.LookupMs);
            }
            else if (response.Snapshot?.ControlType == "Button")
            {
                firstButton = Environment.TickCount64;
            }

            await Task.Delay(100);
        }

        Assert.True(firstButton is not null, "The taskbar drill-down never found a button.");
        // Half the hang timeout leaves the rest for IPC and scheduling. A scan on the request path costs 100-600 ms
        // depending on how busy explorer is, so this catches the old blocking refresh whenever explorer is under load.
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
