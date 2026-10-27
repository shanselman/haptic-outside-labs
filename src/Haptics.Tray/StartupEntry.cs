using Microsoft.Win32;

namespace Haptics.Tray;

/// <summary>"Start with Windows": a per-user Run entry pointing at this exe. The registry is the only source of truth.</summary>
internal static class StartupEntry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HapticsEverywhere";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    /// <summary>On only if the entry launches this exe (a moved or older copy reads as off; turning it on fixes it).</summary>
    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
            {
                key.SetValue(ValueName, Command);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
    }
}
