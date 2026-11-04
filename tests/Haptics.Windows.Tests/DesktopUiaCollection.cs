namespace Haptics.Windows.Tests;

// FromPoint fixtures share the desktop, including windows created by other test collections.
[CollectionDefinition(nameof(DesktopUiaCollection), DisableParallelization = true)]
public sealed class DesktopUiaCollection;
