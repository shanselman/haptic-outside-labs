using System.Buffers.Binary;
using System.Collections.Concurrent;
using Haptics.Core;

namespace Haptics.Windows;

public enum RawInputCode
{
    /// <summary>RIM_INPUT: our process was foreground.</summary>
    Foreground = 0,

    /// <summary>RIM_INPUTSINK: delivered while another process was foreground.</summary>
    Background = 1,
}

[Flags]
public enum RawMouseButtons : ushort
{
    None = 0,
    LeftDown = 0x0001,
    LeftUp = 0x0002,
    RightDown = 0x0004,
    RightUp = 0x0008,
    MiddleDown = 0x0010,
    MiddleUp = 0x0020,
    Wheel = 0x0400,
    HWheel = 0x0800,
}

public readonly record struct RawMouseInput(
    double TimestampMs,
    RawInputCode Code,
    long DeviceHandle,
    ushort Flags,
    RawMouseButtons Buttons,
    short WheelDelta,
    int LastX,
    int LastY,
    uint ExtraInformation)
{
    public InputOrigin Origin => DeviceHandle != 0 ? InputOrigin.HardwareRaw : InputOrigin.SyntheticOrUnknown;

    public PointerInputClass InputClass =>
        (ExtraInformation & 0xFFFFFF00) == 0xFF515700
            ? PointerInputClass.Promoted
            : DeviceHandle != 0 ? PointerInputClass.Handle : PointerInputClass.Handleless;
}

public enum MessageWindowKind
{
    /// <summary>HWND_MESSAGE child: never visible, never activatable.</summary>
    MessageOnly,

    /// <summary>Hidden, non-activating tool popup. A distinct variant in case routing cares about real top-levels.</summary>
    HiddenTopLevel,
}

/// <summary>
/// A dedicated thread with its own Win32 window and message loop (SPEC §9.1). Optionally registers
/// mouse-only Raw Input with RIDEV_INPUTSINK (never NOLEGACY/CAPTUREMOUSE). Work posted with
/// <see cref="Post"/> runs on this thread, which is where thread-scoped haptic calls must happen.
/// </summary>
public sealed class MessageWindowThread : IDisposable
{
    private const uint WM_RUN_QUEUE = Native.WM_APP + 1;
    private static readonly unsafe uint HeaderSize = (uint)(sizeof(uint) * 2 + IntPtr.Size * 2);

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private readonly MessageWindowKind _kind;
    private readonly bool _registerRaw;
    private readonly string _className = "HapticsLab_" + Guid.NewGuid().ToString("N");
    private readonly byte[] _buffer = new byte[256];
    private Native.WndProc? _proc;
    private IntPtr _hwnd;
    private Exception? _startError;

    public MessageWindowThread(string name, MessageWindowKind kind, bool registerRawMouse)
    {
        _kind = kind;
        _registerRaw = registerRawMouse;
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>Raised on this thread for every WM_INPUT. Handlers must be fast and never block.</summary>
    public event Action<RawMouseInput>? RawMouse;

    /// <summary>Raised on this thread: (device handle, arrived=true / removed=false).</summary>
    public event Action<long, bool>? DeviceChanged;

    public uint ThreadId { get; private set; }

    public IntPtr Hwnd => _hwnd;

    public bool RawInputRegistered { get; private set; }

    public int RawInputRegistrationError { get; private set; }

    public long RawEventCount { get; private set; }

    public double MaxCallbackMs { get; private set; }

    public MessageWindowThread Start()
    {
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("Message window thread did not start.");
        }

        if (_startError is not null)
        {
            throw new InvalidOperationException("Message window thread failed to start.", _startError);
        }

        return this;
    }

