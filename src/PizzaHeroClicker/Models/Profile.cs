using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PizzaHeroClicker.Models;

/// <summary>Burst mode: run N actions at the normal pace, pause, repeat.</summary>
public partial class BurstSettings : ObservableObject
{
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private int _count = 10;
    [ObservableProperty] private int _pauseMs = 1000;
}

public partial class RepeatSettings : ObservableObject
{
    [ObservableProperty] private RepeatMode _mode = RepeatMode.Infinite;
    /// <summary>Action count or loop count, depending on <see cref="Mode"/>.</summary>
    [ObservableProperty] private long _count = 100;
    [ObservableProperty] private long _durationMs = 60_000;
    /// <summary>Local clock time, "HH:mm" or "HH:mm:ss".</summary>
    [ObservableProperty] private string _untilTime = "23:59:00";

    [JsonIgnore]
    public double DurationSeconds
    {
        get => DurationMs / 1000.0;
        set => DurationMs = (long)Math.Max(0, Math.Round(value * 1000));
    }

    partial void OnDurationMsChanged(long value) => OnPropertyChanged(nameof(DurationSeconds));

    public static bool TryParseTime(string? text, out TimeSpan time) =>
        TimeSpan.TryParseExact(text?.Trim(), [@"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss"], CultureInfo.InvariantCulture, out time)
        && time < TimeSpan.FromDays(1);

    /// <summary>The next occurrence of <see cref="UntilTime"/> after <paramref name="now"/>, as epoch milliseconds.</summary>
    public long? ResolveUntilEpochMs(DateTimeOffset now)
    {
        if (!TryParseTime(UntilTime, out var time)) return null;
        var local = now.ToLocalTime();
        var target = new DateTimeOffset(local.Date + time, local.Offset);
        if (target <= local) target = target.AddDays(1);
        return target.ToUnixTimeMilliseconds();
    }
}

public partial class HotkeySettings : ObservableObject
{
    [ObservableProperty] private HotkeyMode _mode = HotkeyMode.Toggle;
    [ObservableProperty] private KeyCombo _toggle = new(0x75);                               // F6
    [ObservableProperty] private KeyCombo _pause = new(0x76);                                // F7
    [ObservableProperty] private KeyCombo _capture = new(0x77);                              // F8
    [ObservableProperty] private KeyCombo _stop = new(0x78);                                 // F9
    [ObservableProperty] private KeyCombo _overlay = new(0x4F, KeyMods.Ctrl | KeyMods.Shift); // Ctrl+Shift+O
    [ObservableProperty] private KeyCombo _record = new(0x52, KeyMods.Ctrl | KeyMods.Shift);  // Ctrl+Shift+R
}

/// <summary>Restricts a profile to one application window.</summary>
public partial class WindowTarget : ObservableObject
{
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _processName = "";
    /// <summary>Run only while the window is focused; pause when it loses focus, resume when it returns.</summary>
    [ObservableProperty] private bool _onlyWhenFocused = true;
    /// <summary>Positions are offsets from the window's client area, so they survive the window moving.</summary>
    [ObservableProperty] private bool _relativeCoordinates;

    public bool HasWindow => !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(ProcessName);

    public string Description => HasWindow ? $"{Title}  [{ProcessName}]" : "No window selected";

    partial void OnTitleChanged(string value) { OnPropertyChanged(nameof(Description)); OnPropertyChanged(nameof(HasWindow)); }
    partial void OnProcessNameChanged(string value) { OnPropertyChanged(nameof(Description)); OnPropertyChanged(nameof(HasWindow)); }
}

public partial class OverlaySettings : ObservableObject
{
    [ObservableProperty] private bool _showStatus;
    /// <summary>Locked overlays ignore the mouse entirely; unlock to drag the overlay somewhere else.</summary>
    [ObservableProperty] private bool _statusClickThrough = true;
    /// <summary>Overlay position in physical pixels. Null = default corner of the primary monitor.</summary>
    [ObservableProperty] private int? _statusX;
    [ObservableProperty] private int? _statusY;
    [ObservableProperty] private bool _showPositions;
}

