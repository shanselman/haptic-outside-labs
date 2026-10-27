using Haptics.Windows;

namespace Haptics.Tray;

/// <summary>Wait briefly for optional discovery without blocking the UI or losing manual priming.</summary>
internal sealed class StartupPriming(Task<ControllerDiscoveryResult> discovery, long startedAt)
{
    internal const long FallbackAfterMs = 2000;
    private bool _resultTaken;
    private bool _fallbackShown;

    internal bool Completed => _resultTaken;

    internal (ControllerDiscoveryResult? Result, bool ShowFallback) Poll(long now, bool enabled, bool alreadyPrimed)
    {
        ControllerDiscoveryResult? result = null;
        if (!_resultTaken && discovery.IsCompleted)
        {
            _resultTaken = true;
            result = discovery.GetAwaiter().GetResult();
        }

        var ready = alreadyPrimed || result?.Controllers.Count > 0;
        var show = enabled && !ready && !_fallbackShown && (_resultTaken || now - startedAt >= FallbackAfterMs);
        _fallbackShown |= show;
        return (result, show);
    }
}