    public void Post(Action action)
    {
        _queue.Enqueue(action);
        Native.PostMessageW(_hwnd, WM_RUN_QUEUE, IntPtr.Zero, IntPtr.Zero);
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            Native.PostMessageW(_hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        _thread.Join(TimeSpan.FromSeconds(3));
        _ready.Dispose();
    }

    private void Run()
    {
        try
        {
            ThreadId = Native.GetCurrentThreadId();
            _proc = WndProc;
            var hInstance = Native.GetModuleHandleW(null);
            var wc = new Native.WNDCLASSEX
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.WNDCLASSEX>(),
                lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = hInstance,
                lpszClassName = _className,
            };
            if (Native.RegisterClassExW(ref wc) == 0)
            {
                throw new System.ComponentModel.Win32Exception();
            }

            _hwnd = _kind == MessageWindowKind.MessageOnly
                ? Native.CreateWindowExW(0, _className, "HapticsLabMessageWindow", 0, 0, 0, 0, 0, Native.HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero)
                : Native.CreateWindowExW(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE, _className, "HapticsLabHiddenWindow", Native.WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                throw new System.ComponentModel.Win32Exception();
            }

            if (_registerRaw)
            {
                var dev = new[]
                {
                    new Native.RAWINPUTDEVICE
                    {
                        usUsagePage = 0x01, // Generic Desktop
                        usUsage = 0x02,     // Mouse (only; no keyboard, ever)
                        dwFlags = Native.RIDEV_INPUTSINK | Native.RIDEV_DEVNOTIFY,
                        hwndTarget = _hwnd,
                    },
                };
                RawInputRegistered = Native.RegisterRawInputDevices(dev, 1, (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.RAWINPUTDEVICE>());
                if (!RawInputRegistered)
                {
                    RawInputRegistrationError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                }
            }
        }
        catch (Exception ex)
        {
            _startError = ex;
            _ready.Set();
            return;
        }

        _ready.Set();
        while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }

        if (RawInputRegistered)
        {
            var remove = new[] { new Native.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = Native.RIDEV_REMOVE, hwndTarget = IntPtr.Zero } };
            Native.RegisterRawInputDevices(remove, 1, (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.RAWINPUTDEVICE>());
            RawInputRegistered = false;
        }

        Native.UnregisterClassW(_className, Native.GetModuleHandleW(null));
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_INPUT:
                var start = MonotonicClock.Instance.NowMs;
                try
                {
                    if (TryRead(lParam, (RawInputCode)((long)wParam & 0xff), start, out var input))
                    {
                        RawEventCount++;
                        RawMouse?.Invoke(input);
                    }
                }
                catch
                {
                    // An observer bug must never break the message loop.
                }

                var elapsed = MonotonicClock.Instance.NowMs - start;
                if (elapsed > MaxCallbackMs)
                {
                    MaxCallbackMs = elapsed;
                }

                // Required so the system can clean up the raw input buffer.
                return Native.DefWindowProcW(hWnd, msg, wParam, lParam);

            case Native.WM_INPUT_DEVICE_CHANGE:
                try
                {
                    DeviceChanged?.Invoke((long)lParam, (long)wParam == 1);
                }
                catch
                {
                    // Ignore observer failures.
                }

                return IntPtr.Zero;

            case WM_RUN_QUEUE:
                while (_queue.TryDequeue(out var action))
                {
                    try
                    {
                        action();
                    }
                    catch
                    {
                        // Posted work reports its own errors.
                    }
                }

                return IntPtr.Zero;

            case Native.WM_CLOSE:
                Native.DestroyWindow(hWnd);
                return IntPtr.Zero;

            case Native.WM_DESTROY:
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private unsafe bool TryRead(IntPtr hRawInput, RawInputCode code, double ts, out RawMouseInput input)
    {
        input = default;
        var size = (uint)_buffer.Length;
        uint read;
        fixed (byte* p = _buffer)
        {
            read = Native.GetRawInputData(hRawInput, Native.RID_INPUT, p, ref size, HeaderSize);
        }

        if (read == uint.MaxValue || read < HeaderSize + 24)
        {
            return false;
        }

        var span = _buffer.AsSpan();
        var type = BinaryPrimitives.ReadUInt32LittleEndian(span);
        if (type != Native.RIM_TYPEMOUSE)
        {
            return false;
        }

        var hDevice = IntPtr.Size == 8 ? BinaryPrimitives.ReadInt64LittleEndian(span[8..]) : BinaryPrimitives.ReadInt32LittleEndian(span[8..]);
        var m = span[(int)HeaderSize..];
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(m);
        var buttonFlags = BinaryPrimitives.ReadUInt16LittleEndian(m[4..]);
        var buttonData = (short)BinaryPrimitives.ReadUInt16LittleEndian(m[6..]);
        var lastX = BinaryPrimitives.ReadInt32LittleEndian(m[12..]);
        var lastY = BinaryPrimitives.ReadInt32LittleEndian(m[16..]);
        var extra = BinaryPrimitives.ReadUInt32LittleEndian(m[20..]);
        var buttons = (RawMouseButtons)buttonFlags;
        var wheel = (buttons & (RawMouseButtons.Wheel | RawMouseButtons.HWheel)) != 0 ? buttonData : (short)0;
        input = new RawMouseInput(ts, code, hDevice, flags, buttons, wheel, lastX, lastY, extra);
        return true;
    }
}
