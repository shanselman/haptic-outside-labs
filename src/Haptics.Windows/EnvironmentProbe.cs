using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Haptics.Core;
using Microsoft.Win32;

namespace Haptics.Windows;

public static class EnvironmentProbe
{
    public static MachineInfo Capture()
    {
        var info = new MachineInfo
        {
            OsDescription = RuntimeInformation.OSDescription,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            DotnetRuntime = RuntimeInformation.FrameworkDescription,
            WindowsSdkProjection = ProjectionVersion(),
            SessionType = Environment.UserInteractive ? "interactive" : "non-interactive",
        };

        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var build = k?.GetValue("CurrentBuild") as string;
            var ubr = k?.GetValue("UBR");
            info.OsBuild = build is null ? Environment.OSVersion.Version.ToString() : $"{build}.{ubr}";
            info.OsDisplayVersion = k?.GetValue("DisplayVersion") as string;
        }
        catch
        {
            info.OsBuild = Environment.OSVersion.Version.ToString();
        }

        try
        {
            using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            info.DeviceModel = bios?.GetValue("SystemProductName") as string;
        }
        catch
        {
            // Model is optional context.
        }

        info.MachineFingerprint = Fingerprint(info.OsBuild);
        return info;
    }

    /// <summary>Stable per machine+OS build, but not reversible to the machine name.</summary>
    public static string Fingerprint(string osBuild) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"haptics-lab|{Environment.MachineName}|{osBuild}")))[..16].ToLowerInvariant();

    public static string CurrentFingerprint() => Capture().MachineFingerprint;

    public static string CodeCommit()
    {
        var v = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(EnvironmentProbe).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var plus = v?.IndexOf('+') ?? -1;
        return plus >= 0 ? v![(plus + 1)..] : "unknown";
    }

    private static string? ProjectionVersion()
    {
        try
        {
            var asm = typeof(global::Windows.Devices.Haptics.SimpleHapticsController).Assembly;
            return asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? asm.GetName().Version?.ToString();
        }
        catch
        {
            return null;
        }
    }
}
