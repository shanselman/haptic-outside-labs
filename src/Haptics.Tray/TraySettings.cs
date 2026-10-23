using System.Text.Json;
using Haptics.Core;

namespace Haptics.Tray;

/// <summary>Local-only tray settings (%LOCALAPPDATA%\HapticsEverywhere\tray.json). No telemetry, ever.</summary>
public sealed class TraySettings
{
    public bool ConsentGiven { get; set; }
    public EffectProfile Profile { get; set; } = EffectProfile.Balanced;
    public double IntensityScale { get; set; } = 0.25;
    public AppPolicy AppPolicy { get; set; } = AppPolicy.OptInAllowlist;
    public List<string> AllowedProcesses { get; set; } = new();
    public List<string> ExtraExcludedProcesses { get; set; } = new();

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

    public PolicyConfig ToPolicy(HapticsMode mode)
    {
        var p = new PolicyConfig { Mode = mode, IntensityScale = IntensityScale, AppPolicy = AppPolicy, AllowedProcesses = AllowedProcesses.ToList() };
        p.ApplyProfile(Profile);
        p.ExcludedProcesses.AddRange(ExtraExcludedProcesses);
        return p;
    }
}
