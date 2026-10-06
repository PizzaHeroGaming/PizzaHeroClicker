using CommunityToolkit.Mvvm.ComponentModel;

namespace PizzaHeroClicker.Models;

/// <summary>App-wide settings that are not part of any profile. Stored in settings.json.</summary>
public partial class AppSettings : ObservableObject
{
    /// <summary>Profile to load on the next start.</summary>
    [ObservableProperty] private string _lastProfile = "";

    [ObservableProperty] private bool _minimizeToTray = true;
    [ObservableProperty] private bool _startMinimized;

    /// <summary>Spend more CPU for tighter click timing (see TimingEngine).</summary>
    [ObservableProperty] private bool _preciseTiming;

    // Recorder options.
    [ObservableProperty] private bool _recordMouseMoves = true;
    [ObservableProperty] private bool _trimDelays;
    [ObservableProperty] private int _maxDelayMs = 1000;

    /// <summary>Troubleshooting: save what an area watch sees while it runs (about one snapshot a second).</summary>
    [ObservableProperty] private bool _saveAreaSnapshots;

    /// <summary>Ask GitHub once at startup whether a newer version has been released.</summary>
    [ObservableProperty] private bool _checkForUpdates = true;

    public void Normalize()
    {
        LastProfile ??= "";
        MaxDelayMs = Math.Clamp(MaxDelayMs, 0, 3_600_000);
    }
}
