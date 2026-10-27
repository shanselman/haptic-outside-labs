using Haptics.Lab;

namespace Haptics.Windows.Tests;

public class ShowcaseTests
{
    [Fact]
    public void SetupExplainsPrimingAndActivityDoesNotClaimPhysicalEvidence()
    {
        OnStaThread(() =>
        {
            using var form = new ShowcaseForm();
            var controls = Descendants(form).ToList();
            Assert.Contains(controls, c => c.Text == "Start here");
            Assert.Contains(controls, c => c.Text.StartsWith("1. Connect:", StringComparison.Ordinal));
            Assert.Contains(controls, c => c.Text.StartsWith("2. Feel:", StringComparison.Ordinal));
            Assert.Contains(controls, c => c.Text.StartsWith("3. Explore:", StringComparison.Ordinal));
            Assert.Contains(controls, c => c.Text == "Engine activity");
            Assert.Contains(controls, c => c.Text.Contains("not proof of a felt pulse", StringComparison.Ordinal));
            Assert.DoesNotContain(controls, c => c.Text == "What you just felt");

            var intensity = Assert.Single(controls.OfType<TrackBar>());
            Assert.Equal("Haptic intensity", intensity.AccessibleName);
            intensity.Value = 3;
            Assert.Contains(controls, c => c.Text == "30%");
        });
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
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
