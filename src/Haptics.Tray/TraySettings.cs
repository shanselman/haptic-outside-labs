using System.Text.Json;
using Haptics.Core;

namespace Haptics.Tray;

/// <summary>Local-only tray settings (%LOCALAPPDATA%\HapticsEverywhere\tray.json). No telemetry, ever.</summary>
public sealed class TraySettings
{
    public bool Enabled { get; set; } = true;
    public EffectProfile Style { get; set; } = EffectProfile.Focused;
    public double IntensityScale { get; set; } = 0.8;

    /// <summary>Touchpads feel lighter than mice at the same intensity.</summary>
    public double TouchpadBoost { get; set; } = 1.3;

    /// <summary>Let other apps (same user) send haptics over the local pipe — see README "Cooperating apps".</summary>
    public bool CooperationEnabled { get; set; } = true;

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
    /// Sample-app policy: every app, the chosen style, loose global limits. Password fields and the built-in exclusion
    /// list (credential UIs, password managers) stay silent.
    /// </summary>
    public PolicyConfig ToPolicy()
    {
        var p = PolicyConfig.Sample(Style, IntensityScale);
        p.Mode = Enabled ? HapticsMode.On : HapticsMode.Off;
        p.CooperativeEnabled = CooperationEnabled;
        p.ExcludedProcesses.AddRange(ExcludedProcesses);
        return p;
    }
}
