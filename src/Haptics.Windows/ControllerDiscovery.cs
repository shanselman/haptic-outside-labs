using System.ComponentModel;
using System.Runtime.InteropServices;
using Haptics.Core;
using Windows.Devices.Haptics;

namespace Haptics.Windows;

public sealed record ControllerDiscoveryResult(
    IReadOnlyDictionary<long, CapturedController> Controllers,
    string Diagnostic);

/// <summary>
/// Optional, undocumented device lookup. Failure leaves foreground-input priming available.
/// Acquisition never sends feedback and does not establish physical evidence.
/// </summary>
public static class ControllerDiscovery
{
    public static ControllerDiscoveryResult Discover()
    {
        if (!HapticsApi.ControllerTypePresent)
        {
            return new(new Dictionary<long, CapturedController>(), "Automatic lookup unavailable on this Windows build; move over Settings to prime.");
        }

        try
        {
            using var factory = new DeviceFactory();
            return Discover(RawInputDevices.EnumerateMouseHandles, factory.Acquire);
        }
        catch (Exception ex) when (IsPlatformFailure(ex))
        {
            return new(new Dictionary<long, CapturedController>(), $"Automatic lookup unavailable ({HapticsApi.Describe(ex)}); move over Settings to prime.");
        }
    }

    internal static ControllerDiscoveryResult Discover(
        Func<IReadOnlyList<long>> enumerate, Func<long, CapturedController?> acquire)
    {
        var controllers = new Dictionary<long, CapturedController>();
        var failures = new List<string>();
        var attempted = 0;
        foreach (var handle in enumerate().Where(h => h != 0).Distinct())
        {
            attempted++;
            try
            {
                if (acquire(handle) is { } controller)
                {
                    controllers.Add(handle, controller);
                }
            }
            catch (Exception ex) when (IsPlatformFailure(ex))
            {
                failures.Add(HapticsApi.Describe(ex));
            }
        }

        var diagnostic = $"Experimental device lookup: {controllers.Count} controller(s) from {attempted} mouse device(s).";
        if (failures.Count > 0)
        {
            diagnostic += $" {failures.Count} lookup(s) failed: {string.Join("; ", failures.Distinct())}";
        }

        if (controllers.Count == 0)
        {
            diagnostic += " Move over Settings to prime.";
        }

        return new(controllers, diagnostic);
    }

    private static bool IsPlatformFailure(Exception ex) =>
        ex is COMException or InvalidCastException or TypeLoadException or DllNotFoundException
            or EntryPointNotFoundException or UnauthorizedAccessException or Win32Exception;

    private sealed class DeviceFactory : IDisposable
    {
        // Factory IID and its sole additional method verified against InputHost.dll 10.0.28000.3231's
        // matching Microsoft public symbols and IInspectable.GetIids. See docs/RESEARCH.md.
        private static readonly Guid StaticsId = new("0e929425-9118-5f26-a73d-73b879f9de3d");
        private bool _initialized;
        private readonly AcquireController _acquire;
        private IntPtr _factory;

        public DeviceFactory()
        {
            var hr = RoInitialize(1);
            if (hr != unchecked((int)0x80010106)) // An existing STA is also usable; don't uninitialize it.
            {
                Marshal.ThrowExceptionForHR(hr);
                _initialized = true;
            }

            IntPtr name = IntPtr.Zero;
            try
            {
                const string className = "Windows.Internal.Devices.Input.HapticDevice";
                Marshal.ThrowExceptionForHR(WindowsCreateString(className, (uint)className.Length, out name));
                var iid = StaticsId;
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, in iid, out _factory));
                _acquire = Marshal.GetDelegateForFunctionPointer<AcquireController>(
                    Marshal.ReadIntPtr(Marshal.ReadIntPtr(_factory), 6 * IntPtr.Size));
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                if (name != IntPtr.Zero)
                {
                    WindowsDeleteString(name);
                }
            }
        }

        public CapturedController? Acquire(long handle)
        {
            IntPtr abi = IntPtr.Zero;
            try
            {
                Marshal.ThrowExceptionForHR(_acquire(_factory, unchecked((ulong)handle), out abi));
                if (abi == IntPtr.Zero)
                {
                    return null; // S_OK + null is the API's no-controller result, including ordinary mice.
                }

                var controller = SimpleHapticsController.FromAbi(abi);
                if (!controller.SupportedFeedback.Any(f => HapticsApi.Waveforms.Values.Contains(f.Waveform)))
                {
                    return null;
                }

                // The private API does not return a device type. Don't guess Mouse vs. Touchpad from a
                // mouse-compatible raw collection; normal priming can later establish the specific type.
                return new CapturedController(controller, HapticDeviceType.Generic, Native.GetCurrentThreadId(),
                    MonotonicClock.Instance.NowMs);
            }
            finally
            {
                if (abi != IntPtr.Zero)
                {
                    Marshal.Release(abi);
                }
            }
        }

        public void Dispose()
        {
            if (_factory != IntPtr.Zero)
            {
                Marshal.Release(_factory);
                _factory = IntPtr.Zero;
            }

            if (_initialized)
            {
                _initialized = false;
                RoUninitialize();
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int AcquireController(IntPtr factory, ulong device, out IntPtr controller);

        [DllImport("combase.dll")]
        private static extern int RoInitialize(uint type);

        [DllImport("combase.dll")]
        private static extern void RoUninitialize();

        [DllImport("combase.dll", CharSet = CharSet.Unicode)]
        private static extern int WindowsCreateString(string source, uint length, out IntPtr value);

        [DllImport("combase.dll")]
        private static extern int WindowsDeleteString(IntPtr value);

        [DllImport("combase.dll")]
        private static extern int RoGetActivationFactory(IntPtr name, in Guid iid, out IntPtr factory);
    }
}
