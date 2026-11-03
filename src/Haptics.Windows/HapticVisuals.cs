using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace Haptics.Windows;

/// <summary>How a haptic cue looks on screen. Visual only; it is never evidence that a pulse was felt.</summary>
public sealed record HapticVisualCue(
    string Label,
    string Waveform,
    Color Accent,
    int RingCount,
    double DurationMs,
    bool Inward = false,
    bool Segmented = false)
{
    public static HapticVisualCue ForWaveform(string waveform) => waveform switch
    {
        "Hover" => new("Land", waveform, Color.FromRgb(84, 218, 255), 2, 680),
        "Click" => new("Land · big", waveform, Color.FromRgb(190, 245, 255), 3, 780),
        "Step" => new("Detent", waveform, Color.FromRgb(174, 145, 255), 2, 560, Segmented: true),
        "Collide" => new("Edge", waveform, Color.FromRgb(255, 184, 96), 3, 620, Inward: true),
        "Align" => new("Align", waveform, Color.FromRgb(104, 232, 189), 3, 720),
        "Success" => new("Success", waveform, Color.FromRgb(116, 235, 143), 3, 720),
        "Error" => new("Error", waveform, Color.FromRgb(255, 116, 138), 2, 620, Inward: true),
        _ => new(waveform, waveform, Color.FromRgb(119, 190, 255), 2, 650),
    };
}

public static class HapticVisualRenderer
{
    public static void Render(Canvas canvas, HapticVisualCue cue, Point center, double diameter, bool showLabel = false)
    {
        canvas.Children.Clear();
        diameter = Math.Max(1, diameter);
        var ringDiameter = diameter * 0.78;
        var duration = TimeSpan.FromMilliseconds(cue.DurationMs);

        for (var i = 0; i < cue.RingCount; i++)
        {
            var ring = new Ellipse
            {
                Width = ringDiameter,
                Height = ringDiameter,
                Stroke = new SolidColorBrush(cue.Accent),
                StrokeThickness = i == 0 ? 3 : 1.5,
                Opacity = 0.9,
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            var scale = new ScaleTransform();
            if (cue.Segmented)
            {
                ring.StrokeDashArray = [0.18, 0.52];
                ring.StrokeDashCap = PenLineCap.Round;
                var transforms = new TransformGroup();
                transforms.Children.Add(new RotateTransform(i * 35));
                transforms.Children.Add(scale);
                ring.RenderTransform = transforms;
            }
            else
            {
                ring.RenderTransform = scale;
            }

            canvas.Children.Add(ring);
            Canvas.SetLeft(ring, center.X - ringDiameter / 2);
            Canvas.SetTop(ring, center.Y - ringDiameter / 2);

            var delay = TimeSpan.FromMilliseconds(i * (cue.Segmented ? 90 : 110));
            var from = cue.Inward ? 1.0 + i * 0.08 : 0.08;
            var to = cue.Inward ? 0.12 : cue.Segmented ? 0.92 : 1.08;
            var animation = new DoubleAnimation(from, to, new Duration(duration))
            {
                BeginTime = delay,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
            ring.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.82, 0, new Duration(duration))
            {
                BeginTime = delay,
            });
        }

        if (showLabel)
        {
            var label = new TextBlock
            {
                Text = cue.Label.ToUpperInvariant(),
                Foreground = new SolidColorBrush(cue.Accent),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                Width = diameter,
                IsHitTestVisible = false,
            };
            canvas.Children.Add(label);
            Canvas.SetLeft(label, center.X - diameter / 2);
            Canvas.SetTop(label, center.Y + diameter * 0.30);
        }
    }
}

/// <summary>A transient, non-activating overlay whose transparent hit test leaves the app below interactive.</summary>
public sealed class CursorRippleOverlay : Window, IDisposable
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly Canvas _canvas = new() { Width = 200, Height = 200, IsHitTestVisible = false };
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(950) };
    private bool _ready;
    private bool _closed;

    public CursorRippleOverlay()
    {
        Width = 200;
        Height = 200;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        IsHitTestVisible = false;
        Opacity = 0;
        Content = _canvas;
        SourceInitialized += (_, _) => ConfigureClickThrough();
        Closed += (_, _) => _closed = true;
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
    }

    internal bool IsConfiguredClickThrough => _ready;

    public void Play(HapticVisualCue cue, int screenX, int screenY)
    {
        if (!IsVisible)
        {
            Show();
        }

        if (!_ready)
        {
            throw new InvalidOperationException("The desktop ripple could not be made click-through.");
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Round(Width * dpi.DpiScaleX);
        var height = (int)Math.Round(Height * dpi.DpiScaleY);
        Marshal.SetLastPInvokeError(0);
        if (!SetWindowPos(hwnd, HwndTopmost, screenX - width / 2, screenY - height / 2, 0, 0,
                SwpNoSize | SwpNoActivate | SwpShowWindow))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        HapticVisualRenderer.Render(_canvas, cue, new Point(_canvas.Width / 2, _canvas.Height / 2),
            Math.Min(_canvas.Width, _canvas.Height), showLabel: true);
        Opacity = 1;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    public void Dispose()
    {
        _hideTimer.Stop();
        if (!_closed)
        {
            Close(); // Also when hidden: WPF keeps every unclosed window alive.
        }
    }

    private void ConfigureClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Marshal.SetLastPInvokeError(0);
        var style = GetWindowLongPtr(hwnd, GwlExStyle);
        var readError = Marshal.GetLastWin32Error();
        if (style == IntPtr.Zero && readError != 0)
        {
            throw new Win32Exception(readError);
        }

        Marshal.SetLastPInvokeError(0);
        var previous = SetWindowLongPtr(hwnd, GwlExStyle,
            new IntPtr(style.ToInt64() | WsExTransparent | WsExToolWindow | WsExNoActivate));
        var writeError = Marshal.GetLastWin32Error();
        if (previous == IntPtr.Zero && writeError != 0)
        {
            throw new Win32Exception(writeError);
        }

        _ready = true;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
