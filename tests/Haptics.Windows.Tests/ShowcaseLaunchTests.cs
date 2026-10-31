using System.ComponentModel;
using Haptics.Tray;

namespace Haptics.Windows.Tests;

public class ShowcaseLaunchTests
{
    [Theory]
    [InlineData(1223, false)]
    [InlineData(2, true)]
    [InlineData(5, true)]
    public void OnlyUserCancellationIsSilentAndLaterLaunchStillWorks(int nativeError, bool reported)
    {
        var errors = new List<string>();
        TrayContext.TryLaunchShowcase(() => throw new Win32Exception(nativeError, "fixture launch error"), errors.Add);
        Assert.Equal(reported ? 1 : 0, errors.Count);
        if (reported)
        {
            Assert.Contains("fixture launch error", errors[0]);
        }

        var launched = false;
        TrayContext.TryLaunchShowcase(() => launched = true, errors.Add);
        Assert.True(launched);
        Assert.Equal(reported ? 1 : 0, errors.Count);
    }

    [Fact]
    public void UnexpectedLaunchBugIsNotSwallowed()
    {
        var bug = new InvalidOperationException("fixture bug");
        Assert.Same(bug, Assert.Throws<InvalidOperationException>(() =>
            TrayContext.TryLaunchShowcase(() => throw bug, _ => Assert.Fail("Unexpected UI callback"))));
    }
}
