using System.Collections.Concurrent;
using Haptics.Core;

namespace Haptics.Windows;

public abstract record PipelineEvent(double AtMs);

public sealed record SnapshotEvent(double AtMs, ElementSnapshot Snapshot, double RoundTripMs, IReadOnlyList<string> Notes) : PipelineEvent(AtMs);

public sealed record DecisionEvent(double AtMs, PolicyDecision Decision) : PipelineEvent(AtMs);

public sealed record OutputEvent(double AtMs, HapticIntent Intent, HapticAttempt? Attempt, string Status) : PipelineEvent(AtMs);

public sealed record DiagnosticEvent(double AtMs, string Message) : PipelineEvent(AtMs);

public sealed record DeviceChangeEvent(double AtMs, long Handle, bool Arrived) : PipelineEvent(AtMs);

/// <summary>Why something stayed silent before reaching policy (e.g. skip:already-selected, passed-through).</summary>
public sealed record NoteEvent(double AtMs, string Note, ElementSnapshot? Element) : PipelineEvent(AtMs);

public sealed record PipelineOutputRequest(string Route, CapturedController? Controller, HapticIntent Intent, double Intensity);

public sealed record LatestPointerInput(long DeviceHandle, PointerInputClass InputClass, double TimestampMs);

/// <summary>Optional output seam. Production posts and sends on the raw-input thread when no sink is supplied.</summary>
public interface IPipelineOutputSink
{
    void Post(Action output);
    HapticAttempt? Send(PipelineOutputRequest request);
}

public sealed class PipelineOptions
{
    public IClock Clock { get; init; } = MonotonicClock.Instance;
    public IPipelineOutputSink? OutputSink { get; init; }
    public MessageWindowKind WindowKind { get; init; } = MessageWindowKind.MessageOnly;
    public string Route { get; set; } = OutputRoutes.DryRun;
    public UiaClientOptions Uia { get; init; } = new();
    public Func<UiaClientOptions, UiaClient>? UiaFactory { get; init; }
    internal Func<(int X, int Y)> PointerPosition { get; init; } = DesktopContext.CursorPosition;
    internal Func<int> ForegroundPid { get; init; } = () => DesktopContext.Foreground().Pid;
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
    private const double CooperativeInputFreshnessMs = 250;

    private readonly object _lock = new();
    private readonly PipelineOptions _opt;
    private readonly SemanticInterpreter _interpreter = new();
    private readonly ConcurrentDictionary<int, (string? Name, double At)> _names = new();
    private readonly HashSet<long> _removedDevices = new();
    private readonly Dictionary<long, CapturedController> _removedControllers = new();
    private readonly Dictionary<(double TimestampMs, long Generation), InputRouteContext> _requestRoutes = new();
    private readonly Queue<double> _lookup = new();
    private readonly Queue<double> _inputToCall = new();
    private MessageWindowThread? _thread;
    private Timer? _tick;
    private UiaClient? _uia;
    private bool _primaryDown;
    private (int X, int Y) _lastPos = (int.MinValue, int.MinValue);
    private InputRouteContext? _latestInputRoute;
    private CapturedController? _controller;
    private IReadOnlyDictionary<long, CapturedController>? _controllers;
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

    /// <summary>Explicit single-controller fallback for experiments. Tray and Showcase leave this unset.</summary>
    public CapturedController? Controller
    {
        get { lock (_lock) { return _controller; } }
        set { lock (_lock) { _controller = value; } }
    }

