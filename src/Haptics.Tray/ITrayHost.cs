using Haptics.Windows;

namespace Haptics.Tray;

/// <summary>What the settings window needs from the tray, so tests can open it without a tray icon or a running pipeline.</summary>
internal interface ITrayHost
{
    event EventHandler? Changed;

    TraySettings Settings { get; }

    bool Primed { get; }

    /// <summary>Primed device types, e.g. "Mouse + Touchpad".</summary>
    string Devices { get; }

    /// <summary>The app that was in front before ours (what "Exclude" would silence).</summary>
    string? LastApp { get; }

    bool StartWithWindows { get; set; }

    PipelineStats? Stats { get; }

    double? LandingSpeed { get; }

    /// <summary>Saves <see cref="Settings"/> and applies them to the running pipeline.</summary>
    void Apply();

    /// <summary>Captures the controller of the haptic device that just moved over our window.</summary>
    void TryPrime();

    void OpenShowcase();
}
