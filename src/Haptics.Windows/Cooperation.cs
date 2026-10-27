using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Haptics.Windows;

/// <summary>
/// Haptic controllers matched to raw input devices. Manual priming requires input to our own UI.
/// Automatically discovered controllers are matched strictly; they never become the fallback for another device.
/// </summary>
public sealed class ControllerBank
{
    private IReadOnlyDictionary<long, CapturedController> _map = new Dictionary<long, CapturedController>();

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

    public bool RemoveDevice(long handle)
    {
        if (!_map.TryGetValue(handle, out var removed))
        {
            return false;
        }

        var map = new Dictionary<long, CapturedController>(_map);
        map.Remove(handle);
        _map = map;
        if (Latest?.IdHash == removed.IdHash)
        {
            Latest = null;
        }

        return true;
    }

    /// <summary>Capture the device that just moved over our UI. Returns true if something new was added.</summary>
    public bool TryCapture(long deviceHandle)
    {
        var c = HapticsApi.TryCaptureCurrentController();
        if (c is null)
        {
            return false;
        }

        var fallbackChanged = Latest?.IdHash != c.IdHash || Latest?.DeviceType != c.DeviceType;
        Latest = c;
        if (_map.TryGetValue(deviceHandle, out var known) && known.IdHash == c.IdHash && known.DeviceType == c.DeviceType)
        {
            return fallbackChanged;
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
            pipeline.Controller = Latest;
            pipeline.Controllers = _map;
        }
    }
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
    private Thread? _thread;

    public CooperativeServer(Action<string, int> onIntent) => _onIntent = onIntent;

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "haptics-cooperative-pipe" };
        _thread.Start();
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
        _cts.Cancel();
        _cts.Dispose();
    }

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                server.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();
                GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out var pid);
                using var reader = new StreamReader(server, Encoding.UTF8);
                for (var lines = 0; lines < 64 && reader.ReadLine() is { } line; lines++)
                {
                    if (line.Length > 256)
                    {
                        break;
                    }

                    var waveform = Parse(line);
                    if (waveform is not null)
                    {
                        _onIntent(waveform, (int)pid);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                Thread.Sleep(100); // Malformed client or pipe error: keep serving.
            }
        }
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
