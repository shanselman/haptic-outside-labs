using System.Runtime.CompilerServices;
using Haptics.Core;
using Windows.Devices.Haptics;
using Windows.Foundation.Metadata;

namespace Haptics.Windows;

public sealed record ThreadManagerState(
    uint ThreadId,
    bool ApiPresent,
    bool ManagerOk,
    uint ManagerThreadId,
    bool ControllerPresent,
    string DeviceType,
    string? ControllerIdHash,
    string? Error);

/// <summary>A controller obtained through our own input thread or optional device discovery.</summary>
public sealed class CapturedController
{
    internal CapturedController(SimpleHapticsController controller, HapticDeviceType type, uint thread, double at)
    {
        Controller = controller;
        DeviceType = type;
        CapturedOnThread = thread;
        CapturedAtMs = at;
        IdHash = RawInputDevices.Hash(SafeId(controller));
        try
        {
            SupportedWaveforms = controller.SupportedFeedback.Select(f => f.Waveform).Distinct().ToArray();
            IsIntensitySupported = controller.IsIntensitySupported;
        }
        catch
        {
            SupportedWaveforms = [];
        }
    }

    public SimpleHapticsController Controller { get; }

    public HapticDeviceType DeviceType { get; }

    public uint CapturedOnThread { get; }

    public double CapturedAtMs { get; }

    public string IdHash { get; }

    public ushort[] SupportedWaveforms { get; }

    public bool IsIntensitySupported { get; }

    private static string SafeId(SimpleHapticsController c)
    {
        try
        {
            return c.Id;
        }
        catch
        {
            return "";
        }
    }
}

/// <summary>
/// Every call into Windows.Devices.Haptics goes through here, guarded by ApiInformation and try/catch,
/// and returns a <see cref="HapticAttempt"/> describing what the API said. Nothing here can claim a
/// physical result; that is the job of blinded trials.
/// </summary>
public static class HapticsApi
{
    public const string ManagerType = "Windows.Devices.Haptics.InputHapticsManager";
    public const string ControllerType = "Windows.Devices.Haptics.SimpleHapticsController";

    /// <summary>Discrete (non-continuous) waveforms the lab is willing to send.</summary>
    public static readonly IReadOnlyList<string> DiscreteWaveformNames =
        ["Click", "Hover", "Press", "Release", "Success", "Error", "Align", "Collide", "Step", "Grow"];

    private static readonly Lazy<bool> s_typePresent = new(() =>
    {
        try
        {
            return ApiInformation.IsTypePresent(ManagerType);
        }
        catch
        {
            return false;
        }
    });

    private static readonly Lazy<IReadOnlyDictionary<string, ushort>> s_waveforms = new(ResolveWaveforms);

    public static bool TypePresent => s_typePresent.Value;

    internal static bool ControllerTypePresent => Safe(() => ApiInformation.IsTypePresent(ControllerType));

    public static IReadOnlyDictionary<string, ushort> Waveforms => s_waveforms.Value;

    public static ushort? WaveformId(string name) => Waveforms.TryGetValue(name, out var id) ? id : null;

    public static string WaveformName(ushort id) => Waveforms.FirstOrDefault(kv => kv.Value == id).Key ?? $"0x{id:X4}";

    public static bool SafeIsSupported()
    {
        if (!TypePresent)
        {
            return false;
        }

        try
        {
            return CallIsSupported();
        }
        catch
        {
            return false;
        }
    }

    public static bool SafeIsHapticDevicePresent()
    {
        if (!TypePresent)
        {
            return false;
        }

        try
        {
            return CallIsDevicePresent();
        }
        catch
        {
            return false;
        }
    }

