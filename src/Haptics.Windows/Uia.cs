using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public bool Focused { get; set; }
    public bool ObserveCaptions { get; set; } = true;
}

public sealed class UiaResponse
{
    /// <summary>Client-only interaction token; never sent to or trusted from the worker.</summary>
    [JsonIgnore]
    public long Generation { get; set; }

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
        var cache = BuildCacheRequest(includeAutomationId: false);
        CacheRequest? fixtureCache = null;
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
                    resp = Query(req, req.AutomationId ? fixtureCache ??= BuildCacheRequest(includeAutomationId: true) : cache);
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

    private static CacheRequest BuildCacheRequest(bool includeAutomationId)
    {
        var c = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
        foreach (var p in new AutomationProperty[]
                 {
                     AutomationElement.ControlTypeProperty, AutomationElement.BoundingRectangleProperty,
                     AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty, AutomationElement.IsPasswordProperty,
                     AutomationElement.ProcessIdProperty, AutomationElement.NativeWindowHandleProperty, AutomationElement.FrameworkIdProperty,
                     AutomationElement.ClassNameProperty,
                     AutomationElement.IsControlElementProperty, AutomationElement.RuntimeIdProperty,
                     AutomationElement.IsSelectionItemPatternAvailableProperty, SelectionItemPattern.IsSelectedProperty,
                     AutomationElement.IsTogglePatternAvailableProperty, TogglePatternIdentifiers.ToggleStateProperty,
                     AutomationElement.IsExpandCollapsePatternAvailableProperty,
                 })
        {
            c.Add(p);
        }

        if (includeAutomationId)
        {
            c.Add(AutomationElement.AutomationIdProperty);
        }

        c.Add(SelectionItemPattern.Pattern);
        return c;
    }

    private static CacheRequest BuildRangeCacheRequest()
    {
        var c = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.None };
        foreach (var p in new[]
                 {
                     AutomationElement.IsRangeValuePatternAvailableProperty,
                     RangeValuePattern.ValueProperty, RangeValuePattern.MinimumProperty, RangeValuePattern.MaximumProperty,
                     RangeValuePattern.SmallChangeProperty, RangeValuePattern.LargeChangeProperty,
                 })
        {
            c.Add(p);
        }

