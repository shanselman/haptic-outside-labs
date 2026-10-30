using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using Haptics.Core;

namespace Haptics.Windows;

public sealed class UiaRequest
{
    public long Id { get; set; }
    public string Cmd { get; set; } = "query";
    public int X { get; set; }
    public int Y { get; set; }
    public bool AutomationId { get; set; }
}

public sealed class UiaResponse
{
    public long Id { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public double LookupMs { get; set; }
    public ElementSnapshot? Snapshot { get; set; }
    public int WorkerPid { get; set; }

    /// <summary>Worker start-up handshake (id 0). Not a query response.</summary>
    public bool Ready { get; set; }

    /// <summary>Client-side: request was superseded, timed out, or the worker restarted.</summary>
    public string? ClientStatus { get; set; }
}

/// <summary>
/// Runs inside a dedicated worker PROCESS on a windowless MTA thread (SPEC §4.5/§9.1). Read-only:
/// ElementFromPoint with a minimal cache request. Never reads Name/Value/Text, never invokes patterns.
/// Protocol: one JSON request per stdin line, one JSON response per stdout line.
/// </summary>
public static class UiaWorker
{
    public const string Arg = "--uia-worker";
    public const string FaultInjectionArg = "--allow-fault-injection";

    public static int Run(string[] args)
    {
        var fault = args.Contains(FaultInjectionArg);
        var exit = 0;
        var t = new Thread(() => exit = Loop(fault)) { Name = "uia-worker-mta" };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        t.Join();
        return exit;
    }

    private static int Loop(bool allowFaults)
    {
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var cache = BuildCacheRequest();
        var pid = Environment.ProcessId;

        // Announce readiness so process start-up never counts against a query's deadline.
        output.WriteLine(JsonSerializer.Serialize(new UiaResponse { Id = 0, Ok = true, Ready = true, WorkerPid = pid }, HapticsJson.Compact));
        while (input.ReadLine() is { } line)
        {
            UiaRequest? req;
            try
            {
                req = JsonSerializer.Deserialize<UiaRequest>(line, HapticsJson.Compact);
            }
            catch (JsonException)
            {
                continue;
            }

            if (req is null)
            {
                continue;
            }

            UiaResponse resp;
            switch (req.Cmd)
            {
                case "ping":
                    resp = new UiaResponse { Id = req.Id, Ok = true };
                    break;
                case "hang" when allowFaults:
                    Thread.Sleep(Timeout.Infinite);
                    return 0;
                case "slow" when allowFaults:
                    Thread.Sleep(req.X);
                    resp = new UiaResponse { Id = req.Id, Ok = true, LookupMs = req.X };
                    break;
                case "crash" when allowFaults:
                    Environment.Exit(3);
                    return 3;
                case "query":
                    resp = Query(req, cache);
                    break;
                default:
                    resp = new UiaResponse { Id = req.Id, Ok = false, Error = "unsupported-command" };
                    break;
            }

            resp.WorkerPid = pid;
            output.WriteLine(JsonSerializer.Serialize(resp, HapticsJson.Compact));
        }

        return 0;
    }

    private static CacheRequest BuildCacheRequest()
    {
        var c = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
        foreach (var p in new AutomationProperty[]
                 {
                     AutomationElement.ControlTypeProperty, AutomationElement.BoundingRectangleProperty,
                     AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty, AutomationElement.IsPasswordProperty,
                     AutomationElement.ProcessIdProperty, AutomationElement.NativeWindowHandleProperty, AutomationElement.FrameworkIdProperty,
                     AutomationElement.ClassNameProperty,
                     AutomationElement.IsControlElementProperty, AutomationElement.AutomationIdProperty, AutomationElement.RuntimeIdProperty,
                     AutomationElement.IsRangeValuePatternAvailableProperty, RangeValuePattern.ValueProperty, RangeValuePattern.MinimumProperty,
                     RangeValuePattern.MaximumProperty, RangeValuePattern.SmallChangeProperty, RangeValuePattern.LargeChangeProperty,
                     AutomationElement.IsSelectionItemPatternAvailableProperty, SelectionItemPattern.IsSelectedProperty,
                 })
        {
            c.Add(p);
        }

        c.Add(RangeValuePattern.Pattern);
        c.Add(SelectionItemPattern.Pattern);
        return c;
    }

