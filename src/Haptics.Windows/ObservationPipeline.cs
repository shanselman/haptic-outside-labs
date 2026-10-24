using System.Collections.Concurrent;
using Haptics.Core;

namespace Haptics.Windows;

public abstract record PipelineEvent(double AtMs);

public sealed record SnapshotEvent(double AtMs, ElementSnapshot Snapshot, double RoundTripMs, IReadOnlyList<string> Notes) : PipelineEvent(AtMs);

public sealed record DecisionEvent(double AtMs, PolicyDecision Decision) : PipelineEvent(AtMs);

public sealed record OutputEvent(double AtMs, HapticIntent Intent, HapticAttempt? Attempt, string Status) : PipelineEvent(AtMs);

public sealed record DiagnosticEvent(double AtMs, string Message) : PipelineEvent(AtMs);

/// <summary>Why something stayed silent before reaching policy (e.g. skip:already-selected, passed-through).</summary>
public sealed record NoteEvent(double AtMs, string Note, ElementSnapshot? Element) : PipelineEvent(AtMs);

public sealed class PipelineOptions
{
    public MessageWindowKind WindowKind { get; init; } = MessageWindowKind.MessageOnly;
    public string Route { get; set; } = OutputRoutes.DryRun;
    public UiaClientOptions Uia { get; init; } = new();
    public Func<UiaClientOptions, UiaClient>? UiaFactory { get; init; }
}

public sealed record PipelineStats(
    long RawEvents,
    long BackgroundRawEvents,
    long Snapshots,
    long Emits,
    long DryRuns,
    long Suppressed,
    long ApiAccepted,
    long ApiRejected,
    long Expired,
    double LookupP50,
    double LookupP95,
    double InputToCallP50,
    double InputToCallP95,
    double MaxRawCallbackMs,
    UiaClientStats? Uia);

/// <summary>
/// Production observation loop (shared by Lab Live Observer and the Tray). Passive: raw mouse input
/// and read-only UIA only. Output happens on the raw-input thread, gated by the central policy.
/// </summary>
public sealed class ObservationPipeline : IDisposable
{
    private readonly object _lock = new();
    private readonly PipelineOptions _opt;
    private readonly SemanticInterpreter _interpreter = new();
    private readonly ConcurrentDictionary<int, (string? Name, double At)> _names = new();
    private readonly HashSet<long> _removedDevices = new();
    private readonly Queue<double> _lookup = new();
    private readonly Queue<double> _inputToCall = new();
    private MessageWindowThread? _thread;
    private Timer? _tick;
    private UiaClient? _uia;
    private bool _primaryDown;
    private (int X, int Y) _lastPos = (int.MinValue, int.MinValue);
    private long _raw, _bgRaw, _snaps, _emits, _dry, _supp, _acc, _rej, _exp;

    public ObservationPipeline(PolicyConfig config, PipelineOptions? options = null)
    {
        _opt = options ?? new PipelineOptions();
        Policy = new HapticPolicy(config, ResolveName);
    }

    public HapticPolicy Policy { get; }

    public string Route
    {
        get => _opt.Route;
        set => _opt.Route = value;
    }

    /// <summary>For R3a/R3b: a controller captured on one of our own input threads.</summary>
    public CapturedController? Controller { get; set; }

    /// <summary>
    /// Optional per-raw-device controllers (e.g. mouse and touchpad both primed). Output goes to the controller of the
    /// device that produced the latest input, falling back to <see cref="Controller"/>. Replace the whole map to update.
    /// </summary>
    public IReadOnlyDictionary<long, CapturedController>? Controllers { get; set; }

    /// <summary>Raw device handle of the most recent mouse input (0 = no handle, e.g. some precision touchpads).</summary>
    public long LastDeviceHandle => Interlocked.Read(ref _lastDevice);

    private long _lastDevice;

    private CapturedController? ControllerForLatestInput() =>
        Controllers is { } map && map.TryGetValue(LastDeviceHandle, out var c) ? c : Controller;

    /// <summary>Touchpads feel lighter than mice at the same intensity; this multiplies their pulses (clamped to 1).</summary>
    public double TouchpadBoost { get; set; } = 1.0;

    public SemanticInterpreter Interpreter => _interpreter;

    public bool Running => _thread is not null;

