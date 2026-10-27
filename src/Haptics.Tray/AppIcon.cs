using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Media.Imaging;

namespace Haptics.Tray;

internal enum TrayState
{
    Off,
    NeedsPrime,
    Ready,
}

/// <summary>The app icon (assets\app.ico, embedded; regenerate with scripts\make-icon.ps1).</summary>
internal static class AppIcon
{
    private static readonly ColorMatrix Grayscale = new(
    [
        [0.3f, 0.3f, 0.3f, 0, 0],
        [0.59f, 0.59f, 0.59f, 0, 0],
        [0.11f, 0.11f, 0.11f, 0, 0],
        [0, 0, 0, 0.8f, 0],
        [0, 0, 0, 0, 1],
    ]);

    /// <summary>Tray icon at the shell's small-icon size: full color when ready, an amber dot while it needs a wiggle, gray when off.</summary>
    public static Icon ForTray(TrayState state)
    {
        var size = SystemInformation.SmallIconSize.Width;
        using var stream = Open();
        using var source = new Icon(stream, size, size);
        using var frame = source.ToBitmap();
        using var canvas = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var attributes = new ImageAttributes();
            if (state == TrayState.Off)
            {
                attributes.SetColorMatrix(Grayscale);
            }

            g.DrawImage(frame, new Rectangle(0, 0, size, size), 0, 0, frame.Width, frame.Height, GraphicsUnit.Pixel, attributes);
            if (state == TrayState.NeedsPrime)
            {
                var d = size * 0.5f;
                var badge = new RectangleF(size - d, size - d, d, d);
                g.FillEllipse(Brushes.White, badge);
                badge.Inflate(-size * 0.07f, -size * 0.07f);
                using var amber = new SolidBrush(Color.FromArgb(255, 185, 0));
                g.FillEllipse(amber, badge);
            }
        }

        var handle = canvas.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        return icon;
    }

    /// <summary>The smallest frame at least <paramref name="pixels"/> wide (the 256 px frame is a PNG; WPF decodes it).</summary>
    public static BitmapSource Frame(int pixels)
    {
        using var stream = Open();
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return decoder.Frames.Where(f => f.PixelWidth >= pixels).MinBy(f => f.PixelWidth) ?? decoder.Frames.MaxBy(f => f.PixelWidth)!;
    }

    private static Stream Open() => typeof(AppIcon).Assembly.GetManifestResourceStream("app.ico")!;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);
}
