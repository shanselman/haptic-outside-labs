using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Haptics.Core;
using Windows.Devices.Haptics;

namespace Haptics.Windows;

/// <summary>
/// Haptic controllers matched to raw input devices. Manual priming requires input to our own UI.
/// Automatically discovered controllers are matched strictly; they never become the fallback for another device.
/// </summary>
public sealed class ControllerBank
{
    internal const double ManualCaptureFreshnessMs = 1000;

    private readonly Func<CapturedController?> _captureController;
    private IReadOnlyDictionary<long, CapturedController> _map = new Dictionary<long, CapturedController>();
    private long _latestHandle;

    public ControllerBank() : this(() => HapticsApi.TryCaptureCurrentController()) { }

    internal ControllerBank(Func<CapturedController?> captureController) => _captureController = captureController;

    /// <summary>Most recent manual capture for display and explicitly single-controller experiments; not a routing fallback.</summary>
    public CapturedController? Latest { get; private set; }

    public IReadOnlyDictionary<long, CapturedController> Map => _map;

    public bool Any => Latest is not null || _map.Count > 0;

    public void AddDiscovered(ControllerDiscoveryResult discovery)
    {
        var map = new Dictionary<long, CapturedController>(_map);
        foreach (var (handle, controller) in discovery.Controllers)
        {
            map.TryAdd(handle, controller); // Don't overwrite a controller already primed by the user.
        }

        _map = map;
    }

    public bool RemoveDevice(long handle, CapturedController? expected = null)
    {
        if (!_map.TryGetValue(handle, out var removed) ||
            expected is not null && !ReferenceEquals(expected, removed))
        {
            return false;
        }

        var map = new Dictionary<long, CapturedController>(_map);
        foreach (var (mappedHandle, controller) in map.ToArray())
        {
            if (mappedHandle == handle || ControllerRouting.IsHandlelessAlias(mappedHandle, controller, removed))
            {
                map.Remove(mappedHandle);
            }
        }

        _map = map;
        if (_latestHandle == handle ||
            Latest is { } latest && ControllerRouting.IsHandlelessAlias(_latestHandle, latest, removed))
        {
            Latest = null;
            _latestHandle = 0;
        }

        return true;
    }

    public bool RemoveLostController(long handle, CapturedController expected)
    {
        var current = ControllerRouting.Resolve(_map, handle,
            handle == 0 ? PointerInputClass.Handleless : PointerInputClass.Handle).Controller;
        if (!ReferenceEquals(current, expected))
        {
            return false;
        }

        foreach (var mappedHandle in _map.Where(pair =>
                     ControllerRouting.AffectedByLoss(handle, pair.Key, pair.Value, expected))
                     .Select(pair => pair.Key).ToArray())
        {
            RemoveDevice(mappedHandle);
        }

        return true;
    }

    /// <summary>Capture the device that just moved over our UI. Returns true if something new was added.</summary>
    public bool TryCapture(long deviceHandle, PointerInputClass inputClass, double inputTimestampMs, double nowMs)
    {
        if (!ControllerRouting.IsManualCaptureValid(
                deviceHandle, inputClass, inputTimestampMs, nowMs, ManualCaptureFreshnessMs))
        {
            return false;
        }

        var c = _captureController();
        if (c is null || !ControllerRouting.MatchesInputClass(inputClass, c.DeviceType))
        {
            return false;
        }

        var sameController = _map.Where(pair => pair.Key != deviceHandle && ControllerRouting.SameController(pair.Value, c)).ToArray();
        var classifiesDiscoveredTouchpad = inputClass == PointerInputClass.Handleless &&
                                           c.DeviceType == HapticDeviceType.Touchpad &&
                                           sameController.All(pair => pair.Value.DeviceType == HapticDeviceType.Generic);
        if (sameController.Length > 0 && !classifiesDiscoveredTouchpad)
        {
            return false;
        }

        return CaptureAndMap(deviceHandle, c);
    }

    private bool CaptureAndMap(long deviceHandle, CapturedController? c)
    {
        if (c is null)
        {
            return false;
        }

        var latestChanged = Latest?.IdHash != c.IdHash || Latest?.DeviceType != c.DeviceType;
        Latest = c;
        _latestHandle = deviceHandle;
        if (_map.TryGetValue(deviceHandle, out var known) &&
            (ReferenceEquals(known, c) ||
             known.DeviceType == c.DeviceType && known.IdHash == c.IdHash &&
             known.Controller is { } knownController && ReferenceEquals(knownController, c.Controller)))
        {
            return latestChanged;
        }

        _map = new Dictionary<long, CapturedController>(_map) { [deviceHandle] = c };
        return true;
    }

