// Minimal background haptics for a haptic mouse (e.g. Surface Mouse 2nd Edition).
//
// The trick, proven by Haptics Lab EXP-020 (blinded, 6/6 felt vs 0/6 controls):
//   1. InputHapticsManager only hands out a controller to a thread whose *own window* just got input from the device.
//      So: show a window, let the user move the mouse over it once, keep that SimpleHapticsController.
//   2. Listen to the mouse in the background with Raw Input (RIDEV_INPUTSINK): mouse only, passive, nothing blocked.
//   3. Call the captured controller whenever you like, even while another app is in the foreground.
// What does NOT work (EXP-020 R2): calling InputHapticsManager.GetForCurrentThread() on the thread receiving
// background WM_INPUT. That thread never gets a controller.
//
// This demo is a "ratchet": one Click every ~150 px of mouse travel, in any app. Close the window to stop.

using System.Runtime.InteropServices;
using Windows.Devices.Haptics;

ApplicationConfiguration.Initialize();
Application.Run(new DemoForm());

internal sealed class DemoForm : Form
{
    private const double PixelsPerTick = 150;
    private readonly Label _label = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 12f) };
    private readonly RawMouseSink _sink;
    private SimpleHapticsController? _controller;
    private SimpleHapticsControllerFeedback? _click;
    private double _travel;

    public DemoForm()
    {
        Text = "Minimal background haptics";
        ClientSize = new Size(460, 160);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_label);
        _label.Text = "Step 1: wiggle your haptic mouse over this window.";
        _label.MouseMove += (_, _) => CaptureController();
        _sink = new RawMouseSink(OnRawMouse);
    }

    // Step 1: our window just received input from the mouse, so this thread's manager knows the device.
    private void CaptureController()
    {
        if (_controller is not null || !InputHapticsManager.IsSupported())
        {
            return;
        }

        var manager = InputHapticsManager.GetForCurrentThread();
        if (manager.CurrentHapticsControllerDeviceType != HapticDeviceType.Mouse || manager.CurrentHapticsController is not { } c)
        {
            return;
        }

        _controller = c;
        _click = c.SupportedFeedback.FirstOrDefault(f => f.Waveform == KnownSimpleHapticsControllerWaveforms.Click);
        _label.Text = "Captured ✓  Step 2: switch to ANY app and move the mouse.\nYou'll feel a tick every ~150 px. Close this window to stop.";
    }

    // Steps 2 + 3: background raw mouse movement drives the captured controller.
    private void OnRawMouse(int dx, int dy)
    {
        if (_controller is null || _click is null)
        {
            return;
        }

        _travel += Math.Sqrt(((double)dx * dx) + ((double)dy * dy));
        if (_travel >= PixelsPerTick)
        {
            _travel = 0;
            _controller.SendHapticFeedback(_click, 1.0);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _sink.Dispose();
        base.OnFormClosed(e);
    }
}

/// <summary>Message-only window that receives mouse Raw Input even when another app is in the foreground.</summary>
internal sealed class RawMouseSink : NativeWindow, IDisposable
{
    private const int WM_INPUT = 0x00FF;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RID_INPUT = 0x10000003;
    private readonly Action<int, int> _onMove;
    private readonly byte[] _buffer = new byte[128];

    public RawMouseSink(Action<int, int> onMove)
    {
        _onMove = onMove;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE
        var device = new RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x02, Flags = RIDEV_INPUTSINK, Target = Handle }; // mouse only
        if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            throw new System.ComponentModel.Win32Exception();
        }
    }

    protected override unsafe void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT)
        {
            var headerSize = (uint)(8 + (2 * IntPtr.Size)); // RAWINPUTHEADER
            var size = (uint)_buffer.Length;
            fixed (byte* p = _buffer)
            {
                if (GetRawInputData(m.LParam, RID_INPUT, p, ref size, headerSize) != uint.MaxValue && *(uint*)p == 0 /* RIM_TYPEMOUSE */)
                {
                    var mouse = p + headerSize;
                    _onMove(*(int*)(mouse + 12), *(int*)(mouse + 16)); // RAWMOUSE.lLastX / lLastY
                }
            }
        }

        base.WndProc(ref m); // DefWindowProc must still see WM_INPUT.
    }

    public void Dispose() => DestroyHandle();

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern unsafe uint GetRawInputData(IntPtr raw, uint command, byte* data, ref uint size, uint headerSize);
}