    private static UiaResponse Query(UiaRequest req, CacheRequest cache)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            AutomationElement el;
            AutomationElement? parent = null;
            using (cache.Activate())
            {
                el = AutomationElement.FromPoint(new System.Windows.Point(req.X, req.Y));
                var type = TypeName(el);
                AutomationElement? partOf = type is "Thumb" or "Button" ? Parent(el, cache) : null;
                if (partOf is not null && TypeName(partOf) == "Slider")
                {
                    // A slider's thumb or track (repeat) buttons are parts of the slider, not buttons to land on.
                    el = partOf;
                    type = "Slider";
                    partOf = null;
                }
                else if (type == "Pane" && DrillDownHosts.Contains(Get<string>(el, AutomationElement.ClassNameProperty) ?? "") &&
                         HostedControlAt(el, req.X, req.Y, cache) is { } hosted)
                {
                    // ElementFromPoint stops at the taskbar's HWND and never enters its XAML island.
                    el = hosted;
                    type = TypeName(el);
                }

                if (SemanticVocabulary.InteractiveTypes.Contains(type))
                {
                    parent = partOf ?? Parent(el, cache);
                }
            }

            var snap = ToSnapshot(el, parent, req);
            return new UiaResponse { Id = req.Id, Ok = true, Snapshot = snap with { LookupMs = sw.Elapsed.TotalMilliseconds }, LookupMs = sw.Elapsed.TotalMilliseconds };
        }
        catch (ElementNotAvailableException)
        {
            return Fail(req, sw, "element-not-available");
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(req, sw, "access-denied");
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            return Fail(req, sw, $"com-0x{ex.HResult:X8}");
        }
        catch (Exception ex)
        {
            return Fail(req, sw, ex.GetType().Name);
        }
    }

    /// <summary>Top-level windows whose interactive content ElementFromPoint does not reach (Windows 11 taskbars).</summary>
    private static readonly HashSet<string> DrillDownHosts = new(StringComparer.Ordinal) { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    private static readonly Dictionary<int, (long At, List<(AutomationElement El, System.Windows.Rect Rect)> Items)> HostCache = new();

    /// <summary>Smallest interactive descendant of a drill-down host containing the point; host contents cached ~1.5 s.</summary>
    private static AutomationElement? HostedControlAt(AutomationElement host, int x, int y, CacheRequest cache)
    {
        var key = Get<int>(host, AutomationElement.NativeWindowHandleProperty);
        var now = Environment.TickCount64;
        if (!HostCache.TryGetValue(key, out var entry) || now - entry.At > 1500)
        {
            var items = new List<(AutomationElement, System.Windows.Rect)>();
            foreach (AutomationElement d in host.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                if (SemanticVocabulary.InteractiveTypes.Contains(TypeName(d)) && Get<System.Windows.Rect>(d, AutomationElement.BoundingRectangleProperty) is { IsEmpty: false } r)
                {
                    items.Add((d, r));
                }
            }

            entry = (now, items);
            HostCache[key] = entry;
        }

        return entry.Items.Where(i => i.Rect.Contains(x, y)).OrderBy(i => i.Rect.Width * i.Rect.Height).Select(i => i.El).FirstOrDefault();
    }

    private static UiaResponse Fail(UiaRequest req, Stopwatch sw, string error) => new()
    {
        Id = req.Id,
        Ok = false,
        Error = error,
        LookupMs = sw.Elapsed.TotalMilliseconds,
        Snapshot = new ElementSnapshot { QueryX = req.X, QueryY = req.Y, Error = error, LookupMs = sw.Elapsed.TotalMilliseconds },
    };

    private static AutomationElement? Parent(AutomationElement el, CacheRequest cache)
    {
        try
        {
            return TreeWalker.ControlViewWalker.GetParent(el, cache);
        }
        catch
        {
            return null;
        }
    }

    private static ElementSnapshot ToSnapshot(AutomationElement el, AutomationElement? parent, UiaRequest req)
    {
        var rect = Get<System.Windows.Rect>(el, AutomationElement.BoundingRectangleProperty);
        RangeInfo? range = null;
        if (Get<bool>(el, AutomationElement.IsRangeValuePatternAvailableProperty))
        {
            range = new RangeInfo(
                Get<double>(el, RangeValuePattern.ValueProperty),
                Get<double>(el, RangeValuePattern.MinimumProperty),
                Get<double>(el, RangeValuePattern.MaximumProperty),
                Get<double>(el, RangeValuePattern.SmallChangeProperty),
                Get<double>(el, RangeValuePattern.LargeChangeProperty));
        }

        return new ElementSnapshot
        {
            Source = "uia",
            ProcessId = Get<int>(el, AutomationElement.ProcessIdProperty),
            Hwnd = Get<int>(el, AutomationElement.NativeWindowHandleProperty),
            ControlType = TypeName(el),
            FrameworkId = Get<string>(el, AutomationElement.FrameworkIdProperty),
            RuntimeIdHash = HashRuntimeId(Get<int[]>(el, AutomationElement.RuntimeIdProperty)),
            Bounds = rect.IsEmpty || double.IsInfinity(rect.Width) ? null : new RectD(rect.X, rect.Y, rect.Width, rect.Height),
            IsEnabled = Get<bool>(el, AutomationElement.IsEnabledProperty),
            IsOffscreen = Get<bool>(el, AutomationElement.IsOffscreenProperty),
            IsPassword = Get<bool>(el, AutomationElement.IsPasswordProperty),
            IsControlElement = Get<bool>(el, AutomationElement.IsControlElementProperty),
            ParentControlType = parent is null ? null : TypeName(parent),
            ParentRuntimeIdHash = parent is null ? null : HashRuntimeId(Get<int[]>(parent, AutomationElement.RuntimeIdProperty)),
            ParentBounds = parent is null ? null : ToRect(Get<System.Windows.Rect>(parent, AutomationElement.BoundingRectangleProperty)),
            IsSelected = Get<bool>(el, AutomationElement.IsSelectionItemPatternAvailableProperty) ? Get<bool>(el, SelectionItemPattern.IsSelectedProperty) : null,
            Scale = MonitorScale(req.X, req.Y),
            Range = range,
            AutomationId = req.AutomationId ? Get<string>(el, AutomationElement.AutomationIdProperty) : null,
            QueryX = req.X,
            QueryY = req.Y,
        };
    }

    private static RectD? ToRect(System.Windows.Rect r) =>
        r.IsEmpty || double.IsInfinity(r.Width) ? null : new RectD(r.X, r.Y, r.Width, r.Height);

    private static double MonitorScale(int x, int y)
    {
        try
        {
            var mon = MonitorFromPoint(new Native.POINT { X = x, Y = y }, 2 /* MONITOR_DEFAULTTONEAREST */);
            return GetDpiForMonitor(mon, 0 /* MDT_EFFECTIVE_DPI */, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Native.POINT pt, uint flags);

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    private static string TypeName(AutomationElement el)
    {
        var ct = Get<ControlType>(el, AutomationElement.ControlTypeProperty);
        var name = ct?.ProgrammaticName ?? "ControlType.Unknown";
        return name.StartsWith("ControlType.", StringComparison.Ordinal) ? name["ControlType.".Length..] : name;
    }

    private static T? Get<T>(AutomationElement el, AutomationProperty p)
    {
        try
        {
            var v = el.GetCachedPropertyValue(p, ignoreDefaultValue: false);
            return v is T t ? t : default;
        }
        catch
        {
            return default;
        }
    }

    private static string? HashRuntimeId(int[]? id) =>
        id is null || id.Length == 0 ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('.', id))))[..12].ToLowerInvariant();
}