/// <summary>Everything that defines one saved setup. A game can have several. Serialised to profiles\&lt;game&gt;\&lt;name&gt;.json.</summary>
public partial class Profile : ObservableObject
{
    public const int CurrentSchemaVersion = 1;

    [ObservableProperty] private int _schemaVersion = CurrentSchemaVersion;
    [ObservableProperty] private string _name = "Default";

    /// <summary>
    /// The game this profile is filed under ("" = none). Not written into the file: like the
    /// name, it comes from where the file sits (profiles\&lt;Game&gt;\&lt;Name&gt;.json).
    /// </summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private string _game = "";
    /// <summary>Epoch milliseconds (UTC).</summary>
    [ObservableProperty] private long _createdAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    [ObservableProperty] private long _modifiedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [ObservableProperty] private ObservableCollection<ActionBase> _actions = new();
    [ObservableProperty] private RunOrder _order = RunOrder.Sequential;

    /// <summary>Global delay between actions (the h/m/s/ms fields summed). Minimum 1.</summary>
    [ObservableProperty] private long _intervalMs = 100;
    [ObservableProperty] private bool _randomInterval;
    [ObservableProperty] private long _intervalMinMs = 80;
    [ObservableProperty] private long _intervalMaxMs = 120;

    /// <summary>Random +/- pixel offset applied to every click target.</summary>
    [ObservableProperty] private int _jitterPx;
    /// <summary>Glide the cursor to each target over this many ms. 0 = jump.</summary>
    [ObservableProperty] private int _smoothMoveMs;
    /// <summary>Scales every interval and duration: 2 = twice as fast.</summary>
    [ObservableProperty] private double _speedMultiplier = 1.0;
    [ObservableProperty] private int _startDelayMs = 3000;

    /// <summary>Button and click type used when the action list is empty (plain clicker mode).</summary>
    [ObservableProperty] private ClickButton _defaultButton = ClickButton.Left;
    [ObservableProperty] private ClickKind _defaultKind = ClickKind.Single;

    /// <summary>Emergency stop when the mouse is pushed into a screen corner.</summary>
    [ObservableProperty] private bool _cornerStop = true;

    [ObservableProperty] private BurstSettings _burst = new();
    [ObservableProperty] private RepeatSettings _repeat = new();
    [ObservableProperty] private HotkeySettings _hotkeys = new();
    /// <summary>Global hotkey that switches to this profile from anywhere.</summary>
    [ObservableProperty] private KeyCombo _quickSwitchHotkey;
    [ObservableProperty] private WindowTarget _window = new();
    [ObservableProperty] private OverlaySettings _overlay = new();

    /// <summary>Repairs missing sections and out-of-range values after loading from disk.</summary>
    public void Normalize()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "Default" : Name.Trim();
        Game ??= "";
        Actions ??= new();
        Burst ??= new();
        Repeat ??= new();
        Hotkeys ??= new();
        Window ??= new();
        Overlay ??= new();

        for (int i = Actions.Count - 1; i >= 0; i--)
        {
            if (Actions[i] is null) Actions.RemoveAt(i);
            else Actions[i].Normalize();
        }

        IntervalMs = Math.Max(1, IntervalMs);
        IntervalMinMs = Math.Max(1, IntervalMinMs);
        IntervalMaxMs = Math.Max(IntervalMinMs, IntervalMaxMs);
        JitterPx = Math.Clamp(JitterPx, 0, 500);
        SmoothMoveMs = Math.Clamp(SmoothMoveMs, 0, 60_000);
        SpeedMultiplier = double.IsFinite(SpeedMultiplier) ? Math.Clamp(SpeedMultiplier, 0.1, 20) : 1;
        StartDelayMs = Math.Clamp(StartDelayMs, 0, 3_600_000);
        Burst.Count = Math.Max(1, Burst.Count);
        Burst.PauseMs = Math.Max(0, Burst.PauseMs);
        Repeat.Count = Math.Max(1, Repeat.Count);
        Repeat.DurationMs = Math.Max(1, Repeat.DurationMs);
        Repeat.UntilTime ??= "";
        Window.Title ??= "";
        Window.ProcessName ??= "";
    }
}