    public string Describe() =>
        !Any ? "nothing yet" : string.Join(" + ", _map.Values.Concat(Latest is { } latest ? [latest] : [])
            .Select(c => c.DeviceType == global::Windows.Devices.Haptics.HapticDeviceType.Generic ? "Haptic device" : c.DeviceType.ToString()).Distinct());

    public void ApplyTo(ObservationPipeline? pipeline)
    {
        if (pipeline is not null)
        {
            pipeline.Controller = null;
            pipeline.Controllers = _map;
        }
    }
}

internal readonly record struct ControllerRoute(CapturedController? Controller, string Reason);

internal static class ControllerRouting
{
    internal static bool AffectedByLoss(long inputHandle, long mappedHandle, CapturedController candidate, CapturedController lost) =>
        mappedHandle == inputHandle || IsHandlelessAlias(mappedHandle, candidate, lost) ||
        inputHandle == 0 && candidate.DeviceType is HapticDeviceType.Generic or HapticDeviceType.Touchpad &&
        SameController(lost, candidate);

    internal static bool IsHandlelessAlias(long handle, CapturedController candidate, CapturedController removed) =>
        handle == 0 &&
        candidate.DeviceType is HapticDeviceType.Touchpad or HapticDeviceType.Generic &&
        SameController(removed, candidate);

    internal static bool SameController(CapturedController left, CapturedController right) =>
        ReferenceEquals(left, right) ||
        left.Controller is { } controller && ReferenceEquals(controller, right.Controller) ||
        HasStableId(left) && left.IdHash == right.IdHash;

    internal static ControllerRoute Resolve(
        IReadOnlyDictionary<long, CapturedController>? controllers,
        long deviceHandle,
        PointerInputClass inputClass)
    {
        if (inputClass == PointerInputClass.Promoted)
        {
            return new(null, "skip:promoted-input");
        }

        if (deviceHandle != 0)
        {
            if (inputClass != PointerInputClass.Handle)
            {
                return new(null, "skip:input-class-mismatch");
            }

            return controllers is not null && controllers.TryGetValue(deviceHandle, out var mapped)
                ? new(mapped, "mapped-device")
                : new(null, "skip:unmapped-device");
        }

        if (inputClass != PointerInputClass.Handleless)
        {
            return new(null, "skip:unknown-input-origin");
        }

        var touchpads = new List<CapturedController>();
        foreach (var controller in controllers?.Values ?? [])
        {
            var hasStableId = !string.IsNullOrEmpty(controller.IdHash) && controller.IdHash != "none";
            if (controller.DeviceType != HapticDeviceType.Touchpad ||
                touchpads.Any(existing => ReferenceEquals(existing, controller) ||
                    hasStableId && existing.IdHash == controller.IdHash))
            {
                continue;
            }

            touchpads.Add(controller);
        }

        return touchpads.Count switch
        {
            1 => new(touchpads[0], "mapped-touchpad"),
            0 => new(null, "skip:no-touchpad-mapping"),
            _ => new(null, "skip:ambiguous-touchpad"),
        };
    }

    internal static bool MatchesInputClass(PointerInputClass inputClass, HapticDeviceType deviceType) =>
        inputClass switch
        {
            PointerInputClass.Handle => deviceType == HapticDeviceType.Mouse,
            PointerInputClass.Handleless => deviceType == HapticDeviceType.Touchpad,
            _ => false,
        };

    private static bool HasStableId(CapturedController controller) =>
        !string.IsNullOrEmpty(controller.IdHash) && controller.IdHash != "none";

    internal static bool IsManualCaptureValid(
        long deviceHandle,
        PointerInputClass inputClass,
        double inputTimestampMs,
        double nowMs,
        double freshnessMs) =>
        double.IsFinite(inputTimestampMs) && double.IsFinite(nowMs) &&
        double.IsFinite(freshnessMs) && freshnessMs >= 0 &&
        nowMs >= inputTimestampMs && nowMs - inputTimestampMs <= freshnessMs &&
        (deviceHandle == 0
            ? inputClass == PointerInputClass.Handleless
            : inputClass == PointerInputClass.Handle);
}

