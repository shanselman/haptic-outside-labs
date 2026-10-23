using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Haptics.Windows;

/// <summary>Cheap, read-only desktop context queries.</summary>
public static class DesktopContext
{
    public static readonly int OwnProcessId = Environment.ProcessId;

    public static (int X, int Y) CursorPosition() =>
        Native.GetCursorPos(out var p) ? (p.X, p.Y) : (int.MinValue, int.MinValue);

    public static (IntPtr Hwnd, int Pid) Foreground()
    {
        var h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero)
        {
            return (IntPtr.Zero, 0);
        }

        Native.GetWindowThreadProcessId(h, out var pid);
        return (h, (int)pid);
    }

    /// <summary>
    /// Top-level window and owning PID under a screen point. Note: WindowFromPoint can message the target
    /// window (WM_NCHITTEST), so do not call it on a hot path; use it for experiment provenance checks.
    /// </summary>
    public static (IntPtr Hwnd, int Pid) TopLevelAt(int x, int y)
    {
        var h = Native.WindowFromPoint(new Native.POINT { X = x, Y = y });
        if (h == IntPtr.Zero)
        {
            return (IntPtr.Zero, 0);
        }

        var root = Native.GetAncestor(h, Native.GA_ROOT);
        if (root != IntPtr.Zero)
        {
            h = root;
        }

        Native.GetWindowThreadProcessId(h, out var pid);
        return (h, (int)pid);
    }

    public static string? ProcessName(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }

        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return null;
        }
    }
}

public sealed record RawDeviceInfo(long Handle, string Type, string? VidPid, string NameHash, string Connection);

/// <summary>Enumerates raw-input mice without persisting full device paths (which can embed instance/serial data).</summary>
public static partial class RawInputDevices
{
    public static IReadOnlyList<RawDeviceInfo> EnumerateMice()
    {
        uint count = 0;
        var size = (uint)Marshal.SizeOf<Native.RAWINPUTDEVICELIST>();
        Native.GetRawInputDeviceList(null, ref count, size);
        if (count == 0)
        {
            return [];
        }

        var list = new Native.RAWINPUTDEVICELIST[count];
        var got = Native.GetRawInputDeviceList(list, ref count, size);
        if (got == uint.MaxValue)
        {
            return [];
        }

        return list.Take((int)got).Where(d => d.dwType == Native.RIM_TYPEMOUSE).Select(d => Describe((long)d.hDevice)).ToList();
    }

    public static RawDeviceInfo Describe(long handle)
    {
        var name = DeviceName(handle) ?? "";
        var m = VidPidRegex().Match(name);
        var vidpid = m.Success ? $"VID_{m.Groups[1].Value.ToUpperInvariant()}&PID_{m.Groups[2].Value.ToUpperInvariant()}" : null;
        var connection = name.Contains("BTHLE", StringComparison.OrdinalIgnoreCase) || name.Contains("{00001812", StringComparison.OrdinalIgnoreCase)
            ? "bluetooth-le"
            : name.Contains("BTH", StringComparison.OrdinalIgnoreCase) ? "bluetooth" : name.Contains("USB", StringComparison.OrdinalIgnoreCase) ? "usb" : name.Length == 0 ? "unknown" : "other";
        return new RawDeviceInfo(handle, "mouse", vidpid, Hash(name), connection);
    }

    public static string Hash(string s) =>
        s.Length == 0 ? "none" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("haptics-lab:" + s)))[..12].ToLowerInvariant();

    private static string? DeviceName(long handle)
    {
        uint chars = 0;
        Native.GetRawInputDeviceInfoW((IntPtr)handle, Native.RIDI_DEVICENAME, IntPtr.Zero, ref chars);
        if (chars == 0)
        {
            return null;
        }

        var buf = Marshal.AllocHGlobal((int)chars * 2);
        try
        {
            var r = Native.GetRawInputDeviceInfoW((IntPtr)handle, Native.RIDI_DEVICENAME, buf, ref chars);
            return r == uint.MaxValue ? null : Marshal.PtrToStringUni(buf);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    [GeneratedRegex(@"VID(?:_|&0002|&0001|&02|&01|&)([0-9A-Fa-f]{4}).*?PID(?:_|&)([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPidRegex();
}