public sealed class UiaClientOptions
{
    public double TimeoutMs { get; init; } = 250;
    public double StartupTimeoutMs { get; init; } = 15_000;
    public bool AllowFaultInjection { get; init; }
    public bool IncludeAutomationId { get; init; }
    public double QuarantineMs { get; init; } = 10_000;
    public int MaxRestartsPerMinute { get; init; } = 6;
}

public sealed record UiaClientStats(long Requests, long Responses, long Superseded, long Timeouts, long Restarts, long Quarantined, long LateDropped);

/// <summary>
/// Owns the UIA worker process: one outstanding request at a time, latest-wins coalescing, hard
/// timeouts with kill+restart, per-foreground-process quarantine after a hang, and no late delivery.
/// </summary>
public sealed class UiaClient : IDisposable
{
    private readonly object _gate = new();
    private readonly UiaClientOptions _opt;
    private readonly string _exe;
    private readonly string[] _baseArgs;
    private readonly Timer _watchdog;
    private readonly Dictionary<long, TaskCompletionSource<UiaResponse>> _waiters = new();
    private readonly Dictionary<int, double> _quarantine = new();
    private readonly Queue<double> _restarts = new();
    private Process? _proc;
    private long _nextId;
    private (long Id, double SentAt, double PointerTs, int FgPid, int X, int Y)? _outstanding;
    private (long Id, string Cmd, int X, int Y, double PointerTs, int FgPid)? _pending;
    private long _requests, _responses, _superseded, _timeouts, _restartCount, _quarantined, _late, _starts;
    private bool _disposed;
    private bool _workerReady;
    private double _workerStartedAt;
    private double? _retryAt;