    /// <summary>An app-reported haptic (cooperative pipe). Arbitrated by the same policy as everything else.</summary>
    public void SubmitCooperative(string waveform, int processId)
    {
        lock (_lock)
        {
            var e = _interpreter.Cooperative(waveform, processId, Now);
            HandleLocked(new InterpretResult([e], []), Now);
        }
    }

    public uint RawThreadId => _thread?.ThreadId ?? 0;

    public bool RawInputRegistered => _thread?.RawInputRegistered ?? false;

    public event Action<PipelineEvent>? Event;

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _uia = _opt.UiaFactory?.Invoke(_opt.Uia) ?? UiaClient.ForCurrentProcess(_opt.Uia);
        _uia.Response += OnUia;
        _uia.Diagnostic += m => Publish(new DiagnosticEvent(Now, m));
        _thread = new MessageWindowThread("haptics-raw-input", _opt.WindowKind, registerRawMouse: true);
        _thread.RawMouse += OnRaw;
        _thread.DeviceChanged += OnDeviceChanged;
        _thread.Start();
        _tick = new Timer(_ => Tick(), null, 15, 15);
        Publish(new DiagnosticEvent(Now, _thread.RawInputRegistered
            ? $"raw input registered on thread {_thread.ThreadId} ({_opt.WindowKind})"
            : $"raw input registration FAILED (win32 {_thread.RawInputRegistrationError})"));
    }

    public void Stop()
    {
        _tick?.Dispose();
        _tick = null;
        _thread?.Dispose();
        _thread = null;
        _uia?.Dispose();
        _uia = null;
        lock (_lock)
        {
            _lastPos = (int.MinValue, int.MinValue);
        }
    }

    public void Dispose() => Stop();

    public PipelineStats Stats()
    {
        lock (_lock)
        {
            return new PipelineStats(_raw, _bgRaw, _snaps, _emits, _dry, _supp, _acc, _rej, _exp,
                Pct(_lookup, 0.5), Pct(_lookup, 0.95), Pct(_inputToCall, 0.5), Pct(_inputToCall, 0.95),
                _thread?.MaxCallbackMs ?? 0, _uia?.Stats);
        }
    }

    private static double Now => MonotonicClock.Instance.NowMs;

    private void Tick()
    {
        // A pointer that stops on a target sends no more input; dwell landings resolve here.
        lock (_lock)
        {
            if (_interpreter.HasPendingArrival)
            {
                HandleLocked(_interpreter.OnTick(Now), Now);
            }
        }
    }

    private void OnRaw(RawMouseInput raw)
    {
        // Raw-input thread: keep it tiny. No UIA, no disk, no process enumeration.
        if ((raw.Buttons & RawMouseButtons.LeftDown) != 0)
        {
            _primaryDown = true;
        }

        if ((raw.Buttons & RawMouseButtons.LeftUp) != 0)
        {
            _primaryDown = false;
        }

        var (x, y) = DesktopContext.CursorPosition();
        var fg = DesktopContext.Foreground().Pid;
        Interlocked.Exchange(ref _lastDevice, raw.DeviceHandle);
        var obs = new PointerObservation(raw.TimestampMs, x, y, raw.Origin, _primaryDown, fg, raw.DeviceHandle);
        bool moved;
        lock (_lock)
        {
            _raw++;
            _interpreter.Config.LandingSpeedPxPerSec = Policy.Config.LandingSpeedPxPerSec;
            if (raw.Code == RawInputCode.Background)
            {
                _bgRaw++;
            }

            if (_removedDevices.Count > 0 && raw.DeviceHandle != 0 && !_removedDevices.Contains(raw.DeviceHandle))
            {
                Policy.DeviceAvailable = true;
                _removedDevices.Clear();
            }

            HandleLocked(_interpreter.OnPointer(obs), raw.TimestampMs);
            moved = (x, y) != _lastPos;
            _lastPos = (x, y);
        }

        if (moved || (raw.Buttons & (RawMouseButtons.LeftDown | RawMouseButtons.LeftUp)) != 0)
        {
            _uia?.RequestLatest(x, y, raw.TimestampMs, fg);
        }
    }

    private void OnUia(UiaResponse resp, double pointerTs, double roundTripMs)
    {
        var now = Now;
        var snap = resp.Snapshot ?? new ElementSnapshot { Error = resp.Error ?? resp.ClientStatus ?? "no-snapshot", RequestedAtMs = pointerTs, ReceivedAtMs = now };
        lock (_lock)
        {
            _snaps++;
            Push(_lookup, resp.LookupMs);
            var r = _interpreter.OnSnapshot(snap, now);
            Publish(new SnapshotEvent(now, snap, roundTripMs, r.Notes));
            HandleLocked(r, now);
        }
    }

    private void OnDeviceChanged(long handle, bool arrived)
    {
        lock (_lock)
        {
            if (arrived)
            {
                Publish(new DiagnosticEvent(Now, $"device arrived ({RawInputDevices.Describe(handle).VidPid ?? "unknown"})"));
                return;
            }

            _removedDevices.Add(handle);
            Policy.DeviceAvailable = false;
            HandleLocked(_interpreter.OnDeviceUnavailable(Now), Now);
            Publish(new DiagnosticEvent(Now, "device removed: output disabled until input arrives from a present device"));
        }
    }

    private void HandleLocked(InterpretResult r, double now)
    {
        foreach (var n in r.Notes)
        {
            if (n.StartsWith("skip:", StringComparison.Ordinal) || n == "passed-through")
            {
                Publish(new NoteEvent(now, n, _interpreter.Current));
            }
        }

        foreach (var e in r.Events)
        {
            var d = Policy.Evaluate(e, Now);
            switch (d.Outcome)
            {
                case PolicyOutcome.Emit:
                    _emits++;
                    break;
                case PolicyOutcome.DryRun:
                    _dry++;
                    break;
                default:
                    _supp++;
                    break;
            }

            Publish(new DecisionEvent(Now, d));
            if (d.Outcome == PolicyOutcome.Emit && d.Intent is { } intent)
            {
                _thread?.Post(() => Output(intent));
            }
        }
    }

    private void Output(HapticIntent intent)
    {
        // Raw-input thread. Drop, never replay, late sensations.
        var now = Now;
        if (now > intent.DeadlineMs)
        {
            lock (_lock)
            {
                _exp++;
            }

            Publish(new OutputEvent(now, intent, null, "expired-before-output"));
            return;
        }

        var controller = ControllerForLatestInput();
        var intensity = controller?.DeviceType == global::Windows.Devices.Haptics.HapticDeviceType.Touchpad
            ? Math.Min(1.0, intent.Intensity * TouchpadBoost)
            : intent.Intensity;
        HapticAttempt? a = Route switch
        {
            OutputRoutes.RawThreadManager => HapticsApi.SendViaThreadManager(Route, intent.Waveform, intensity, intent.PointerTimestampMs),
            OutputRoutes.CachedController => HapticsApi.SendViaController(Route, controller, intent.Waveform, intensity, intent.PointerTimestampMs),
            OutputRoutes.OverrideOnRawThread => HapticsApi.SendViaOverride(Route, controller, intent.Waveform, intensity, intent.PointerTimestampMs),
            _ => null,
        };

        lock (_lock)
        {
            if (a?.Accepted == true)
            {
                _acc++;
            }
            else if (a is not null)
            {
                _rej++;
            }

            if (a?.InputToCallMs is { } itc)
            {
                Push(_inputToCall, itc);
            }
        }

        Publish(new OutputEvent(Now, intent, a, a is null ? "dry-run-route" : a.Accepted ? "api-accepted" : $"api-rejected:{a.Error ?? "false"}"));
    }

    private string? ResolveName(int pid)
    {
        var now = Now;
        if (_names.TryGetValue(pid, out var e) && now - e.At < 30_000)
        {
            return e.Name;
        }

        var name = DesktopContext.ProcessName(pid);
        _names[pid] = (name, now);
        return name;
    }

    private void Publish(PipelineEvent e)
    {
        try
        {
            Event?.Invoke(e);
        }
        catch
        {
            // Listener faults must not break observation.
        }
    }

    private static void Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > 2000)
        {
            q.Dequeue();
        }
    }

    private static double Pct(IEnumerable<double> values, double p)
    {
        var a = values.OrderBy(v => v).ToArray();
        if (a.Length == 0)
        {
            return double.NaN;
        }

        return a[Math.Clamp((int)Math.Ceiling(p * a.Length) - 1, 0, a.Length - 1)];
    }
}
