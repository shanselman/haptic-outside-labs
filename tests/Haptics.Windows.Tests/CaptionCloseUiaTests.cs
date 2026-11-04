using Haptics.Core;
using Haptics.Windows;
using Xunit.Abstractions;

namespace Haptics.Windows.Tests;

/// <summary>
/// Measures the real caption geometry the worker reports for inert, never-activated WinForms windows, so the Close
/// recognizer's tolerance comes from a fixture rather than a guess. Only the worker reads UIA, as in production.
/// </summary>
[Collection(nameof(DesktopUiaCollection))]
public class CaptionCloseUiaTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptionsUseNativeFocusedIdentityAndLegacyGeometry(bool focused)
    {
        if (!Environment.UserInteractive || Environment.GetEnvironmentVariable("CI") is not null)
        {
            return; // Real UIA provider coverage needs an interactive desktop, like EXP-040.
        }

        var ready = new TaskCompletionSource<(Form Ltr, Form Rtl)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                SetThreadDpiAwarenessContext(PerMonitorAwareV2);
                using var ltr = new InertForm(false, 120);
                using var rtl = new InertForm(true, 360);
                ltr.Shown += (_, _) =>
                {
                    rtl.Show();
                    ltr.BeginInvoke(() =>
                    {
                        // The first Show can still change Z-order after Shown returns.
                        ltr.ShowForObservation();
                        rtl.ShowForObservation();
                        ready.TrySetResult((ltr, rtl));
                    });
                };
                Application.Run(ltr);
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
            foreach (var form in new[] { ui.Ltr, ui.Rtl })
            {
                output.WriteLine(form.Invoke(() =>
                {
                    Assert.True(Native.GetWindowRect(form.Handle, out var bounds));
                    var hit = Native.WindowFromPoint(new Native.POINT
                    {
                        X = (bounds.Left + bounds.Right) / 2, Y = (bounds.Top + bounds.Bottom) / 2,
                    });
                    Native.GetWindowThreadProcessId(hit, out var pid);
                    return $"Fixture visible={Native.IsWindowVisible(form.Handle)}, bounds={bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}, native center PID={pid}";
                }));
            }
            using var client = new UiaClient(Path.Combine(AppContext.BaseDirectory, "haptics-lab.exe"),
                [UiaWorker.Arg], new UiaClientOptions { TimeoutMs = 2000 });

            if (focused)
            {
                foreach (var form in new[] { ui.Ltr, ui.Rtl })
                {
                    var info = form.Invoke(() =>
                    {
                        var reported = new Native.TITLEBARINFOEX
                        {
                            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.TITLEBARINFOEX>(),
                            State = new uint[6], Bounds = new Native.RECT[6],
                        };
                        Assert.NotEqual(IntPtr.Zero, Native.ReadTitleBarInfo(form.Handle, Native.WM_GETTITLEBARINFOEX,
                            UIntPtr.Zero, ref reported, 2, 100, out _));
                        return reported;
                    });
                    foreach (var (slot, kind) in new[] { (5, Haptics.Core.CaptionButton.Close), (3, Haptics.Core.CaptionButton.Maximize), (2, Haptics.Core.CaptionButton.Minimize) })
                    {
                        var bounds = info.Bounds[slot];
                        Assert.True(bounds.Right > bounds.Left && bounds.Bottom > bounds.Top);
                        var response = await client.QueryAsync((bounds.Left + bounds.Right) / 2,
                            (bounds.Top + bounds.Bottom) / 2, focused: true).WaitAsync(TimeSpan.FromSeconds(5));
                        Assert.True(response.Ok, response.Error);
                        output.WriteLine($"Native caption {kind}: {response.Snapshot}");
                        Assert.Equal(Environment.ProcessId, response.Snapshot?.ProcessId);
                        Assert.Equal(kind, response.Snapshot?.CaptionButton);
                        Assert.False(response.Snapshot?.IsFocusedLanding);
                    }
                }
                return;
            }

            var ltr = await CaptionButtons(client, ui.Ltr.Bounds, "ltr");
            Assert.Equal(3, ltr.Length);
            Assert.True(SemanticVocabulary.IsCaptionClose(ltr[0]), "rightmost left-to-right caption button is Close");
            Assert.All(ltr.Skip(1), s => Assert.False(SemanticVocabulary.IsCaptionClose(s)));

            // Mirrored caption: Close is leftmost. With no trusted direction field it stays generic, and the mirrored
            // Minimize/Maximize must not be mistaken for Close.
            var rtl = await CaptionButtons(client, ui.Rtl.Bounds, "rtl");
            Assert.Equal(3, rtl.Length);
            Assert.All(rtl, s => Assert.False(SemanticVocabulary.IsCaptionClose(s)));
        }
        finally
        {
            ui.Ltr.BeginInvoke(ui.Ltr.Close);
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "UIA fixture thread did not exit.");
        }
    }

    /// <summary>Caption buttons the worker reports along the window's caption row, rightmost first.</summary>
    private async Task<ElementSnapshot[]> CaptionButtons(UiaClient client, System.Drawing.Rectangle window, string label)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var hit = await Query(client, window.X + (window.Width / 2), window.Y + (window.Height / 2));
        output.WriteLine($"{label} fixture {window}, expected PID {Environment.ProcessId}, initial hit {hit.ProcessId}/{hit.ControlType}/{hit.ClassName} at {hit.Bounds}");
        while (hit.ProcessId != Environment.ProcessId)
        {
            Assert.True(DateTime.UtcNow < deadline, $"{label} fixture window never became the UIA hit target");
            await Task.Delay(100);
            hit = await Query(client, window.X + (window.Width / 2), window.Y + (window.Height / 2));
        }

        var buttons = new Dictionary<string, ElementSnapshot>();
        for (var x = window.X + 2; x < window.Right - 1; x += 4)
        {
            var s = await Query(client, x, window.Y + 20);
            if (s is { ControlType: "Button", ParentControlType: "TitleBar", Bounds: { } b, ParentBounds: { } p } &&
                s.ProcessId == Environment.ProcessId && buttons.TryAdd(b.ToString(), s))
            {
                output.WriteLine($"{label} button {b} parent {p} scale {s.Scale} rightGap {p.X + p.Width - (b.X + b.Width)} leftGap {b.X - p.X}");
            }
        }

        return buttons.Values.OrderByDescending(s => s.Bounds!.X).ToArray();
    }

    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    // Physical coordinates for the fixture windows, matching the per-monitor-aware worker.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    private static async Task<ElementSnapshot> Query(UiaClient client, int x, int y)
    {
        var r = await client.QueryAsync(x, y).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(r.Ok, r.Error);
        return r.Snapshot!;
    }

    private sealed class InertForm : Form
    {
        public InertForm(bool rtl, int top)
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = new System.Drawing.Rectangle(120, top, 420, 200);
            TopMost = true;
            if (rtl)
            {
                RightToLeft = RightToLeft.Yes;
                RightToLeftLayout = true;
            }
        }

        protected override bool ShowWithoutActivation => true;

        public void ShowForObservation()
        {
            if (!SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040))
            {
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    }
}
