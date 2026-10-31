using Haptics.Core;
using Haptics.Windows;
using Xunit.Abstractions;

namespace Haptics.Windows.Tests;

/// <summary>
/// Measures the real caption geometry the worker reports for inert, never-activated WinForms windows, so the Close
/// recognizer's tolerance comes from a fixture rather than a guess. Only the worker reads UIA, as in production.
/// </summary>
public class CaptionCloseUiaTests(ITestOutputHelper output)
{
    [Fact]
    public async Task WinFormsCaptionGeometryRecognizesOnlyLeftToRightClose()
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
                var ltr = new InertForm(false, 120);
                var rtl = new InertForm(true, 360);
                ltr.Shown += (_, _) =>
                {
                    rtl.Shown += (_, _) => ready.TrySetResult((ltr, rtl));
                    rtl.Show();
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
            using var client = new UiaClient(Path.Combine(AppContext.BaseDirectory, "haptics-lab.exe"),
                [UiaWorker.Arg], new UiaClientOptions { TimeoutMs = 2000 });

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
        while ((await Query(client, window.X + (window.Width / 2), window.Y + (window.Height / 2))).ProcessId != Environment.ProcessId)
        {
            Assert.True(DateTime.UtcNow < deadline, $"{label} fixture window never became the UIA hit target");
            await Task.Delay(100);
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
    }
}
