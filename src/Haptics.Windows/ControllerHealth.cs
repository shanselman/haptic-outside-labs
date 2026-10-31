using Haptics.Core;

namespace Haptics.Windows;

internal enum ControllerFailure { None, UnsupportedWaveform, Transient, Lost }

internal static class ControllerHealth
{
    internal static ControllerFailure Classify(HapticAttempt attempt)
    {
        if (attempt.Accepted)
        {
            return ControllerFailure.None;
        }

        if (attempt.Error == "waveform-not-supported-by-controller")
        {
            return ControllerFailure.UnsupportedWaveform;
        }

        // Only explicit stale/closed/disconnected signals revoke a mapping; a generic failure is not unplug evidence.
        return attempt.HResult is unchecked((int)0x80010108) or // RPC_E_DISCONNECTED
                unchecked((int)0x80000013) or // RO_E_CLOSED
                unchecked((int)0x8007048F) or // ERROR_DEVICE_NOT_CONNECTED
                unchecked((int)0x80070006) // E_HANDLE
            ? ControllerFailure.Lost
            : ControllerFailure.Transient;
    }
}