    /// <summary>Optional exact raw-device mappings. Replace the whole map to update it.</summary>
    public IReadOnlyDictionary<long, CapturedController>? Controllers
    {
        get { lock (_lock) { return _controllers; } }
        set
        {
            lock (_lock)
            {
                var previous = _controllers;
                _controllers = value;
                var touchpad = value?.GetValueOrDefault(0);
                var previousTouchpad = previous?.GetValueOrDefault(0);
                if (touchpad?.DeviceType == global::Windows.Devices.Haptics.HapticDeviceType.Touchpad &&
                    !ReferenceEquals(touchpad, previousTouchpad) &&
                    _removedControllers.TryGetValue(0, out var removedAlias) &&
                    ControllerRouting.SameController(removedAlias, touchpad))
                {
                    _removedControllers.Remove(0);
                }

                foreach (var handle in _removedDevices.ToArray())
                {
                    var current = value?.GetValueOrDefault(handle);
                    var replacedAtHandle = current is not null &&
                        (!_removedControllers.TryGetValue(handle, out var removed) || !ReferenceEquals(current, removed));
                    var handlelessReprime = current is null &&
                        touchpad?.DeviceType == global::Windows.Devices.Haptics.HapticDeviceType.Touchpad &&
                        !ReferenceEquals(touchpad, previousTouchpad) &&
                        _removedControllers.TryGetValue(handle, out var removedController) &&
                        ControllerRouting.SameController(removedController, touchpad);
                    if (replacedAtHandle)
                    {
                        _removedDevices.Remove(handle);
                        _removedControllers.Remove(handle);
                    }
                    else if (handlelessReprime)
                    {
                        _removedControllers.Remove(0);
                    }
                }
            }
        }
    }

    /// <summary>Raw device handle of the most recent mouse input (0 = no handle, e.g. some precision touchpads).</summary>
    public long LastDeviceHandle => Volatile.Read(ref _latestInputRoute)?.DeviceHandle ?? 0;

    public PointerInputClass LastInputClass => Volatile.Read(ref _latestInputRoute)?.InputClass ?? PointerInputClass.Promoted;

    public double LastInputTimestampMs => Volatile.Read(ref _latestInputRoute)?.TimestampMs ?? double.NaN;

    public LatestPointerInput? LatestInput
    {
        get
        {
            lock (_lock)
            {
                return _latestInputRoute is { } input
                    ? new LatestPointerInput(input.DeviceHandle, input.InputClass, input.TimestampMs)
                    : null;
            }
        }
    }

    private CapturedController? ControllerForLatestInput()
    {
        lock (_lock)
        {
            return Volatile.Read(ref _latestInputRoute) is { } input ? ResolveController(input).Controller : Controller;
        }
    }

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
            HandleLocked(new InterpretResult([e], []), Now, FreshCooperativeInput(Now));
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
        RevokeOutput();
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

    /// <summary>Revoke output and pending work before any teardown or settings I/O.</summary>
    public void RevokeOutput()
    {
        lock (_lock)
        {
            Policy.Config.Mode = HapticsMode.Off;
            Policy.ConsentGiven = false;
            _interpreter.Invalidate("output-revoked");
            _requestRoutes.Clear();
            _latestInputRoute = null;
        }
    }

    public PipelineStats Stats()
    {
        lock (_lock)
        {
            return new PipelineStats(_raw, _bgRaw, _snaps, _emits, _dry, _supp, _acc, _rej, _exp,
                Pct(_lookup, 0.5), Pct(_lookup, 0.95), Pct(_inputToCall, 0.5), Pct(_inputToCall, 0.95),
                _thread?.MaxCallbackMs ?? 0, _uia?.Stats);
        }
    }

    private double Now => _opt.Clock.NowMs;