    public static Dictionary<string, object?> ProbeStatic()
    {
        var d = new Dictionary<string, object?>
        {
            ["typePresent.InputHapticsManager"] = TypePresent,
            ["typePresent.SimpleHapticsController"] = ControllerTypePresent,
            ["typePresent.KnownSimpleHapticsControllerWaveforms"] = Safe(() => ApiInformation.IsTypePresent("Windows.Devices.Haptics.KnownSimpleHapticsControllerWaveforms")),
            ["contract.UniversalApiContract.v19"] = Safe(() => ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 19)),
        };
        foreach (var (method, args) in new[]
                 {
                     ("IsSupported", 0), ("IsHapticDevicePresent", 0), ("GetForCurrentThread", 0), ("TryGetForThread", 1),
                     ("TrySendHapticWaveform", 2), ("TrySendHapticWaveform", 3), ("TrySendHapticWaveformForDuration", 4),
                     ("TrySendHapticWaveformForPlayCount", 5), ("TryStopFeedback", 0), ("SetOverrideHapticsController", 2),
                     ("ClearOverrideHapticsController", 1),
                 })
        {
            d[$"methodPresent.{method}/{args}"] = Safe(() => ApiInformation.IsMethodPresent(ManagerType, method, (uint)args));
        }

        d["isSupported"] = SafeIsSupported();
        d["isHapticDevicePresent"] = SafeIsHapticDevicePresent();
        d["waveforms"] = Waveforms.ToDictionary(kv => kv.Key, kv => (object?)$"0x{kv.Value:X4}");
        return d;
    }

    /// <summary>Inspect (never send) the calling thread's manager.</summary>
    public static ThreadManagerState InspectCurrentThread()
    {
        var tid = Native.GetCurrentThreadId();
        if (!TypePresent)
        {
            return new ThreadManagerState(tid, false, false, 0, false, "None", null, "api-absent");
        }

        try
        {
            return InspectCore(tid);
        }
        catch (Exception ex)
        {
            return new ThreadManagerState(tid, true, false, 0, false, "None", null, Describe(ex));
        }
    }

    /// <summary>Capture the controller of the device that most recently gave input to THIS thread, if any.</summary>
    public static CapturedController? TryCaptureCurrentController(HapticDeviceType? require = null)
    {
        if (!TypePresent)
        {
            return null;
        }

        try
        {
            return CaptureCore(require);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>R1/R2: GetForCurrentThread() on the calling thread, then TrySendHapticWaveform.</summary>
    public static HapticAttempt SendViaThreadManager(
        string route,
        string waveform,
        double intensity,
        double? inputTimestampMs = null,
        string fallback = "",
        CapturedController? expectedController = null)
    {
        var a = NewAttempt(route, waveform, intensity, fallback);
        if (!a.ApiPresent)
        {
            a.Error = "api-absent";
            return a;
        }

        try
        {
            SendManagerCore(a, inputTimestampMs, expectedController);
        }
        catch (Exception ex)
        {
            a.Error = Describe(ex);
            a.HResult = ex.HResult;
            a.CallEndMs = MonotonicClock.Instance.NowMs;
        }

        return a;
    }

    /// <summary>R3a: use a previously captured SimpleHapticsController directly from this thread.</summary>
    public static HapticAttempt SendViaController(string route, CapturedController? captured, string waveform, double intensity, double? inputTimestampMs = null)
    {
        var a = NewAttempt(route, waveform, intensity, "");
        if (captured is null)
        {
            a.Error = "no-captured-controller";
            return a;
        }

        try
        {
            SendControllerCore(a, captured, inputTimestampMs);
        }
        catch (Exception ex)
        {
            a.Error = Describe(ex);
            a.HResult = ex.HResult;
            a.CallEndMs = MonotonicClock.Instance.NowMs;
        }

        return a;
    }

    /// <summary>R3b: on this thread's manager, SetOverrideHapticsController(captured), send, then clear immediately.</summary>
    public static HapticAttempt SendViaOverride(string route, CapturedController? captured, string waveform, double intensity, double? inputTimestampMs = null)
    {
        var a = NewAttempt(route, waveform, intensity, "");
        if (captured is null)
        {
            a.Error = "no-captured-controller";
            return a;
        }

        if (!a.ApiPresent)
        {
            a.Error = "api-absent";
            return a;
        }

        try
        {
            SendOverrideCore(a, captured, inputTimestampMs);
        }
        catch (Exception ex)
        {
            a.Error = Describe(ex);
            a.HResult = ex.HResult;
            a.CallEndMs = MonotonicClock.Instance.NowMs;
        }

        return a;
    }

    public static bool TryStopOnCurrentThread()
    {
        if (!TypePresent)
        {
            return false;
        }

        try
        {
            return StopCore();
        }
        catch
        {
            return false;
        }
    }

    public static string Describe(Exception ex) => $"{ex.GetType().Name}: 0x{ex.HResult:X8} {ex.Message}".Trim();

    private static HapticAttempt NewAttempt(string route, string waveform, double intensity, string fallback) => new()
    {
        Route = route,
        ThreadId = Native.GetCurrentThreadId(),
        Waveform = waveform,
        WaveformId = WaveformId(waveform) ?? 0,
        FallbackId = string.IsNullOrEmpty(fallback) ? (ushort)0 : WaveformId(fallback) ?? 0,
        Intensity = intensity,
        ApiPresent = TypePresent,
    };

    // The *Core methods are separate and non-inlined so the WinRT types are only touched after guards pass.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool CallIsSupported() => InputHapticsManager.IsSupported();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool CallIsDevicePresent() => InputHapticsManager.IsHapticDevicePresent();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool StopCore() => InputHapticsManager.GetForCurrentThread()?.TryStopFeedback() ?? false;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ThreadManagerState InspectCore(uint tid)
    {
        var mgr = InputHapticsManager.GetForCurrentThread();
        if (mgr is null)
        {
            return new ThreadManagerState(tid, true, false, 0, false, "None", null, "manager-null");
        }

        var ctrl = mgr.CurrentHapticsController;
        return new ThreadManagerState(tid, true, true, mgr.ThreadId, ctrl is not null, mgr.CurrentHapticsControllerDeviceType.ToString(),
            ctrl is null ? null : RawInputDevices.Hash(SafeControllerId(ctrl)), null);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CapturedController? CaptureCore(HapticDeviceType? require)
    {
        var mgr = InputHapticsManager.GetForCurrentThread();
        var ctrl = mgr?.CurrentHapticsController;
        if (mgr is null || ctrl is null)
        {
            return null;
        }

        var type = mgr.CurrentHapticsControllerDeviceType;
        if (require is { } r && type != r)
        {
            return null;
        }

        return new CapturedController(ctrl, type, Native.GetCurrentThreadId(), MonotonicClock.Instance.NowMs);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SendManagerCore(HapticAttempt a, double? inputTs, CapturedController? expectedController)
    {
        var mgr = InputHapticsManager.GetForCurrentThread();
        a.ManagerOk = mgr is not null;
        if (mgr is null)
        {
            a.Error = "manager-null";
            return;
        }

        var ctrl = mgr.CurrentHapticsController;
        a.ControllerPresent = ctrl is not null;
        a.DeviceType = mgr.CurrentHapticsControllerDeviceType.ToString();
        a.ControllerIdHash = ctrl is null ? null : RawInputDevices.Hash(SafeControllerId(ctrl));
        if (expectedController is not null &&
            (ctrl is null || !MatchesExpectedController(a.ControllerIdHash, expectedController)))
        {
            a.Error = "controller-mismatch";
            a.CallEndMs = MonotonicClock.Instance.NowMs;
            return;
        }

        a.Called = true;
        a.CallStartMs = MonotonicClock.Instance.NowMs;
        a.InputToCallMs = inputTs is { } t ? a.CallStartMs - t : null;
        a.Accepted = mgr.TrySendHapticWaveform(a.WaveformId, a.FallbackId, a.Intensity);
        a.CallEndMs = MonotonicClock.Instance.NowMs;
    }

    internal static bool MatchesExpectedController(string? currentIdHash, CapturedController expected) =>
        expected.IdHash != "none" && currentIdHash == expected.IdHash;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SendControllerCore(HapticAttempt a, CapturedController captured, double? inputTs)
    {
        var ctrl = captured.Controller;
        a.ControllerPresent = true;
        a.DeviceType = captured.DeviceType.ToString();
        a.ControllerIdHash = captured.IdHash;
        var feedback = ctrl.SupportedFeedback.FirstOrDefault(f => f.Waveform == a.WaveformId);
        if (feedback is null)
        {
            a.Error = "waveform-not-supported-by-controller";
            return;
        }

        a.Called = true;
        a.CallStartMs = MonotonicClock.Instance.NowMs;
        a.InputToCallMs = inputTs is { } t ? a.CallStartMs - t : null;
        if (ctrl.IsIntensitySupported)
        {
            ctrl.SendHapticFeedback(feedback, a.Intensity);
        }
        else
        {
            ctrl.SendHapticFeedback(feedback);
        }

        a.Accepted = true; // void API: "did not throw" is the only acceptance signal available.
        a.CallEndMs = MonotonicClock.Instance.NowMs;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SendOverrideCore(HapticAttempt a, CapturedController captured, double? inputTs)
    {
        var mgr = InputHapticsManager.GetForCurrentThread();
        a.ManagerOk = mgr is not null;
        if (mgr is null)
        {
            a.Error = "manager-null";
            return;
        }

        var token = mgr.SetOverrideHapticsController(captured.DeviceType, captured.Controller);
        try
        {
            var ctrl = mgr.CurrentHapticsController;
            a.ControllerPresent = ctrl is not null;
            a.DeviceType = mgr.CurrentHapticsControllerDeviceType.ToString();
            a.ControllerIdHash = captured.IdHash;
            a.Called = true;
            a.CallStartMs = MonotonicClock.Instance.NowMs;
            a.InputToCallMs = inputTs is { } t ? a.CallStartMs - t : null;
            a.Accepted = mgr.TrySendHapticWaveform(a.WaveformId, a.FallbackId, a.Intensity);
            a.CallEndMs = MonotonicClock.Instance.NowMs;
        }
        finally
        {
            mgr.ClearOverrideHapticsController(token);
        }
    }

    private static IReadOnlyDictionary<string, ushort> ResolveWaveforms()
    {
        var map = new Dictionary<string, ushort>();
        if (!Safe(() => ApiInformation.IsTypePresent("Windows.Devices.Haptics.KnownSimpleHapticsControllerWaveforms")))
        {
            return map;
        }

        foreach (var name in DiscreteWaveformNames)
        {
            try
            {
                var id = ReadWaveform(name);
                if (id is not null)
                {
                    map[name] = id.Value;
                }
            }
            catch
            {
                // Property missing on this OS build: leave it out.
            }
        }

        return map;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ushort? ReadWaveform(string name) => name switch
    {
        "Click" => KnownSimpleHapticsControllerWaveforms.Click,
        "Hover" => KnownSimpleHapticsControllerWaveforms.Hover,
        "Press" => KnownSimpleHapticsControllerWaveforms.Press,
        "Release" => KnownSimpleHapticsControllerWaveforms.Release,
        "Success" => KnownSimpleHapticsControllerWaveforms.Success,
        "Error" => KnownSimpleHapticsControllerWaveforms.Error,
        "Align" => KnownSimpleHapticsControllerWaveforms.Align,
        "Collide" => KnownSimpleHapticsControllerWaveforms.Collide,
        "Step" => KnownSimpleHapticsControllerWaveforms.Step,
        "Grow" => KnownSimpleHapticsControllerWaveforms.Grow,
        _ => null,
    };

    private static string SafeControllerId(SimpleHapticsController c)
    {
        try
        {
            return c.Id;
        }
        catch
        {
            return "";
        }
    }

    private static bool Safe(Func<bool> f)
    {
        try
        {
            return f();
        }
        catch
        {
            return false;
        }
    }
}
