using Haptics.Windows;

namespace Haptics.Tray;

/// <summary>Coalesces device changes into one lookup at a time and rejects results from an older device set.</summary>
internal sealed class ControllerRefresh(Func<Task<ControllerDiscoveryResult>> discover)
{
    private StartupPriming? _current;
    private long _revision;
    private long _lookupRevision;
    private bool _requested;

    internal void Request()
    {
        _revision++;
        _requested = true;
    }

    internal void DeviceChanged(bool arrived)
    {
        _revision++;
        _requested |= arrived;
    }

    internal (ControllerDiscoveryResult? Result, bool ShowFallback, bool Started) Poll(long now, bool enabled, bool alreadyPrimed)
    {
        ControllerDiscoveryResult? result = null;
        var showFallback = false;
        var started = false;
        if (_current is { } current)
        {
            var update = current.Poll(now, enabled, alreadyPrimed);
            var fresh = _revision == _lookupRevision;
            result = fresh ? update.Result : null;
            showFallback = update.ShowFallback && (fresh || update.Result is null);
            if (current.Completed)
            {
                _current = null;
            }
        }

        if (_current is null && _requested && enabled)
        {
            _requested = false;
            _lookupRevision = _revision;
            _current = new StartupPriming(discover(), now);
            started = true;
        }

        return (result, showFallback, started);
    }
}