/// <summary>
/// Cooperative integration ("Best" tier): any app running as the same user can tell us what just happened —
/// e.g. <c>{"waveform":"Align"}</c> when its snap guide appears — over a local named pipe. Apps know outcomes we
/// can't infer, so this is the honest route to Align/Success/Error. Everything still goes through the policy.
/// </summary>
public sealed class CooperativeServer : IDisposable
{
    public const string PipeName = "HapticsEverywhere";
    private readonly Action<string, int> _onIntent;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();
    private readonly string _pipeName;
    private readonly int _clientDeadlineMs;
    private NamedPipeServerStream? _active;
    private Thread? _thread;
    private bool _disposed;

    public CooperativeServer(Action<string, int> onIntent) : this(onIntent, PipeName, 1000) { }

    internal CooperativeServer(Action<string, int> onIntent, string pipeName, int clientDeadlineMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clientDeadlineMs);
        _onIntent = onIntent;
        _pipeName = pipeName;
        _clientDeadlineMs = clientDeadlineMs;
    }

    internal bool IsRunning => _thread?.IsAlive == true;

    public event Action<string>? Diagnostic;

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null)
            {
                return;
            }

            _thread = new Thread(Loop) { IsBackground = true, Name = "haptics-cooperative-pipe" };
            _thread.Start();
        }
    }

    /// <summary>Client helper (also used by `haptics-lab coop-send`).</summary>
    public static bool Send(string waveform, int timeoutMs = 1000)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            c.Connect(timeoutMs);
            var bytes = Encoding.UTF8.GetBytes($"{{\"waveform\":\"{waveform}\"}}\n");
            c.Write(bytes);
            c.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cts.Cancel();
            _active?.Dispose();
            thread = _thread;
        }

        if (thread is not null && thread != Thread.CurrentThread && !thread.Join(2000))
        {
            throw new TimeoutException("The cooperative pipe did not stop after cancellation.");
        }

        _cts.Dispose();
    }

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                lock (_sync)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _active = server;
                }

                server.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();
                GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out var pid);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                deadline.CancelAfter(_clientDeadlineMs);
                ReadClient(server, (int)pid, deadline.Token);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                Report("Cooperative client deadline exceeded; connection closed.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Report($"Cooperative pipe error: {ex.Message}");
                if (_cts.Token.WaitHandle.WaitOne(1000))
                {
                    return;
                }
            }
            finally
            {
                lock (_sync)
                {
                    _active = null;
                }
            }
        }
    }

    private void ReadClient(Stream server, int pid, CancellationToken token)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, bufferSize: 128, leaveOpen: true);
        var buffer = new char[1];
        var line = new StringBuilder(256);
        var skipLf = false;
        var lines = 0;
        while (lines < 64)
        {
            var count = reader.ReadAsync(buffer.AsMemory(), token).GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            if (count == 0)
            {
                if (line.Length > 0)
                {
                    Deliver(line.ToString(), pid);
                }

                return;
            }

            var c = buffer[0];
            if (skipLf && c == '\n')
            {
                skipLf = false;
                continue;
            }

            skipLf = c == '\r';
            if (c is '\r' or '\n')
            {
                Deliver(line.ToString(), pid);
                line.Clear();
                lines++;
            }
            else if (line.Length == 256)
            {
                Report("Cooperative line exceeds 256 characters; connection closed.");
                return;
            }
            else
            {
                line.Append(c);
            }
        }
    }

    private void Deliver(string line, int pid)
    {
        if (Parse(line) is { } waveform)
        {
            try
            {
                _onIntent(waveform, pid);
            }
            catch (Exception ex)
            {
                Report($"Cooperative callback error: {ex.Message}");
            }
        }
    }

    private void Report(string message)
    {
        System.Diagnostics.Trace.TraceWarning(message);
        Diagnostic?.Invoke(message);
    }

    private static string? Parse(string line)
    {
        line = line.Trim();
        if (line.StartsWith('{'))
        {
            try
            {
                return (string?)JsonNode.Parse(line)?["waveform"];
            }
            catch
            {
                return null;
            }
        }

        return line.Length is > 0 and < 32 ? line : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);
}
