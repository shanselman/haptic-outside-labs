using Haptics.Lab;
using WinForms = System.Windows.Forms;

namespace Haptics.Windows.Tests;

// Issue #37: fonts follow display scaling, so the rating and answer panels must scale their layout with them.
public class TrialPanelLayoutTests
{
    [Fact]
    public void RatingPanelTextFitsAtTheCurrentDpi() => ShowcaseTests.OnStaThread(() =>
    {
        using var panel = new RatingPanel("Block 1 of 3: how did the haptics feel?", (_, _) => { });
        AssertTextFits(panel);
    });

    [Fact]
    public void AnswerPanelTextFitsAtTheCurrentDpi() => ShowcaseTests.OnStaThread(() =>
    {
        using var panel = new AnswerPanel(_ => { }, () => { });
        panel.SetMessage("Trial 12/24: keep the OTHER app in front and keep moving the target device over it. Wait for NOW…");
        AssertTextFits(panel);
    });

    private static void AssertTextFits(WinForms.Form panel)
    {
        // Tests run at 96 DPI, so also require DPI scaling: fitting here then implies fitting at 150% and 200%.
        Assert.Equal(WinForms.AutoScaleMode.Dpi, panel.AutoScaleMode);
        panel.PerformLayout();
        Assert.True(WinForms.Screen.PrimaryScreen!.WorkingArea.Contains(panel.Bounds), $"Panel {panel.Bounds} is off screen.");
        foreach (var control in Descendants(panel))
        {
            Assert.True(control.Parent!.ClientRectangle.Contains(control.Bounds),
                $"'{control.Text}' at {control.Bounds} is clipped by its parent {control.Parent.ClientRectangle}.");
            if (control is WinForms.Label { Text.Length: > 0 } label)
            {
                var needed = label.GetPreferredSize(new System.Drawing.Size(label.Width, 0));
                Assert.True(needed.Width <= label.Width && needed.Height <= label.Height,
                    $"'{label.Text}' needs {needed} but has {label.Size}.");
            }
        }
    }

    private static IEnumerable<WinForms.Control> Descendants(WinForms.Control root)
    {
        foreach (WinForms.Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