    public UiaClient(string exe, string[] baseArgs, UiaClientOptions? options = null)
    {
        _exe = exe;
        _baseArgs = baseArgs;
        _opt = options ?? new UiaClientOptions();
        _watchdog = new Timer(_ => Watchdog(), null, 25, 25);
    }

    /// <summary>Raised on a background thread for every delivered (non-stale) response.</summary>
    public event Action<UiaResponse, double, double>? Response;

    public event Action<string>? Diagnostic;

    public UiaClientStats Stats => new(_requests, _responses, _superseded, _timeouts, _restartCount, _quarantined, _late);

    /// <summary>Worker command for the currently running executable (works for apphost and `dotnet x.dll`).</summary>
    public static UiaClient ForCurrentProcess(UiaClientOptions? options = null)
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        var args = new List<string>();
        if (Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
#pragma warning disable IL3000 // Only reached when hosted by dotnet.exe, never in a single-file app.
            args.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
#pragma warning restore IL3000
        }

        args.Add(UiaWorker.Arg);
        if (options?.AllowFaultInjection == true)
        {
            args.Add(UiaWorker.FaultInjectionArg);
        }

        return new UiaClient(path, args.ToArray(), options);
    }

    /// <summary>Latest-wins: replaces any not-yet-sent request. Never queues per-pixel work.</summary>
    public void RequestLatest(int x, int y, double pointerTs, int foregroundPid)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_quarantine.TryGetValue(foregroundPid, out var until) && MonotonicClock.Instance.NowMs < until)
            {
                _quarantined++;
                return;
            }

            if (_pending is { } old)
            {
                _superseded++;
                CompleteWaiter(old.Id, new UiaResponse { Id = old.Id, ClientStatus = "superseded" });
            }

            _pending = (++_nextId, "query", x, y, pointerTs, foregroundPid);
            _requests++;
            PumpLocked();
        }
    }

    /// <summary>Sequential query for experiments. Completes with a ClientStatus on timeout/supersede.</summary>
    public Task<UiaResponse> QueryAsync(int x, int y, string cmd = "query")
    {
        var tcs = new TaskCompletionSource<UiaResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_disposed)
            {
                tcs.SetResult(new UiaResponse { ClientStatus = "disposed", Error = "disposed" });
                return tcs.Task;
            }

            if (_pending is { } old)
            {
                _superseded++;
                CompleteWaiter(old.Id, new UiaResponse { Id = old.Id, ClientStatus = "superseded" });
            }

            var id = ++_nextId;
            lock (_waiters)
            {
                _waiters[id] = tcs;
            }

            _pending = (id, cmd, x, y, MonotonicClock.Instance.NowMs, 0);
            _requests++;
            PumpLocked();
        }

        return tcs.Task;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            KillLocked();
            lock (_waiters)
            {
                foreach (var w in _waiters.Values)
                {
                    w.TrySetResult(new UiaResponse { ClientStatus = "disposed" });
                }

                _waiters.Clear();
            }
        }

        _watchdog.Dispose();
    }

    private void PumpLocked()
    {
        if (_disposed || _outstanding is not null || _pending is not { } next)
        {
            return;
        }

        if (EnsureProcessLocked() is { } failure)
        {
            FailPendingLocked(failure);
            return;
        }

        if (!_workerReady)
        {
            return; // Pending request stays queued (latest-wins) until the worker says it is ready.
        }

        _pending = null;
        var req = new UiaRequest { Id = next.Id, Cmd = next.Cmd, X = next.X, Y = next.Y, AutomationId = _opt.IncludeAutomationId };
        try
        {
            _outstanding = (next.Id, MonotonicClock.Instance.NowMs, next.PointerTs, next.FgPid, next.X, next.Y);
            _proc!.StandardInput.WriteLine(JsonSerializer.Serialize(req, HapticsJson.Compact));
            _proc.StandardInput.Flush();
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"uia-write-failed: {ex.Message}");
            _outstanding = null;
            CompleteWaiter(next.Id, new UiaResponse { Id = next.Id, ClientStatus = "worker-write-failed" });
            KillLocked();
        }
    }

    private void FailPendingLocked(string status)
    {
        if (_pending is not { } pending)
        {
            return;
        }

        _pending = null;
        CompleteWaiter(pending.Id, new UiaResponse { Id = pending.Id, Error = status, ClientStatus = status });
    }

    private string? EnsureProcessLocked()
    {
        if (_proc is { HasExited: false })
        {
            return null;
        }

        var now = MonotonicClock.Instance.NowMs;
        while (_restarts.Count > 0 && now - _restarts.Peek() >= 60_000)
        {
            _restarts.Dequeue();
        }

        if (_restarts.Count >= _opt.MaxRestartsPerMinute)
        {
            Diagnostic?.Invoke("uia-worker-restart-budget-exhausted");
            _retryAt = _restarts.Count > 0 ? _restarts.Peek() + 60_000 : null;
            return "worker-restart-budget-exhausted";
        }

        if (_retryAt is { } retryAt && now < retryAt)
        {
            return "worker-restart-backoff";
        }

        KillLocked();
        var psi = new ProcessStartInfo(_exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in _baseArgs)
        {
            psi.ArgumentList.Add(a);
        }

        // Failed starts count too: a missing executable must not cause an unbounded retry loop.
        _restarts.Enqueue(now);
        try
        {
            var p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
            _proc = p;
            _workerReady = false;
            _workerStartedAt = now;
            _starts++;
            _restartCount = Math.Max(0, _starts - 1);
            _retryAt = null;
            var reader = new Thread(() => ReadLoop(p)) { IsBackground = true, Name = "uia-client-reader" };
            reader.Start();
            return null;
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"uia-worker-start-failed: {ex.Message}");
            _retryAt = now + _opt.TimeoutMs;
            return "worker-start-failed";
        }
    }

    private void ReadLoop(Process p)
    {
        try
        {
            while (p.StandardOutput.ReadLine() is { } line)
            {
                UiaResponse? resp;
                try
                {
                    resp = JsonSerializer.Deserialize<UiaResponse>(line, HapticsJson.Compact);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (resp is null)
                {
                    continue;
                }

                if (resp.Ready)
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(p, _proc))
                        {
                            _workerReady = true;
                            PumpLocked();
                        }
                    }

                    continue;
                }

                double pointerTs, sentAt;
                lock (_gate)
                {
                    if (!ReferenceEquals(p, _proc) || _outstanding?.Id != resp.Id)
                    {
                        _late++;
                        continue;
                    }

                    pointerTs = _outstanding.Value.PointerTs;
                    sentAt = _outstanding.Value.SentAt;
                    _outstanding = null;
                    _responses++;
                }

                var now = MonotonicClock.Instance.NowMs;
                if (resp.Snapshot is not null)
                {
                    resp.Snapshot = resp.Snapshot with { RequestedAtMs = pointerTs, ReceivedAtMs = now };
                }

                CompleteWaiter(resp.Id, resp);
                Response?.Invoke(resp, pointerTs, now - sentAt);
                lock (_gate)
                {
                    PumpLocked();
                }
            }
        }
        catch
        {
            // Process died; watchdog/next request handles restart.
        }
    }

    private void Watchdog()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_proc is { HasExited: true } && !_workerReady)
            {
                Diagnostic?.Invoke("uia-worker-exited-before-ready");
                KillLocked();
                FailPendingLocked("worker-exited-before-ready");
                _retryAt = MonotonicClock.Instance.NowMs + _opt.TimeoutMs;
                return;
            }

            if (_proc is not null && !_workerReady && MonotonicClock.Instance.NowMs - _workerStartedAt > _opt.StartupTimeoutMs)
            {
                Diagnostic?.Invoke($"uia-worker-start-timeout after {_opt.StartupTimeoutMs}ms; killing worker");
                KillLocked();
                FailPendingLocked("worker-start-timeout");
                _retryAt = MonotonicClock.Instance.NowMs + _opt.TimeoutMs;

                return;
            }

            if (_outstanding is { } o && MonotonicClock.Instance.NowMs - o.SentAt > _opt.TimeoutMs)
            {
                _timeouts++;
                if (o.FgPid > 0)
                {
                    _quarantine[o.FgPid] = MonotonicClock.Instance.NowMs + _opt.QuarantineMs;
                }

                Diagnostic?.Invoke($"uia-timeout id={o.Id} after {_opt.TimeoutMs}ms; killing worker");
                _outstanding = null;
                KillLocked();
                var timeout = new UiaResponse { Id = o.Id, Ok = false, Error = "timeout", ClientStatus = "timeout" };
                CompleteWaiter(o.Id, timeout);
                PumpLocked();
            }
            else if (_outstanding is { } o2 && _proc is { HasExited: true })
            {
                _outstanding = null;
                Diagnostic?.Invoke("uia-worker-exited");
                CompleteWaiter(o2.Id, new UiaResponse { Id = o2.Id, Ok = false, Error = "worker-exited", ClientStatus = "worker-exited" });
                KillLocked();
                PumpLocked();
            }

            if (_retryAt is { } retryAt && MonotonicClock.Instance.NowMs >= retryAt)
            {
                EnsureProcessLocked();
            }

            PumpLocked();
        }
    }

    private void KillLocked()
    {
        _workerReady = false;
        if (_proc is null)
        {
            return;
        }

        try
        {
            if (!_proc.HasExited)
            {
                _proc.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Already gone.
        }

        _proc.Dispose();
        _proc = null;
    }

    private void CompleteWaiter(long id, UiaResponse resp)
    {
        TaskCompletionSource<UiaResponse>? tcs;
        lock (_waiters)
        {
            if (_waiters.Remove(id, out tcs))
            {
                tcs.TrySetResult(resp);
            }
        }
    }
}
