using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PizzaHeroClicker.Controls;
using PizzaHeroClicker.Engine;
using PizzaHeroClicker.Models;
using PizzaHeroClicker.Services;

namespace PizzaHeroClicker.ViewModels;

/// <summary>
/// The main window's view model: run control, live status, validation, and the currently
/// loaded profile. Everything here runs on the UI thread; engine events are marshalled in.
/// Split across partial files by feature area.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    public const string HkToggle = "toggle";
    public const string HkPause = "pause";
    public const string HkCapture = "capture";
    public const string HkStop = "stop";

    private readonly AppServices _s;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _statusTimer;
    private readonly Queue<(long Ticks, long Clicks)> _cpsSamples = new();
    private bool _loading;
    private string _hotkeyDuplicateError = "";

    [ObservableProperty] private Profile _profile = new();

    /// <summary>Bumped on every add / remove / move in the action list, so row numbers refresh.</summary>
    [ObservableProperty] private int _actionListVersion;

    // Interval, split into the four input fields. Their sum is Profile.IntervalMs.
    [ObservableProperty] private int _intervalHours;
    [ObservableProperty] private int _intervalMinutes;
    [ObservableProperty] private int _intervalSeconds;
    [ObservableProperty] private int _intervalMillis;
    [ObservableProperty] private string _intervalSummary = "";

    // Live status.
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _stateKind = "Stopped";
    [ObservableProperty] private string _stateText = "STOPPED";
    [ObservableProperty] private string _toggleText = "START";
    [ObservableProperty] private string _pauseText = "PAUSE";
    [ObservableProperty] private long _clickCount;
    [ObservableProperty] private long _actionCount;
    [ObservableProperty] private long _sessionClicks;
    [ObservableProperty] private string _cpsText = "0.0";
    [ObservableProperty] private string _nextText = "–";
    [ObservableProperty] private string _runTimeText = "0:00";

    [ObservableProperty] private string _footer = "Ready.";
    [ObservableProperty] private string _validationMessage = "";
    [ObservableProperty] private string _hotkeyWarning = "";

    public MainViewModel(AppServices services)
    {
        _s = services;
        _s.DialogHost.CaptureKeyName = () => Profile.Hotkeys.Capture.IsEmpty ? null : Profile.Hotkeys.Capture.ToString();
        _s.DialogHost.CoordinateOrigin = CoordinateOrigin;

        _s.Engine.Stopped += (reason, error) => _ui.BeginInvoke(() => OnEngineStopped(reason, error));
        _s.Hotkeys.Pressed += OnHotkeyPressed;
        _s.Hotkeys.Released += OnHotkeyReleased;
        HotkeyBox.CaptureActiveChanged += OnHotkeyCaptureChanged;

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        InitializeWindowFeatures();
        InitializeProfiles();
        Revalidate();
        RefreshStatus();
    }

    // ------------------------------------------------------------------ profile wiring

    partial void OnProfileChanged(Profile? oldValue, Profile newValue)
    {
        if (oldValue is not null) DetachProfile(oldValue);
        AttachProfile(newValue);
    }

    private void AttachProfile(Profile p)
    {
        p.PropertyChanged += OnProfileEdited;
        p.Hotkeys.PropertyChanged += OnProfileEdited;
        p.Repeat.PropertyChanged += OnProfileEdited;
        p.Burst.PropertyChanged += OnProfileEdited;
        p.Window.PropertyChanged += OnProfileEdited;
        p.Overlay.PropertyChanged += OnProfileEdited;
        p.Actions.CollectionChanged += OnActionsChanged;

        _loading = true;
        long ms = Math.Max(0, p.IntervalMs);
        IntervalHours = (int)(ms / 3_600_000);
        IntervalMinutes = (int)(ms / 60_000 % 60);
        IntervalSeconds = (int)(ms / 1000 % 60);
        IntervalMillis = (int)(ms % 1000);
        _loading = false;
        UpdateIntervalSummary();
    }

    private void DetachProfile(Profile p)
    {
        p.PropertyChanged -= OnProfileEdited;
        p.Hotkeys.PropertyChanged -= OnProfileEdited;
        p.Repeat.PropertyChanged -= OnProfileEdited;
        p.Burst.PropertyChanged -= OnProfileEdited;
        p.Window.PropertyChanged -= OnProfileEdited;
        p.Overlay.PropertyChanged -= OnProfileEdited;
        p.Actions.CollectionChanged -= OnActionsChanged;
    }

    private void OnActionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ActionListVersion++; // renumbers the rows
        OnProfileEdited(sender, new PropertyChangedEventArgs(nameof(Profile.Actions)));
    }

    private void OnProfileEdited(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading || e.PropertyName == nameof(Profile.ModifiedAtMs)) return; // ModifiedAtMs is stamped by the save itself
        if (sender is HotkeySettings || e.PropertyName == nameof(Profile.QuickSwitchHotkey)) ApplyHotkeys();
        Revalidate();
        AfterProfileEdited(sender, e);
    }

    /// <summary>Hook for the other partial files (unsaved-changes flag, overlays).</summary>
    partial void AfterProfileEdited(object? sender, PropertyChangedEventArgs e);

    // ------------------------------------------------------------------ interval fields

    partial void OnIntervalHoursChanged(int value) => OnIntervalPartChanged();
    partial void OnIntervalMinutesChanged(int value) => OnIntervalPartChanged();
    partial void OnIntervalSecondsChanged(int value) => OnIntervalPartChanged();
    partial void OnIntervalMillisChanged(int value) => OnIntervalPartChanged();

    private long IntervalTotalMs =>
        Math.Max(0, IntervalHours) * 3_600_000L + Math.Max(0, IntervalMinutes) * 60_000L
        + Math.Max(0, IntervalSeconds) * 1000L + Math.Max(0, IntervalMillis);

    private void OnIntervalPartChanged()
    {
        if (_loading) return;
        // A zero total is flagged by validation rather than silently corrected while the user is typing.
        Profile.IntervalMs = Math.Max(1, IntervalTotalMs);
        UpdateIntervalSummary();
        Revalidate();
    }

    private void UpdateIntervalSummary()
    {
        long ms = IntervalTotalMs;
        IntervalSummary = ms < 1
            ? "Interval must be at least 1 ms."
            : $"= {ms:N0} ms between actions ({1000.0 / ms:0.##} per second)";
    }

    // ------------------------------------------------------------------ run control

    [RelayCommand]
    private void ToggleRun()
    {
        if (_s.Engine.IsActive) _s.Engine.Stop(StopReason.User);
        else StartRun(skipStartDelay: false);
    }

    [RelayCommand]
    private void Pause()
    {
        _s.Engine.TogglePause();
        RefreshStatus();
    }

    private void StartRun(bool skipStartDelay)
    {
        if (_s.Engine.IsActive) return;
        if (IsRecording)
        {
            Footer = "Stop recording before starting a run.";
            return;
        }
        var errors = Validate();
        if (errors.Count > 0)
        {
            Footer = "Can't start: " + errors[0];
            return;
        }

        _s.Engine.AreaDebugFolder = Settings.SaveAreaSnapshots ? AppPaths.Debug : null;
        if (_s.Engine.Start(Profile, skipStartDelay))
        {
            Footer = "Running.";
            _cpsSamples.Clear();
            RefreshStatus();
        }
    }

    private void OnEngineStopped(StopReason reason, string? error)
    {
        Footer = reason switch
        {
            StopReason.EmergencyHotkey => "Emergency stop (hotkey).",
            StopReason.EmergencyCorner => "Emergency stop: mouse moved to a screen corner.",
            StopReason.Completed => "Finished: the repeat limit was reached.",
            StopReason.PixelTimeout => "Stopped: a pixel or area trigger timed out.",
            StopReason.Error => "Stopped with an error: " + (error ?? "unknown"),
            _ => "Stopped.",
        };
        RefreshStatus();
    }

    // ------------------------------------------------------------------ hotkeys

    private void OnHotkeyCaptureChanged(bool capturing)
    {
        if (capturing) _s.Hotkeys.Suspend();
        else
        {
            _s.Hotkeys.Resume();
            ApplyHotkeys();
        }
    }

    /// <summary>The hotkeys to register for the current profile.</summary>
    private List<HotkeyRequest> BuildHotkeyRequests()
    {
        var hk = Profile.Hotkeys;
        var list = new List<HotkeyRequest>
        {
            new(HkToggle, "Start / stop", hk.Toggle, TrackRelease: hk.Mode == HotkeyMode.Hold),
            new(HkPause, "Pause / resume", hk.Pause),
            new(HkCapture, "Capture position", hk.Capture),
            new(HkStop, "Emergency stop", hk.Stop),
            new(HkOverlay, "Position markers", hk.Overlay),
            new(HkRecord, "Record", hk.Record),
            new(HkProfilePrefix + CurrentRef.Key, $"Switch to {CurrentRef.Display}", Profile.QuickSwitchHotkey),
        };

        // Quick-switch keys of the other profiles stay live no matter which profile is loaded.
        foreach (var (other, combo) in _s.Profiles.QuickSwitchHotkeys())
        {
            if (!other.Is(CurrentRef))
                list.Add(new HotkeyRequest(HkProfilePrefix + other.Key, $"Switch to {other.Display}", combo));
        }
        return list;
    }

    /// <summary>
    /// Re-registers all global hotkeys. Two bindings sharing a combination is a validation
    /// error (only the first is registered); a combination owned by another program is a warning.
    /// </summary>
    private void ApplyHotkeys()
    {
        var seen = new Dictionary<KeyCombo, HotkeyRequest>();
        var duplicates = new List<string>();
        var valid = new List<HotkeyRequest>();
        foreach (var request in BuildHotkeyRequests())
        {
            if (request.Combo.IsEmpty) continue;
            if (seen.TryGetValue(request.Combo, out var other))
                duplicates.Add($"\"{request.Label}\" and \"{other.Label}\" both use {request.Combo}");
            else
            {
                seen.Add(request.Combo, request);
                valid.Add(request);
            }
        }

        var failed = _s.Hotkeys.Apply(valid);
        _hotkeyDuplicateError = duplicates.Count > 0 ? "Duplicate hotkeys: " + string.Join("; ", duplicates) + "." : "";
        HotkeyWarning = failed.Count > 0
            ? "Already in use by another program, so it won't work here: "
              + string.Join(", ", failed.Select(f => $"{f.Combo} ({f.Label})")) + ". Pick a different key."
            : "";
        UpdateToggleText();
        OnPropertyChanged(nameof(CaptureButtonText));
        OnPropertyChanged(nameof(RecordButtonText));
        Revalidate();
    }

    private void OnHotkeyPressed(string name)
    {
        switch (name)
        {
            case HkToggle:
                if (Profile.Hotkeys.Mode == HotkeyMode.Hold) StartRun(skipStartDelay: true);
                else ToggleRun();
                break;
            case HkPause:
                Pause();
                break;
            case HkCapture:
                CaptureAtCursor();
                break;
            case HkStop:
                _s.Engine.Stop(StopReason.EmergencyHotkey);
                break;
            case HkOverlay:
                TogglePositionOverlay();
                break;
            case HkRecord:
                ToggleRecording();
                break;
            default:
                if (name.StartsWith(HkProfilePrefix, StringComparison.Ordinal)) SwitchToProfileByHotkey(name[HkProfilePrefix.Length..]);
                break;
        }
    }

    private void OnHotkeyReleased(string name)
    {
        if (name == HkToggle) _s.Engine.Stop(StopReason.User);
    }

    // ------------------------------------------------------------------ validation

    private List<string> Validate()
    {
        var errors = new List<string>();
        var p = Profile;

        if (p.RandomInterval)
        {
            if (p.IntervalMinMs < 1) errors.Add("The random interval's minimum must be at least 1 ms.");
            if (p.IntervalMaxMs < p.IntervalMinMs) errors.Add("The random interval's maximum is below its minimum.");
        }
        else if (IntervalTotalMs < 1)
        {
            errors.Add("The click interval must be at least 1 ms.");
        }

        // An empty list is fine (plain clicker at the cursor), but a list with nothing enabled has nothing to run.
        if (p.Actions.Count > 0 && !p.Actions.Any(a => a.Enabled))
            errors.Add("Every action in the list is disabled. Enable one, or clear the list.");
        for (int i = 0; i < p.Actions.Count; i++)
        {
            if (p.Actions[i].Enabled && ActionEditorViewModel.ValidateAction(p.Actions[i]) is { } problem)
                errors.Add($"Action {i + 1}: {problem}");
        }

        // Synthetic key presses trigger global hotkeys just like real ones, so a sequence that
        // presses (say) the start/stop key would switch itself off.
        var hotkeyOwners = new Dictionary<KeyCombo, string>();
        foreach (var request in BuildHotkeyRequests())
        {
            if (!request.Combo.IsEmpty) hotkeyOwners.TryAdd(request.Combo, request.Label);
        }
        for (int i = 0; i < p.Actions.Count; i++)
        {
            if (p.Actions[i] is not KeyPressAction { Enabled: true } keys) continue;
            foreach (var combo in keys.Keys)
            {
                if (hotkeyOwners.TryGetValue(combo, out string? owner))
                    errors.Add($"Action {i + 1} presses {combo}, which is also the \"{owner}\" hotkey. Change one of them.");
            }
        }

        if (p.JitterPx < 0 || p.SmoothMoveMs < 0 || p.StartDelayMs < 0) errors.Add("Jitter, smooth move and countdown can't be negative.");
        if (p.SpeedMultiplier is not (>= 0.1 and <= 20)) errors.Add("Speed must be between 0.1 and 20.");
        if (p.Burst.Enabled && (p.Burst.Count < 1 || p.Burst.PauseMs < 0)) errors.Add("Burst needs at least 1 action and a pause of 0 ms or more.");
        switch (p.Repeat.Mode)
        {
            case RepeatMode.Count or RepeatMode.Loops when p.Repeat.Count < 1:
                errors.Add("The repeat count must be at least 1.");
                break;
            case RepeatMode.Duration when p.Repeat.DurationMs < 1:
                errors.Add("The run duration must be more than zero.");
                break;
            case RepeatMode.UntilTime when !RepeatSettings.TryParseTime(p.Repeat.UntilTime, out _):
                errors.Add("The stop time must look like 18:30 or 18:30:00.");
                break;
        }

        if (p.Window.Enabled && !p.Window.HasWindow) errors.Add("Pick a target window, or turn window targeting off.");
        if (_hotkeyDuplicateError.Length > 0) errors.Add(_hotkeyDuplicateError);
        return errors;
    }

    private void Revalidate()
    {
        var errors = Validate();
        ValidationMessage = errors.Count == 0 ? "" : string.Join("  ", errors);
    }

    // ------------------------------------------------------------------ status polling

    private void UpdateToggleText()
    {
        string key = Profile.Hotkeys.Toggle.IsEmpty ? "" : $"   [{Profile.Hotkeys.Toggle}]";
        ToggleText = (IsActive ? "STOP" : "START") + key;
    }

    private void RefreshStatus()
    {
        var snap = _s.Engine.GetSnapshot();
        IsActive = snap.State != EngineState.Stopped;
        (StateKind, StateText) = snap.State switch
        {
            EngineState.Countdown => ("Countdown", $"STARTING IN {snap.MsUntilNext / 1000:0.0}"),
            EngineState.Running when snap.OverOwnWindow => ("Waiting", "CURSOR IS ON THIS APP"),
            EngineState.Running when snap.WaitingForMarker => ("Waiting", "WAITING FOR MARKER"),
            EngineState.Running when snap.DifferentScreen => ("Waiting", "DIFFERENT SCREEN"),
            EngineState.Running => ("Running", "RUNNING"),
            EngineState.Paused => ("Paused", "PAUSED"),
            EngineState.WaitingForWindow => ("Waiting", "WAITING FOR WINDOW"),
            _ => ("Stopped", "STOPPED"),
        };
        PauseText = _s.Engine.IsPaused && IsActive ? "RESUME" : "PAUSE";
        UpdateToggleText();

        ClickCount = snap.Clicks;
        ActionCount = snap.Actions;
        SessionClicks = snap.TotalClicks;
        NextText = snap.State == EngineState.Running ? FormatDuration(snap.MsUntilNext) : "–";
        if (IsActive)
            RunTimeText = FormatClock(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - snap.RunStartEpochMs);

        // Actual clicks per second, measured over the last second of real output.
        long now = TimingEngine.Now;
        _cpsSamples.Enqueue((now, snap.TotalClicks));
        while (_cpsSamples.Count > 2 && TimingEngine.TicksToMs(now - _cpsSamples.Peek().Ticks) > 1000) _cpsSamples.Dequeue();
        var (oldTicks, oldClicks) = _cpsSamples.Peek();
        double seconds = TimingEngine.TicksToMs(now - oldTicks) / 1000;
        double cps = seconds > 0.05 ? (snap.TotalClicks - oldClicks) / seconds : 0;
        CpsText = cps.ToString(cps >= 100 ? "0" : "0.0");

        UpdateRecordStatus();
        AfterStatusRefreshed(snap);
    }

    /// <summary>Hook for the other partial files (overlays, tray).</summary>
    partial void AfterStatusRefreshed(EngineSnapshot snapshot);

    private static string FormatDuration(double ms) => ms switch
    {
        >= 60_000 => FormatClock((long)ms),
        >= 1000 => $"{ms / 1000:0.0} s",
        _ => $"{ms:0} ms",
    };

    private static string FormatClock(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public void Shutdown()
    {
        _statusTimer.Stop();
        HotkeyBox.CaptureActiveChanged -= OnHotkeyCaptureChanged;
        _s.Engine.Stop();
        OnShutdown();
    }

    partial void OnShutdown();
}
