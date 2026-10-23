using System.Text.Json;
using Haptics.Core;

namespace Haptics.Tray;

/// <summary>Local-only tray settings (%LOCALAPPDATA%\HapticsEverywhere\tray.json). No telemetry, ever.</summary>
public sealed class TraySettings
{
    public bool Enabled { get; set; } = true;
    public double IntensityScale { get; set; } = 0.8;
    public List<string> ExcludedProcesses { get; set; } = new();

    public static string PathOnDisk => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HapticsEverywhere", "tray.json");

    public static TraySettings Load()
    {
        try
        {
            return File.Exists(PathOnDisk) ? JsonSerializer.Deserialize<TraySettings>(File.ReadAllText(PathOnDisk), HapticsJson.Indented) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOnDisk)!);
        File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(this, HapticsJson.Indented));
    }

    /// <summary>
    /// Sample-app policy: every app, hover + boundaries + slider detents, loose rate limits. Password fields and the
    /// built-in exclusion list (credential UIs, password managers) stay silent.
    /// </summary>
    public PolicyConfig ToPolicy() => new()
    {
        Mode = Enabled ? HapticsMode.On : HapticsMode.Off,
        Profile = EffectProfile.Experimental,
        IntensityScale = IntensityScale,
        AppPolicy = AppPolicy.AllExceptExclusions,
        GlobalMaxPulsesPerSecond = 12,
        MaxPulsesPerMinute = 600,
        MinimumGlobalSpacingMs = 40,
        SameElementReentryCooldownMs = 200,
        SemanticIntentExpiryMs = 150,
        HoverMaxPointerSpeedPxPerSec = 1_000_000,
        ExcludedProcesses = new PolicyConfig().ExcludedProcesses.Concat(ExcludedProcesses).ToList(),
    };
}
