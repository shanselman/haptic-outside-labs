using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Haptics.Windows;

/// <summary>
/// Haptic controllers captured per raw input device ("whatever haptic device is wiggled"). Capture only works on a
/// thread whose own window just received input from that device; call <see cref="TryCapture"/> from such a window.
/// </summary>
public sealed class ControllerBank
{
    private IReadOnlyDictionary<long, CapturedController> _map = new Dictionary<long, CapturedController>();

    public CapturedController? Latest { get; private set; }

    public IReadOnlyDictionary<long, CapturedController> Map => _map;

    public bool Any => Latest is not null;

    /// <summary>Capture the device that just moved over our UI. Returns true if something new was added.</summary>
    public bool TryCapture(long deviceHandle)
    {
        var c = HapticsApi.TryCaptureCurrentController();
        if (c is null)
        {
            return false;
        }

        Latest = c;
        if (_map.TryGetValue(deviceHandle, out var known) && known.IdHash == c.IdHash)
        {
            return false;
        }

        _map = new Dictionary<long, CapturedController>(_map) { [deviceHandle] = c };
        return true;
    }

    public string Describe() =>
        Latest is null ? "nothing yet" : string.Join(" + ", _map.Values.Append(Latest).Select(c => c.DeviceType.ToString()).Distinct());

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