    private void Tick()
    {
        // A pointer that stops on a target sends no more input; dwell landings resolve here.
        lock (_lock)
        {
            if (_interpreter.HasPendingArrival)
            {
                HandleLocked(_interpreter.OnTick(Now), Now, _latestInputRoute);
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

        var (x, y) = _opt.PointerPosition();
        var fg = _opt.ForegroundPid();
        var obs = new PointerObservation(raw.TimestampMs, x, y, raw.Origin, _primaryDown, fg, raw.DeviceHandle)
        {
            InputClass = raw.InputClass,
        };
        bool moved;
        long generation;
        lock (_lock)
        {
            _raw++;
            _interpreter.Config.LandingSpeedPxPerSec = Policy.Config.LandingSpeedPxPerSec;
            if (raw.Code == RawInputCode.Background)
            {
                _bgRaw++;
            }

            var previousGeneration = _interpreter.Generation;
            var input = ObservePointerLocked(obs);
            generation = input.Generation;
            moved = (x, y) != _lastPos || previousGeneration != generation;
            _lastPos = (x, y);
            if (moved || (raw.Buttons & (RawMouseButtons.LeftDown | RawMouseButtons.LeftUp)) != 0)
            {
                _requestRoutes[(raw.TimestampMs, input.Generation)] = input;
                while (_requestRoutes.Count > 64)
                {
                    _requestRoutes.Remove(_requestRoutes.Keys.MinBy(key => key.TimestampMs));
                }
            }
        }

        if (moved || (raw.Buttons & (RawMouseButtons.LeftDown | RawMouseButtons.LeftUp)) != 0)
        {
            _uia?.RequestLatest(x, y, raw.TimestampMs, fg, generation);
        }
    }

    private void OnUia(UiaResponse resp, double pointerTs, double roundTripMs)
    {
        var now = Now;
        var snap = resp.Snapshot ?? new ElementSnapshot { Error = resp.Error ?? resp.ClientStatus ?? "no-snapshot", RequestedAtMs = pointerTs, ReceivedAtMs = now, Generation = resp.Generation };
        lock (_lock)
        {
            _snaps++;
            Push(_lookup, resp.LookupMs);
            InputRouteContext? input = null;
            if (_requestRoutes.Remove((pointerTs, resp.Generation), out var requested))
            {
                input = requested;
            }
            else if (_latestInputRoute is { } latest && latest.TimestampMs == pointerTs)
            {
                input = latest;
            }

            if (input is null)
            {
                Publish(new NoteEvent(now, "skip:unknown-input-origin", snap));
                return;
            }

            if (input.Generation != _interpreter.Generation || resp.Generation != input.Generation)
            {
                Publish(new NoteEvent(now, "skip:stale-device-generation", snap));
                return;
            }

            var r = _interpreter.OnSnapshot(snap, now);
            Publish(new SnapshotEvent(now, snap, roundTripMs, r.Notes));
            HandleLocked(r, now, input);
        }
    }

    private void OnDeviceChanged(long handle, bool arrived)
    {
        lock (_lock)
        {
            Publish(new DeviceChangeEvent(Now, handle, arrived));
            if (arrived)
            {
                Publish(new DiagnosticEvent(Now, $"device arrived ({RawInputDevices.Describe(handle).VidPid ?? "unknown"})"));
                return;
            }

            _removedDevices.Add(handle);
            if (Controllers is { } controllers && controllers.TryGetValue(handle, out var controller))
            {
                foreach (var (mappedHandle, mappedController) in controllers)
                {
                    if (ControllerRouting.SameController(controller, mappedController))
                    {
                        _removedControllers[mappedHandle] = mappedController;
                    }
                }
            }

            HandleLocked(_interpreter.OnDeviceUnavailable(Now), Now);
            Publish(new DiagnosticEvent(Now, "device removed: its route is disabled until rediscovery or priming"));
        }
    }

    internal void ObservePointer(PointerObservation observation)
    {
        lock (_lock)
        {
            ObservePointerLocked(observation);
        }
    }

    private InputRouteContext ObservePointerLocked(PointerObservation observation)
    {
        var result = _interpreter.OnPointer(observation);
        var input = new InputRouteContext(
            observation.DeviceHandle, observation.InputClass, observation.ForegroundPid, observation.TimestampMs, _interpreter.Generation);
        Volatile.Write(ref _latestInputRoute, input);
        HandleLocked(result, observation.TimestampMs, input);
        return input;
    }

    private void HandleLocked(InterpretResult r, double now, InputRouteContext? input = null)
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
                if (e.Kind == SemanticEventKind.Cooperative)
                {
                    input ??= FreshCooperativeInput(now);
                    if (input is null)
                    {
                        Publish(new NoteEvent(now, "skip:no-fresh-input-origin", e.Element));
                        continue;
                    }
                }

                if (input is null)
                {
                    Publish(new NoteEvent(now, "skip:unknown-input-origin", e.Element));
                    continue;
                }

                if (input.Generation != _interpreter.Generation || e.Generation != input.Generation)
                {
                    Publish(new NoteEvent(now, "skip:stale-device-generation", e.Element));
                    continue;
                }

                var route = ResolveController(input);
                if (route.Controller is not { } controller)
                {
                    Publish(new NoteEvent(now, route.Reason, e.Element));
                    continue;
                }

                if (_opt.OutputSink is { } sink)
                {
                    sink.Post(() => Output(intent, input, controller));
                }
                else
                {
                    _thread?.Post(() => Output(intent, input, controller));
                }
            }
        }
    }

    private void Output(HapticIntent intent, InputRouteContext? input = null, CapturedController? controller = null)
    {
        // Serialize revocation with the final authorization check and native call.
        lock (_lock)
        {
            OutputLocked(intent, input, controller);
        }
    }

    private void OutputLocked(HapticIntent intent, InputRouteContext? input, CapturedController? controller)
    {
        // Raw-input thread. Drop, never replay, late sensations.
        var now = Now;
        if (Policy.Config.Mode != HapticsMode.On || !Policy.ConsentGiven || !Policy.OutputRouteVerified)
        {
            Publish(new NoteEvent(now, "skip:output-not-authorized", null));
            return;
        }

        if (now > intent.DeadlineMs)
        {
            lock (_lock)
            {
                _exp++;
            }

            Publish(new OutputEvent(now, intent, null, "expired-before-output"));
            return;
        }

        if (input is not null)
        {
            string? skip = null;
            lock (_lock)
            {
                if (intent.SourceRule == "cooperative" &&
                    (now < input.TimestampMs || now - input.TimestampMs > CooperativeInputFreshnessMs))
                {
                    skip = "skip:stale-input-origin";
                }
                else if (input.Generation != _interpreter.Generation)
                {
                    skip = "skip:stale-device-generation";
                }
                else if (!ReferenceEquals(ResolveController(input).Controller, controller))
                {
                    skip = "skip:stale-device-mapping";
                }
            }

            if (skip is not null)
            {
                Publish(new NoteEvent(now, skip, null));
                return;
            }
        }

        controller ??= ControllerForLatestInput();
        var intensity = controller?.DeviceType == global::Windows.Devices.Haptics.HapticDeviceType.Touchpad
            ? Math.Min(1.0, intent.Intensity * TouchpadBoost)
            : intent.Intensity;
        HapticAttempt? a = _opt.OutputSink is { } sink
            ? sink.Send(new PipelineOutputRequest(Route, controller, intent, intensity))
            : Route switch
        {
            OutputRoutes.RawThreadManager => HapticsApi.SendViaThreadManager(
                Route, intent.Waveform, intensity, intent.PointerTimestampMs, expectedController: controller),
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

    private InputRouteContext? FreshCooperativeInput(double now) =>
        _latestInputRoute is { } input &&
        input.Generation == _interpreter.Generation &&
        now >= input.TimestampMs &&
        now - input.TimestampMs <= CooperativeInputFreshnessMs
            ? input
            : null;

    private ControllerRoute ResolveController(InputRouteContext input)
    {
        if (input.DeviceHandle != 0 && _removedDevices.Contains(input.DeviceHandle))
        {
            return new(null, "skip:removed-device");
        }

        var route = ControllerRouting.Resolve(Controllers, input.DeviceHandle, input.InputClass);
        if (route.Controller is { } mapped &&
            _removedControllers.Any(pair =>
                ControllerRouting.SameController(pair.Value, mapped) &&
                (pair.Key == input.DeviceHandle || ReferenceEquals(pair.Value, mapped))))
        {
            return new(null, "skip:removed-device");
        }

        if (route.Controller is not null || Controller is null || Controllers is { Count: > 0 })
        {
            return route;
        }

        return route.Reason is "skip:unmapped-device" or "skip:no-touchpad-mapping"
            ? new ControllerRoute(Controller, "explicit-controller")
            : route;
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

    private sealed record InputRouteContext(
        long DeviceHandle,
        PointerInputClass InputClass,
        int ForegroundPid,
        double TimestampMs,
        long Generation);
}