        return c;
    }

    private static UiaResponse Query(UiaRequest req, CacheRequest cache)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Exclude shell hosts before any UIA call; never enumerate taskbar descendants.
            {
                var root = Native.GetAncestor(Native.WindowFromPoint(new Native.POINT { X = req.X, Y = req.Y }), Native.GA_ROOT);
                if (root != IntPtr.Zero)
                {
                    var rootClass = new StringBuilder(256);
                    if (Native.GetClassNameW(root, rootClass, rootClass.Capacity) == 0)
                    {
                        return Fail(req, sw, $"window-class-error:{System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
                    }

                    if (SkipHoverObservation(req, rootClass.ToString()))
                    {
                        return new UiaResponse
                        {
                            Id = req.Id, Ok = true, LookupMs = sw.Elapsed.TotalMilliseconds,
                            Snapshot = new ElementSnapshot
                            {
                                Source = "hover-observation-disabled", ControlType = "Pane",
                                QueryX = req.X, QueryY = req.Y, IsEnabled = false, IsOffscreen = true,
                            },
                        };
                    }
                }
            }

            AutomationElement el;
            AutomationElement? parent = null;
            AutomationElement? ownerPart = null;
            RectD? sliderThumbBounds = null;
            var headerOwner = false;
            using (cache.Activate())
            {
                el = AutomationElement.FromPoint(new System.Windows.Point(req.X, req.Y));
                var type = TypeName(el);
                AutomationElement? partOf = type is "Thumb" or "Button" or "Text" or "Image" ? Parent(el, cache) : null;
                if (partOf is not null && type is "Text" or "Image" &&
                    (TypeName(partOf) is "TabItem" or "MenuItem" or "Button" or "Hyperlink" ||
                     (TypeName(partOf) == "CheckBox" &&
                      Get<bool>(partOf, AutomationElement.IsTogglePatternAvailableProperty))))
                {
                    // Labels/icons share their owner's identity and dwell. Do not read a second parent.
                    ownerPart = el;
                    el = partOf;
                    type = TypeName(el);
                    headerOwner = type is "TabItem" or "MenuItem";
                }
                else if (partOf is not null && type is "Thumb" or "Button" &&
                         (TypeName(partOf) == "Slider" || (req.Focused && TypeName(partOf) == "ComboBox")))
                {
                    // A slider thumb/track or dropdown arrow belongs to its owning control.
                    ownerPart = el;
                    if (TypeName(partOf) == "Slider" && type == "Thumb")
                    {
                        sliderThumbBounds = ToRect(Get<System.Windows.Rect>(el, AutomationElement.BoundingRectangleProperty));
                    }
                    el = partOf;
                    type = TypeName(partOf);
                    partOf = null;
                }
                if (ownerPart is null && (SemanticVocabulary.InteractiveTypes.Contains(type) || type == "ListItem"))
                {
                    parent = partOf ?? Parent(el, cache);
                }
            }

            var snap = ToSnapshot(el, parent, req) with { SliderThumbBounds = sliderThumbBounds };
            if (ownerPart is not null)
            {
                var normalized = NormalizeOwnerPart(snap,
                    Get<int>(ownerPart, AutomationElement.ProcessIdProperty),
                    Get<bool>(ownerPart, AutomationElement.IsEnabledProperty),
                    Get<bool>(ownerPart, AutomationElement.IsOffscreenProperty),
                    Get<bool>(ownerPart, AutomationElement.IsPasswordProperty));
                if (normalized is null)
                {
                    return Fail(req, sw, headerOwner ? "header-owner-process-mismatch" :
                        snap.ControlType == "Slider" ? "slider-owner-process-mismatch" : "control-owner-process-mismatch");
                }
                snap = normalized with
                {
                    IsHeaderOwner = headerOwner,
                    IsButtonOwner = snap.ControlType == "Button",
                };
            }
            if (req.Focused)
            {
                var window = Native.WindowFromPoint(new Native.POINT { X = req.X, Y = req.Y });
                var root = Native.GetAncestor(window, Native.GA_ROOT);
                if (root != IntPtr.Zero)
                {
                    var className = new StringBuilder(256);
                    if (Native.GetClassNameW(root, className, className.Capacity) == 0)
                    {
                        return Fail(req, sw, $"window-class-error:{System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
                    }

                    {
                        snap = snap with { IsBrowserToolbarCommand = IsBrowserToolbarCommand(className.ToString(), snap) };
                        if (!req.ObserveCaptions)
                        {
                            return new UiaResponse { Id = req.Id, Ok = true, Snapshot = snap with { LookupMs = sw.Elapsed.TotalMilliseconds }, LookupMs = sw.Elapsed.TotalMilliseconds };
                        }

                        var hostedCaption = false;
                        if (className.ToString() == "GitHubCopilotCaptionHost")
                        {
                            var owner = Native.GetAncestor(root, Native.GA_ROOTOWNER);
                            var ownerClass = new StringBuilder(256);
                            if (owner != IntPtr.Zero && owner != root &&
                                Native.GetClassNameW(owner, ownerClass, ownerClass.Capacity) > 0)
                            {
                                Native.GetWindowThreadProcessId(root, out var hostPid);
                                Native.GetWindowThreadProcessId(owner, out var ownerPid);
                                if (CaptionOwnerAllowed(className.ToString(), ownerClass.ToString(), hostPid, ownerPid))
                                {
                                    root = owner;
                                    className = ownerClass;
                                    hostedCaption = true;
                                }
                            }
                        }

                        snap = snap with { IsBrowserToolbarCommand = IsBrowserToolbarCommand(className.ToString(), snap) };
                        if (!Native.GetWindowRect(root, out var windowBounds))
                        {
                            return Fail(req, sw, $"window-bounds-unavailable:{System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
                        }

                        var captionRegion = Native.DwmGetWindowAttribute(root, 5 /* DWMWA_CAPTION_BUTTON_BOUNDS */,
                            out var captionBounds, (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.RECT>()) == 0
                            ? CaptionSearchRegion(windowBounds, captionBounds) : null;
                        if (NeedsCaptionQuery(snap, captionRegion, hostedCaption))
                        {
                            // WM_NCHITTEST only asks which non-client part contains the point; it never activates it.
                            if (req.X is < short.MinValue or > short.MaxValue || req.Y is < short.MinValue or > short.MaxValue)
                            {
                                return Fail(req, sw, "caption-hit-test-coordinate-range");
                            }

                            var point = new IntPtr(unchecked((int)((uint)(ushort)req.X | ((uint)(ushort)req.Y << 16))));
                            if (Native.SendMessageTimeoutW(root, Native.WM_NCHITTEST, UIntPtr.Zero, point,
                                0x0002 /* SMTO_ABORTIFHUNG */, 15, out var hit) == IntPtr.Zero)
                            {
                                return Fail(req, sw, $"caption-hit-test-unavailable:{System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
                            }

                            var caption = CaptionButtonFromHitTest(unchecked((int)hit.ToUInt64()));
                            var info = new Native.TITLEBARINFOEX
                            {
                                Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.TITLEBARINFOEX>(),
                                State = new uint[6],
                                Bounds = new Native.RECT[6],
                            };
                            if (Native.ReadTitleBarInfo(root, Native.WM_GETTITLEBARINFOEX, UIntPtr.Zero, ref info,
                                0x0002, 15, out _) == IntPtr.Zero)
                            {
                                return Fail(req, sw, $"caption-info-unavailable:{System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
                            }

                            caption = caption != CaptionButton.None ? caption : CaptionButtonFromInfo(info, req.X, req.Y);
                            if (caption != CaptionButton.None)
                            {
                                Native.GetWindowThreadProcessId(root, out var rootPid);
                                if (rootPid == 0)
                                {
                                    return Fail(req, sw, "caption-process-unavailable");
                                }

                                snap = NativeCaptionSnapshot(snap, root.ToInt64(), (int)rootPid, className.ToString(), caption, info,
                                    Native.IsWindowEnabled(root), Native.IsWindowVisible(root));
                            }
                        }
                    }
                }
            }

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

    internal static bool SkipHoverObservation(UiaRequest request, string rootClass) =>
        rootClass is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" ||
        (!request.ObserveCaptions && rootClass == "GitHubCopilotCaptionHost");

    internal static ElementSnapshot? NormalizeOwnerPart(ElementSnapshot owner, int partPid,
        bool partEnabled, bool partOffscreen, bool partPassword) =>
        partPid != owner.ProcessId ? null : owner with
        {
            IsEnabled = owner.IsEnabled && partEnabled,
            IsOffscreen = owner.IsOffscreen || partOffscreen,
            IsPassword = owner.IsPassword || partPassword,
        };

    internal static bool CaptionOwnerAllowed(string hostClass, string ownerClass, uint hostPid, uint ownerPid) =>
        hostClass == "GitHubCopilotCaptionHost" && ownerClass == "Tauri Window" && hostPid != 0 && hostPid == ownerPid;

    internal static RectD? CaptionSearchRegion(Native.RECT window, Native.RECT buttons)
    {
        var width = (double)buttons.Right - buttons.Left;
        var height = (double)buttons.Bottom - buttons.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        // DWM bounds can omit resize-border padding. This is only a search region, never a Close hit box.
        return new RectD((double)window.Left + buttons.Left - height, (double)window.Top + buttons.Top - height,
            width + (2 * height), 3 * height);
    }

    internal static bool NeedsCaptionQuery(ElementSnapshot s, RectD? region, bool hostedCaption) =>
        hostedCaption || s.ControlType == "TitleBar" || s.ParentControlType == "TitleBar" ||
        region?.Contains(s.QueryX, s.QueryY) == true;

    internal static bool IsBrowserToolbarCommand(string rootClass, ElementSnapshot s) =>
        rootClass == "Chrome_WidgetWin_1" && s.ControlType == "Button" &&
        (s.ParentControlType is "Pane" or "ToolBar" || s.IsButtonOwner) &&
        s.ClassName is "PinnedActionToolbarButton" or "PageActionView" or "ExtensionsToolbarButton" or
            "AvatarToolbarButton" or "BrowserAppMenuButton";

    internal static CaptionButton CaptionButtonFromHitTest(int hit) => hit switch
    {
        20 => CaptionButton.Close,
        8 => CaptionButton.Minimize,
        9 => CaptionButton.Maximize,
        _ => CaptionButton.None,
    };

    internal static ElementSnapshot NativeCaptionSnapshot(ElementSnapshot observed, long hwnd, int pid, string className,
        CaptionButton caption, Native.TITLEBARINFOEX info, bool windowEnabled, bool windowVisible)
    {
        var index = caption switch { CaptionButton.Close => 5, CaptionButton.Minimize => 2, CaptionButton.Maximize => 3, _ => throw new ArgumentOutOfRangeException(nameof(caption)) };
        var native = info.Bounds[index];
        var rect = new RectD(native.Left, native.Top, native.Right - native.Left, native.Bottom - native.Top);
        var bounds = !rect.IsEmpty && rect.Contains(observed.QueryX, observed.QueryY) ? rect :
            observed.ProcessId == pid &&
            observed is { ControlType: "Button", ParentControlType: "TitleBar", IsOffscreen: false, Bounds: { IsEmpty: false } uia } &&
            uia.Contains(observed.QueryX, observed.QueryY) ? uia : null;
        return new ElementSnapshot
        {
            Source = "native-caption", ProcessId = pid, Hwnd = hwnd, ControlType = "Button", ClassName = className,
            RuntimeIdHash = HashRuntimeId([unchecked((int)hwnd), unchecked((int)(hwnd >> 32)), (int)caption]),
            CaptionButton = caption, ParentControlType = "TitleBar", Bounds = bounds, Scale = observed.Scale,
            IsEnabled = windowEnabled && observed.IsEnabled && (info.State[index] & 0x0001) == 0,
            IsOffscreen = !windowVisible || (info.State[index] & (0x8000 | 0x10000)) != 0,
            IsPassword = observed.IsPassword, QueryX = observed.QueryX, QueryY = observed.QueryY,
        };
    }

    internal static bool? ToggleOn(object state) => state switch
    {
        ToggleState.Off => false,
        ToggleState.On => true,
        _ => null,
    };

    internal static CaptionButton CaptionButtonFromInfo(Native.TITLEBARINFOEX info, int x, int y)
    {
        const uint unavailable = 0x0001 | 0x8000 | 0x10000; // Unavailable, invisible, or offscreen.
        foreach (var (index, kind) in new[] { (5, CaptionButton.Close), (2, CaptionButton.Minimize), (3, CaptionButton.Maximize) })
        {
            var bounds = info.Bounds[index];
            if ((info.State[index] & unavailable) == 0 && x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom)
            {
                return kind;
            }
        }

        return CaptionButton.None;
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
        if (TypeName(el) == "Slider")
        {
            var slider = el.GetUpdatedCache(BuildRangeCacheRequest());
            if (Get<bool>(slider, AutomationElement.IsRangeValuePatternAvailableProperty))
            {
                range = new RangeInfo(
                    Get<double>(slider, RangeValuePattern.ValueProperty),
                    Get<double>(slider, RangeValuePattern.MinimumProperty),
                    Get<double>(slider, RangeValuePattern.MaximumProperty),
                    Get<double>(slider, RangeValuePattern.SmallChangeProperty),
                    Get<double>(slider, RangeValuePattern.LargeChangeProperty));
            }
        }

        return new ElementSnapshot
        {
            Source = "uia",
            ProcessId = Get<int>(el, AutomationElement.ProcessIdProperty),
            Hwnd = Get<int>(el, AutomationElement.NativeWindowHandleProperty),
            ControlType = TypeName(el),
            ClassName = Get<string>(el, AutomationElement.ClassNameProperty),
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
            IsToggle = Get<bool>(el, AutomationElement.IsTogglePatternAvailableProperty),
            IsToggledOn = Get<bool>(el, AutomationElement.IsTogglePatternAvailableProperty)
                ? ToggleOn(el.GetCachedPropertyValue(TogglePatternIdentifiers.ToggleStateProperty, ignoreDefaultValue: true))
                : null,
            HasDropdown = Get<bool>(el, AutomationElement.IsExpandCollapsePatternAvailableProperty),
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
    private readonly Dictionary<long, (TaskCompletionSource<UiaResponse> Completion, long Generation)> _waiters = new();
    private readonly Dictionary<int, double> _quarantine = new();
    private readonly Queue<double> _restarts = new();
    private Process? _proc;
    private long _nextId;
    private (long Id, double SentAt, double PointerTs, int FgPid, int X, int Y, long Generation)? _outstanding;
    private (long Id, string Cmd, int X, int Y, double PointerTs, int FgPid, long Generation, bool Focused)? _pending;
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

    internal Func<bool> ObserveCaptions { get; set; } = static () => true;

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
    public void RequestLatest(int x, int y, double pointerTs, int foregroundPid, long generation = 0, bool focused = false)
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
                CompleteWaiter(old.Id, new UiaResponse { Id = old.Id, ClientStatus = "superseded", Generation = old.Generation });
            }

            _pending = (++_nextId, "query", x, y, pointerTs, foregroundPid, generation, focused);
            _requests++;
            PumpLocked();
        }
    }

    /// <summary>Sequential query for experiments. Completes with a ClientStatus on timeout/supersede.</summary>
    public Task<UiaResponse> QueryAsync(int x, int y, string cmd = "query", long generation = 0, bool focused = false)
    {
        var tcs = new TaskCompletionSource<UiaResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_disposed)
            {
                tcs.SetResult(new UiaResponse { ClientStatus = "disposed", Error = "disposed", Generation = generation });
                return tcs.Task;
            }

            if (_pending is { } old)
            {
                _superseded++;
                CompleteWaiter(old.Id, new UiaResponse { Id = old.Id, ClientStatus = "superseded", Generation = old.Generation });
            }

            var id = ++_nextId;
            lock (_waiters)
            {
                _waiters[id] = (tcs, generation);
            }

            _pending = (id, cmd, x, y, MonotonicClock.Instance.NowMs, 0, generation, focused);
            _requests++;
            PumpLocked();
        }

        return tcs.Task;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            KillLocked();
            lock (_waiters)
            {
                foreach (var w in _waiters.Values)
                {
                    w.Completion.TrySetResult(new UiaResponse { ClientStatus = "disposed", Generation = w.Generation });
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

        var now = MonotonicClock.Instance.NowMs;
        _pending = null;
        var req = new UiaRequest
        {
            Id = next.Id, Cmd = next.Cmd, X = next.X, Y = next.Y, AutomationId = _opt.IncludeAutomationId, Focused = next.Focused,
            ObserveCaptions = ObserveCaptions(),
        };
        try
        {
            _outstanding = (next.Id, now, next.PointerTs, next.FgPid, next.X, next.Y, next.Generation);
            _proc!.StandardInput.WriteLine(JsonSerializer.Serialize(req, HapticsJson.Compact));
            _proc.StandardInput.Flush();
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"uia-write-failed: {ex.Message}");
            _outstanding = null;
            CompleteWaiter(next.Id, new UiaResponse { Id = next.Id, ClientStatus = "worker-write-failed", Generation = next.Generation });
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
        CompleteWaiter(pending.Id, new UiaResponse { Id = pending.Id, Error = status, ClientStatus = status, Generation = pending.Generation });
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
                    resp.Generation = _outstanding.Value.Generation;
                    _outstanding = null;
                    _responses++;
                }

                var now = MonotonicClock.Instance.NowMs;
                if (resp.Snapshot is not null)
                {
                    resp.Snapshot = resp.Snapshot with { RequestedAtMs = pointerTs, ReceivedAtMs = now, Generation = resp.Generation };
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
                var timeout = new UiaResponse { Id = o.Id, Ok = false, Error = "timeout", ClientStatus = "timeout", Generation = o.Generation };
                CompleteWaiter(o.Id, timeout);
                PumpLocked();
            }
            else if (_outstanding is { } o2 && _proc is { HasExited: true })
            {
                _outstanding = null;
                Diagnostic?.Invoke("uia-worker-exited");
                CompleteWaiter(o2.Id, new UiaResponse { Id = o2.Id, Ok = false, Error = "worker-exited", ClientStatus = "worker-exited", Generation = o2.Generation });
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
        lock (_waiters)
        {
            if (_waiters.Remove(id, out var waiter))
            {
                waiter.Completion.TrySetResult(resp);
            }
        }
    }
}
