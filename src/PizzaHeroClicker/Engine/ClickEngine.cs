using System.IO;
using PizzaHeroClicker.Models;
using PizzaHeroClicker.Services;

namespace PizzaHeroClicker.Engine;

public enum EngineState { Stopped, Countdown, Running, Paused, WaitingForWindow }

public enum StopReason { None, User, EmergencyHotkey, EmergencyCorner, Completed, PixelTimeout, Error }

/// <summary>A consistent-enough view of the engine for the UI to poll. Never blocks the engine.</summary>
public readonly record struct EngineSnapshot(
    EngineState State, long Actions, long Clicks, long TotalClicks, double MsUntilNext, int CurrentIndex, long RunStartEpochMs,
    bool OverOwnWindow, bool WaitingForMarker, bool DifferentScreen);

/// <summary>
/// Runs a profile's action list on a dedicated background thread.
///
/// THREADING. Start / Stop / TogglePause / GetSnapshot may be called from any thread. The
/// engine works on a private deep copy of the profile taken at Start, so the UI can keep
/// editing without locks. Counters are published with Interlocked / volatile fields. The
/// Stopped and StateChanged events are raised on the engine thread; subscribers marshal.
///
/// TIMING. The whole run follows one absolute timeline, <c>_t</c>. Every delay (interval,
/// click hold, glide step) advances <c>_t</c> by its nominal length and then waits until
/// <c>_t</c>, so the time spent inside SendInput never accumulates as drift: at a 1 ms
/// interval the engine really performs 1000 actions per second, not "1 ms plus overhead".
/// </summary>
public sealed class ClickEngine : IDisposable
{
    private const double SliceMs = 25;          // longest uninterrupted sleep; housekeeping runs between slices
    private const double CheckMs = 20;          // minimum spacing of housekeeping checks
    private const double MaxLagMs = 20;         // if we fall further behind than this, resync instead of bursting
    private const double GlideStepMs = 8;       // cursor glide update rate (~120 Hz)
    private const double DoubleClickGapMs = 40;
    private const double DragSettleMs = 10;     // pause after press / before release so apps register the drag
    private const double OwnWindowCheckMs = 15;  // how long a "is this our own window?" answer is reused
    private const long WindowSearchEveryMs = 500;
    private const long NeverSearched = long.MinValue / 2; // halved so "now - NeverSearched" cannot overflow

    private readonly IInputService _input;
    private readonly IScreenService _screen;
    private readonly IWindowService _windows;
    private readonly TimingEngine _timing;

    private readonly object _gate = new();
    private readonly ManualResetEvent _stop = new(false);
    private readonly IntPtr _stopHandle;
    private Thread? _thread;

    // Cross-thread state.
    private volatile bool _stopFlag;
    private volatile bool _pauseRequested;
    private volatile EngineState _state = EngineState.Stopped;
    private volatile int _currentIndex = -1;
    private volatile bool _overOwnWindow;
    private volatile bool _waitingForMarker;
    private volatile bool _differentScreen;
    private int _stopReason;
    private long _actions, _clicks, _totalClicks, _nextDue, _runStartEpochMs;

    // Engine-thread-only state (valid during a run).
    private Profile _cfg = new();
    private readonly Random _rng = new();
    private long _t;                    // the timeline: Stopwatch timestamp of the current scheduled instant
    private double _speed = 1;
    private long _lastCheck;
    private long _stopAtTicks;          // Duration mode
    private long _stopAtEpochMs;        // UntilTime mode
    private bool _cornerArmed;
    private bool _inCountdown;
    private (int X, int Y)? _lastSetCursor;
    private IntPtr _hwnd;
    private (int X, int Y) _ownCheckPoint = (int.MinValue, int.MinValue);
    private long _ownCheckAt;
    private bool _ownCheckResult;
    private int[] _areaPixels = [];
    private readonly Dictionary<AreaWatchAction, AreaDetector> _detectors = new(); // engine thread only; rebuilt each run
    private readonly Dictionary<AreaWatchAction, int[]?> _anchors = new();           // decoded start/stop markers
    private int[] _anchorPixels = [];
    private const double AnchorCheckMs = 100;   // how often the marker is looked at while there is nothing to click
    private const double AnchorGraceMs = 500;   // the marker must be gone this long before the watch ENDS
                                                // (clicking stops the instant it goes; see DoAreaWatch)
    private readonly List<(int X, int Y, long At)> _recentAreaClicks = new();        // spots to leave alone for a moment
    private readonly List<(int X, int Y)> _avoid = new();
    private readonly System.Text.StringBuilder _debugClicks = new();
    private long _lastWindowSearch = NeverSearched;

    public event Action<StopReason, string?>? Stopped;
    public event Action<EngineState>? StateChanged;

    public ClickEngine(IInputService input, IScreenService screen, IWindowService windows, TimingEngine timing)
    {
        _input = input;
        _screen = screen;
        _windows = windows;
        _timing = timing;
        _stopHandle = _stop.SafeWaitHandle.DangerousGetHandle();
    }

    /// <summary>
    /// Normally the engine refuses to click this app's own windows (see WaitUntilClearOfOwnWindow).
    /// The self-test turns this on because it clicks a test window of its own.
    /// </summary>
    public bool AllowOwnWindowClicks { get; set; }

    /// <summary>
    /// Troubleshooting: when set, an area watch saves what it captured about once a second into
    /// this folder (frame-NN.png plus report.txt), up to <see cref="MaxDebugSnapshots"/> per run.
    /// Read at the start of each run.
    /// </summary>
    public string? AreaDebugFolder { get; set; }

    public const int MaxDebugSnapshots = 40;
    private static readonly object DebugFileLock = new();
    private string? _debugFolder;
    private volatile int _debugRun;     // changes whenever a new recording starts
    private int _debugCount;
    private long _debugNextAt, _debugStartedAt;

    public EngineState State => _state;
    public bool IsActive => _state != EngineState.Stopped;
    public bool IsPaused => _pauseRequested;

    public EngineSnapshot GetSnapshot()
    {
        var state = _state;
        long due = Interlocked.Read(ref _nextDue);
        double msUntilNext = state is EngineState.Running or EngineState.Countdown && due != 0
            ? Math.Max(0, TimingEngine.TicksToMs(due - TimingEngine.Now))
            : 0;
        return new EngineSnapshot(state, Interlocked.Read(ref _actions), Interlocked.Read(ref _clicks),
            Interlocked.Read(ref _totalClicks), msUntilNext, _currentIndex, Interlocked.Read(ref _runStartEpochMs),
            _overOwnWindow, _waitingForMarker, _differentScreen);
    }

    /// <summary>Starts a run with a snapshot of <paramref name="profile"/>. Returns false if one is already active.</summary>
    public bool Start(Profile profile, bool skipStartDelay = false)
    {
        lock (_gate)
        {
            if (_thread is { IsAlive: true })
            {
                if (_state != EngineState.Stopped) return false;
                _thread.Join(); // previous run is past its loop and only finishing up
            }

            var cfg = ProfileJson.Clone(profile);
            _stop.Reset();
            _stopFlag = false;
            _pauseRequested = false;
            Interlocked.Exchange(ref _stopReason, 0);
            Interlocked.Exchange(ref _actions, 0);
            Interlocked.Exchange(ref _clicks, 0);
            Interlocked.Exchange(ref _nextDue, 0);
            Interlocked.Exchange(ref _runStartEpochMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _currentIndex = -1;

            bool countdown = !skipStartDelay && cfg.StartDelayMs > 0;
            _state = countdown ? EngineState.Countdown : EngineState.Running;

            _thread = new Thread(() => Run(cfg, countdown))
            {
                IsBackground = true,
                Name = "ClickEngine",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
            return true;
        }
    }

    /// <summary>Requests a stop. Returns immediately; the engine thread unwinds within a few milliseconds.</summary>
    public void Stop(StopReason reason = StopReason.User)
    {
        if (!IsActive) return;
        Interlocked.CompareExchange(ref _stopReason, (int)reason, 0); // first reason wins
        _stopFlag = true;
        _stop.Set();
    }

    public void TogglePause()
    {
        if (IsActive) _pauseRequested = !_pauseRequested;
    }

    // ================================================================== engine thread

    private void Run(Profile cfg, bool countdown)
    {
        string? error = null;
        _timing.BeginHighResolution();
        try
        {
            _cfg = cfg;
            _speed = cfg.SpeedMultiplier;
            _hwnd = IntPtr.Zero;
            _lastWindowSearch = NeverSearched;
            _cornerArmed = false;
            _inCountdown = false;
            _overOwnWindow = false;
            _ownCheckPoint = (int.MinValue, int.MinValue);
            _detectors.Clear();
            _anchors.Clear();
            _recentAreaClicks.Clear();
            _debugClicks.Clear();
            StartDebugRecording();
            _lastSetCursor = null;
            _lastCheck = 0;
            _stopAtTicks = long.MaxValue;
            _stopAtEpochMs = long.MaxValue;

            // Empty list = plain clicker: click at the current cursor position.
            var actions = cfg.Actions.Where(a => a.Enabled).ToList();
            bool plainClicker = cfg.Actions.Count == 0;
            if (plainClicker)
                actions.Add(new ClickAction { UseCursor = true, Button = cfg.DefaultButton, Kind = cfg.DefaultKind });
            if (actions.Count == 0)
                throw new InvalidOperationException("Every action in the list is disabled.");

            // Position of each runnable action in the profile's list, for the UI's "current action".
            int[] listIndex = actions.Select(a => plainClicker ? -1 : cfg.Actions.IndexOf(a)).ToArray();

            _t = TimingEngine.Now;
            if (countdown)
            {
                _inCountdown = true;
                Interlocked.Exchange(ref _nextDue, _t + TimingEngine.MsToTicks(cfg.StartDelayMs));
                if (!Advance(cfg.StartDelayMs, scaled: false)) return;
                _inCountdown = false;
                SetState(EngineState.Running);
            }

            Interlocked.Exchange(ref _runStartEpochMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (cfg.Repeat.Mode == RepeatMode.Duration)
                _stopAtTicks = TimingEngine.Now + TimingEngine.MsToTicks(cfg.Repeat.DurationMs);
            if (cfg.Repeat.Mode == RepeatMode.UntilTime)
                _stopAtEpochMs = cfg.Repeat.ResolveUntilEpochMs(DateTimeOffset.Now) ?? long.MaxValue;

            int next = 0, burst = 0;
            long done = 0, loops = 0;
            while (true)
            {
                if (!Housekeeping()) return;

                // Never try to "catch up" a large backlog (after a hiccup or a pause) with a burst of clicks.
                long now = TimingEngine.Now;
                if (now - _t > TimingEngine.MsToTicks(MaxLagMs)) _t = now;

                int index = cfg.Order == RunOrder.Random ? _rng.Next(actions.Count) : next;
                var action = actions[index];
                _currentIndex = listIndex[index];
                Interlocked.Exchange(ref _nextDue, 0);

                if (!Execute(action)) return;

                Interlocked.Increment(ref _actions);
                done++;
                next = (next + 1) % actions.Count;
                if (done % actions.Count == 0) loops++;

                if ((cfg.Repeat.Mode == RepeatMode.Count && done >= cfg.Repeat.Count) ||
                    (cfg.Repeat.Mode == RepeatMode.Loops && loops >= cfg.Repeat.Count))
                {
                    Stop(StopReason.Completed);
                    return;
                }

                double interval = NextInterval(action);
                if (cfg.Burst.Enabled && ++burst >= cfg.Burst.Count)
                {
                    burst = 0;
                    interval = cfg.Burst.PauseMs; // the pause replaces the normal interval
                }

                Interlocked.Exchange(ref _nextDue, _t + TimingEngine.MsToTicks(interval / _speed));
                if (!Advance(interval)) return;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Engine run failed", ex);
            error = ex.Message;
            Interlocked.Exchange(ref _stopReason, (int)StopReason.Error);
        }
        finally
        {
            try { _input.ReleaseAll(); }
            catch (Exception ex) { Log.Error("Releasing held input failed", ex); }
            _timing.EndHighResolution();

            Interlocked.CompareExchange(ref _stopReason, (int)StopReason.User, 0);
            _currentIndex = -1;
            _overOwnWindow = false;
            _waitingForMarker = false;
            _differentScreen = false;
            Interlocked.Exchange(ref _nextDue, 0);
            SetState(EngineState.Stopped);
            try { Stopped?.Invoke((StopReason)Volatile.Read(ref _stopReason), error); }
            catch (Exception ex) { Log.Error("Stopped handler failed", ex); }
        }
    }

    private void SetState(EngineState state)
    {
        if (_state == state) return;
        _state = state;
        try { StateChanged?.Invoke(state); }
        catch (Exception ex) { Log.Error("StateChanged handler failed", ex); }
    }

    /// <summary>
    /// Moves the timeline forward by <paramref name="ms"/> and waits for it. Long waits are cut
    /// into slices so stop, pause, emergency-corner and focus checks stay responsive even
    /// during an hour-long interval. Returns false when the run should end.
    /// </summary>
    private bool Advance(double ms, bool scaled = true)
    {
        if (scaled) ms /= _speed;
        if (ms > 0) _t += TimingEngine.MsToTicks(ms);

        long slice = TimingEngine.MsToTicks(SliceMs);
        while (true)
        {
            if (_stopFlag) return false;
            long now = TimingEngine.Now;
            if (_t - now <= slice) return _timing.WaitUntil(_t, _stopHandle);
            if (!_timing.WaitUntil(now + slice, _stopHandle)) return false;
            if (!Housekeeping()) return false; // may block for a pause, which shifts _t
        }
    }

    /// <summary>
    /// Rate-limited safety and gating checks. Blocks while paused or while the target window
    /// is unfocused, then shifts the timeline by the time spent blocked.
    /// </summary>
    private bool Housekeeping()
    {
        if (_stopFlag) return false;
        long now = TimingEngine.Now;
        if (now - _lastCheck < TimingEngine.MsToTicks(CheckMs)) return true;
        _lastCheck = now;

        if (!SafetyChecks()) return false;
        if (!ShouldBlock(out var blockState)) return true;

        // Entering a blocked state: let go of anything held so nothing stays pressed in the game.
        _input.ReleaseAll();
        var resumeState = _state;
        long blockedAt = TimingEngine.Now;
        SetState(blockState);
        while (true)
        {
            if (_stop.WaitOne(50) || _stopFlag) return false;
            if (!SafetyChecks()) return false;
            if (!ShouldBlock(out blockState)) break;
            SetState(blockState);
        }

        long blocked = TimingEngine.Now - blockedAt;
        _t += blocked;
        if (_stopAtTicks != long.MaxValue) _stopAtTicks += blocked; // paused time does not count toward a duration limit
        long due = Interlocked.Read(ref _nextDue);
        if (due != 0) Interlocked.Exchange(ref _nextDue, due + blocked);
        _lastCheck = TimingEngine.Now;
        SetState(resumeState);
        return true;
    }

    private bool SafetyChecks()
    {
        if (_cfg.CornerStop)
        {
            var (x, y) = _input.GetCursor();
            bool atCorner = _screen.IsAtStuckCorner(x, y);
            // Arm only once the cursor has been seen away from a corner, and ignore corners the
            // engine itself moved to (a click target can legitimately be in a corner).
            bool ownMove = _lastSetCursor is var (lx, ly) && Math.Abs(x - lx) <= 2 && Math.Abs(y - ly) <= 2;
            if (!atCorner) _cornerArmed = true;
            else if (_cornerArmed && !ownMove)
            {
                Stop(StopReason.EmergencyCorner);
                return false;
            }
        }

        if (TimingEngine.Now >= _stopAtTicks || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= _stopAtEpochMs)
        {
            Stop(StopReason.Completed);
            return false;
        }
        return true;
    }

    private bool ShouldBlock(out EngineState state)
    {
        state = EngineState.Paused;
        if (_pauseRequested) return true;

        // The start countdown exists precisely so the user can switch to the target window.
        if (_cfg.Window is { Enabled: true, OnlyWhenFocused: true } && !_inCountdown)
        {
            state = EngineState.WaitingForWindow;
            return !_windows.IsForeground(ResolveWindow());
        }
        return false;
    }

    private IntPtr ResolveWindow()
    {
        if (_windows.IsAlive(_hwnd)) return _hwnd;
        _hwnd = IntPtr.Zero;
        long now = Environment.TickCount64;
        if (now - _lastWindowSearch >= WindowSearchEveryMs) // enumerating windows is not free
        {
            _lastWindowSearch = now;
            _hwnd = _windows.Find(_cfg.Window);
        }
        return _hwnd;
    }

    /// <summary>Converts a stored position to a screen position (adds the window origin in relative mode).</summary>
    private (int X, int Y) Resolve(int x, int y)
    {
        if (_cfg.Window is not { Enabled: true, RelativeCoordinates: true }) return (x, y);
        _lastWindowSearch = NeverSearched; // an action needs the window now: search immediately
        var hwnd = ResolveWindow();
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("The target window was not found.");
        var origin = _windows.GetClientOrigin(hwnd);
        return (x + origin.X, y + origin.Y);
    }

    private double NextInterval(ActionBase action)
    {
        if (action.IntervalMs is int lo)
            return action.IntervalMaxMs is int hi && hi > lo ? _rng.Next(lo, hi + 1) : lo;

        if (action is WaitAction) return 0; // a Wait is already a delay; don't add the global interval on top

        if (_cfg.RandomInterval && _cfg.IntervalMaxMs > _cfg.IntervalMinMs)
            return _cfg.IntervalMinMs + _rng.NextDouble() * (_cfg.IntervalMaxMs - _cfg.IntervalMinMs);
        if (_cfg.RandomInterval) return Math.Max(1, _cfg.IntervalMinMs);
        return Math.Max(1, _cfg.IntervalMs);
    }

    // ------------------------------------------------------------------ actions

    private bool Execute(ActionBase action) => action switch
    {
        ClickAction a => DoClick(a.UseCursor, a.X, a.Y, a.Button, a.Kind, a.HoldMs, a.JitterPx ?? _cfg.JitterPx),
        KeyPressAction a => DoKeys(a),
        WaitAction a => Advance(a.Ms),
        ScrollAction a => DoScroll(a),
        DragAction a => DoDrag(a),
        MoveAction a => DoMove(a),
        PixelClickAction a => DoPixelClick(a),
        PixelWaitAction a => WaitForPixel(a, out _),
        AreaWatchAction a => DoAreaWatch(a),
        _ => true,
    };

    private bool DoClick(bool useCursor, int x, int y, ClickButton button, ClickKind kind, int holdMs, int jitter)
    {
        if (!useCursor)
        {
            (x, y) = Resolve(x, y);
            if (jitter > 0)
            {
                x += _rng.Next(-jitter, jitter + 1);
                y += _rng.Next(-jitter, jitter + 1);
            }
            if (!WaitUntilClearOfOwnWindow(x, y, followCursor: false)) return false;
            if (!MoveCursor(x, y)) return false;
        }
        else if (!WaitUntilClearOfOwnWindow(0, 0, followCursor: true))
        {
            return false;
        }

        int presses = kind == ClickKind.Double ? 2 : 1;
        for (int i = 0; i < presses; i++)
        {
            if (i > 0 && !Advance(DoubleClickGapMs)) return false;
            if (holdMs <= 0)
            {
                _input.Click(button);
            }
            else
            {
                _input.ButtonDown(button);
                if (!Advance(holdMs)) return false; // Run's finally releases the button
                _input.ButtonUp(button);
            }
            Interlocked.Increment(ref _clicks);
            Interlocked.Increment(ref _totalClicks);
        }
        return true;
    }

    /// <summary>
    /// Holds the run while the click would land on one of this app's own windows.
    ///
    /// Without this, starting with the cursor still on the START button makes a fast run
    /// hammer its own STOP button: the first click stops the run and the clicks already queued
    /// behind it start and stop it again, which at 1 ms looks like the app has gone haywire.
    /// The run simply waits (the UI says why) and continues the moment the cursor is elsewhere.
    /// The answer is cached briefly so a 1 ms run does not hit-test the desktop 1000 times a second.
    /// </summary>
    private bool WaitUntilClearOfOwnWindow(int x, int y, bool followCursor)
    {
        if (AllowOwnWindowClicks) return true;
        bool waited = false;
        while (true)
        {
            if (followCursor) (x, y) = _input.GetCursor();
            long now = TimingEngine.Now;
            if ((x, y) != _ownCheckPoint || now - _ownCheckAt >= TimingEngine.MsToTicks(OwnWindowCheckMs))
            {
                _ownCheckPoint = (x, y);
                _ownCheckAt = now;
                _ownCheckResult = _windows.IsOwnWindowAt(x, y);
            }
            if (!_ownCheckResult) break;

            waited = true;
            _overOwnWindow = true;
            Interlocked.Exchange(ref _nextDue, 0);
            _t = TimingEngine.Now;
            if (!Advance(OwnWindowCheckMs, scaled: false)) return false;
            if (!Housekeeping()) return false;
        }

        if (waited)
        {
            _overOwnWindow = false;
            _t = TimingEngine.Now; // the wait had no fixed length: restart the timeline from here
        }
        return true;
    }

    private bool DoKeys(KeyPressAction a)
    {
        for (int i = 0; i < a.Keys.Count; i++)
        {
            if (i > 0 && !Advance(a.GapMs)) return false;
            var combo = a.Keys[i];
            if (combo.IsEmpty) continue;
            _input.KeyComboDown(combo);
            if (a.HoldMs > 0 && !Advance(a.HoldMs)) return false;
            _input.KeyComboUp(combo);
        }
        return true;
    }

    private bool DoScroll(ScrollAction a)
    {
        if (!a.UseCursor)
        {
            var (x, y) = Resolve(a.X, a.Y);
            if (!MoveCursor(x, y)) return false;
        }
        _input.Scroll(a.Direction, a.Amount);
        return true;
    }

    private bool DoDrag(DragAction a)
    {
        var (x1, y1) = Resolve(a.X1, a.Y1);
        var (x2, y2) = Resolve(a.X2, a.Y2);
        if (!MoveCursor(x1, y1)) return false;
        _input.ButtonDown(a.Button);
        if (!Advance(DragSettleMs)) return false;
        if (!Glide(x2, y2, Math.Max(a.DurationMs, 1))) return false;
        if (!Advance(DragSettleMs)) return false;
        _input.ButtonUp(a.Button);
        return true;
    }

    private bool DoMove(MoveAction a)
    {
        var (x, y) = Resolve(a.X, a.Y);
        if (a.DurationMs > 0) return Glide(x, y, a.DurationMs);
        SetCursor(x, y);
        return true;
    }

    private bool DoPixelClick(PixelClickAction a)
    {
        if (!WaitForPixel(a, out bool matched)) return false;
        if (!matched) return true; // timed out with "skip"
        return a.ClickAtPixel
            ? DoClick(false, a.X, a.Y, a.Button, a.Kind, a.HoldMs, 0)
            : DoClick(false, a.ClickX, a.ClickY, a.Button, a.Kind, a.HoldMs, 0);
    }

    /// <summary>
    /// Polls a single screen pixel until it matches. Returns false if the run should end;
    /// <paramref name="matched"/> is false when the wait timed out with the "skip" behaviour.
    /// </summary>
    private bool WaitForPixel(PixelActionBase a, out bool matched)
    {
        matched = false;
        if (!PixelColor.TryParse(a.Color, out var target))
            throw new InvalidOperationException($"'{a.Color}' is not a valid colour (expected #RRGGBB).");

        var (x, y) = Resolve(a.X, a.Y);
        long deadline = a.TimeoutMs > 0 ? TimingEngine.Now + TimingEngine.MsToTicks(a.TimeoutMs) : long.MaxValue;
        while (true)
        {
            if (_screen.GetPixel(x, y) is { } actual && actual.Matches(target, a.Tolerance))
            {
                matched = true;
                break;
            }
            if (TimingEngine.Now >= deadline)
            {
                if (a.OnTimeout == PixelTimeoutBehavior.Stop)
                {
                    Stop(StopReason.PixelTimeout);
                    return false;
                }
                break;
            }
            _t = TimingEngine.Now;
            if (!Advance(a.PollMs, scaled: false)) return false;
            if (!Housekeeping()) return false;
        }
        _t = TimingEngine.Now; // the wait had no fixed length: restart the timeline from here
        return true;
    }

    /// <summary>
    /// Waits for something matching one of the action's pictures or colours to appear in its
    /// rectangle, then clicks it with that rule's button. One target per call; the action loop repeats it.
    /// </summary>
    private bool DoAreaWatch(AreaWatchAction a)
    {
        // Decoding pictures and parsing colours happens once per run, not once per pass.
        if (!_detectors.TryGetValue(a, out var detector)) _detectors[a] = detector = new AreaDetector(a);

        long TimeoutFromNow() => a.TimeoutMs > 0 ? TimingEngine.Now + TimingEngine.MsToTicks(a.TimeoutMs) : long.MaxValue;
        Interlocked.Exchange(ref _nextDue, 0);

        // Start/stop marker: first wait for that piece of the screen to appear.
        int[]? anchor = GetAnchor(a);
        if (anchor is not null)
        {
            long appearBy = TimeoutFromNow();
            _waitingForMarker = true;
            try
            {
                while (!AnchorShowing(a, anchor))
                {
                    if (TimingEngine.Now >= appearBy)
                    {
                        if (a.OnTimeout == PixelTimeoutBehavior.Stop)
                        {
                            Stop(StopReason.PixelTimeout);
                            return false;
                        }
                        _t = TimingEngine.Now;
                        return true;
                    }
                    _t = TimingEngine.Now;
                    if (!Advance(Math.Max(a.PollMs, 50), scaled: false)) return false;
                    if (!Housekeeping()) return false;
                }
            }
            finally
            {
                _waitingForMarker = false;
            }
        }
        long anchorLastSeen = TimingEngine.Now, anchorNextCheck = 0;

        // "Keep watching for": stay on this action, target after target, until this moment. 0 = no time limit
        // (one target only, unless a marker decides when the watch ends).
        long watchUntil = a.WatchForMs > 0 ? TimingEngine.Now + TimingEngine.MsToTicks(a.WatchForMs) : 0;
        bool keepWatching = watchUntil != 0 || anchor is not null;
        long deadline = TimeoutFromNow();
        while (true)
        {
            if (watchUntil != 0 && TimingEngine.Now >= watchUntil)
            {
                _t = TimingEngine.Now; // watched for the full time: move on
                RecordWatchEnd(a);
                return true;
            }
            if (anchor is not null && TimingEngine.Now >= anchorNextCheck)
            {
                anchorNextCheck = TimingEngine.Now + TimingEngine.MsToTicks(AnchorCheckMs);
                if (AnchorShowing(a, anchor)) anchorLastSeen = TimingEngine.Now;
                else if (TimingEngine.Now - anchorLastSeen >= TimingEngine.MsToTicks(AnchorGraceMs))
                {
                    _t = TimingEngine.Now; // the marker has gone: that screen is over
                    RecordWatchEnd(a);
                    return true;
                }
            }

            var (left, top) = Resolve(a.X, a.Y); // re-resolved every pass: the target window may move
            bool captured = _screen.TryCapture(left, top, a.Width, a.Height, ref _areaPixels);
            bool record = captured && _debugFolder is not null && _debugCount < MaxDebugSnapshots && TimingEngine.Now >= _debugNextAt;
            detector.CollectReport = record;

            // Spots clicked within the last "leave it alone" period are passed over.
            long forget = TimingEngine.Now - TimingEngine.MsToTicks(a.RetargetDelayMs);
            _recentAreaClicks.RemoveAll(c => c.At < forget);
            _avoid.Clear();
            foreach (var c in _recentAreaClicks) _avoid.Add((c.X, c.Y));

            var found = captured ? detector.Find(_areaPixels, -1, _avoid) : null;
            _differentScreen = captured && detector.IsScreenChanged;
            if (record) SaveDebugSnapshot(a, detector);
            if (found is var (x, y, rule))
            {
                // The marker is looked at again immediately before every click. When the game's
                // screen closes, whatever replaces it (an upgrade shop, say) can contain things
                // that look like targets, and a single stray click there can cost something.
                // So: no marker, no click, with no grace period. Only ENDING the watch waits a
                // moment, so that something drifting across the marker does not end it early.
                if (anchor is not null)
                {
                    if (!AnchorShowing(a, anchor))
                    {
                        anchorNextCheck = 0; // re-check at the top of the loop, which also handles ending
                        _t = TimingEngine.Now;
                        if (!Advance(Math.Min(a.PollMs, 20), scaled: false)) return false;
                        if (!Housekeeping()) return false;
                        continue;
                    }
                    anchorLastSeen = TimingEngine.Now;
                }

                _t = TimingEngine.Now; // the wait had no fixed length: restart the timeline from here
                // DoClick takes profile-space coordinates and resolves them itself.
                if (!DoClick(false, a.X + x, a.Y + y, a.Rules[rule].Button, ClickKind.Single, a.HoldMs, 0)) return false;
                detector.NotifyClicked();
                if (a.RetargetDelayMs > 0) _recentAreaClicks.Add((x, y, TimingEngine.Now));
                if (_debugFolder is not null && _debugClicks.Length < 20_000)
                {
                    double at = TimingEngine.TicksToMs(TimingEngine.Now - _debugStartedAt) / 1000;
                    _debugClicks.Append($"  {at:0.00} s  {a.Rules[rule].Button.ToString().ToLowerInvariant()}-click at ({a.X + x}, {a.Y + y}) for rule {rule + 1}{Environment.NewLine}");
                }
                if (!keepWatching) return true;

                // Keep watching: pause as between any two actions, then look for the next target.
                if (!Advance(NextInterval(a))) return false;
                if (!Housekeeping()) return false;
                deadline = TimeoutFromNow(); // "give up after" counts from the last target
                continue;
            }

            if (TimingEngine.Now >= deadline)
            {
                if (a.OnTimeout == PixelTimeoutBehavior.Stop)
                {
                    Stop(StopReason.PixelTimeout);
                    return false;
                }
                _t = TimingEngine.Now;
                return true;
            }
            _t = TimingEngine.Now;
            if (!Advance(a.PollMs, scaled: false)) return false;
            if (!Housekeeping()) return false;
        }
    }

    /// <summary>The action's start/stop marker as pixels, decoded once per run. Null if it has none.</summary>
    private int[]? GetAnchor(AreaWatchAction a)
    {
        if (_anchors.TryGetValue(a, out var cached)) return cached;
        int[]? pixels = null;
        if (a.HasAnchor)
        {
            if (!TemplateImage.TryDecode(a.AnchorImage, out var decoded, out int w, out int h) || w != a.AnchorWidth || h != a.AnchorHeight)
                throw new InvalidOperationException("The area watch's start/stop marker can't be read. Snip it again.");
            pixels = decoded;
        }
        _anchors[a] = pixels;
        return pixels;
    }

    private bool AnchorShowing(AreaWatchAction a, int[] anchor)
    {
        var (x, y) = Resolve(a.AnchorX, a.AnchorY);
        return _screen.TryCapture(x, y, a.AnchorWidth, a.AnchorHeight, ref _anchorPixels)
            && ScreenAnchor.Similarity(_anchorPixels, anchor) * 100 >= a.AnchorMatchPercent;
    }

    private void StartDebugRecording()
    {
        _debugFolder = null;
        _debugRun++;
        _debugCount = 0;
        _debugNextAt = 0;
        _debugStartedAt = TimingEngine.Now;
        if (AreaDebugFolder is not { Length: > 0 } folder) return;
        try
        {
            Directory.CreateDirectory(folder);
            // Nothing is deleted here: a run that never reaches an area watch (another profile, a
            // run stopped early) must not wipe the last useful recording. The old snapshots are
            // replaced when this run saves its first one.
            _debugFolder = folder;
        }
        catch (Exception ex)
        {
            Log.Error("Could not start saving troubleshooting snapshots", ex);
        }
    }

    /// <summary>Hands a copy of the frame and its report to a background thread to write, so the run is not held up.</summary>
    private void SaveDebugSnapshot(AreaWatchAction a, AreaDetector detector)
    {
        int number = ++_debugCount;
        _debugNextAt = TimingEngine.Now + TimingEngine.MsToTicks(1000);
        string folder = _debugFolder!;
        if (number == 1)
        {
            // First snapshot of this run: now the previous run's files make way.
            try
            {
                foreach (string old in Directory.EnumerateFiles(folder, "*.png")) File.Delete(old);
                File.WriteAllText(Path.Combine(folder, "report.txt"),
                    $"Area watch troubleshooting run, started {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
                Log.Info($"Area watch troubleshooting snapshots are being saved to {folder}");
            }
            catch (Exception ex)
            {
                Log.Error("Could not clear the previous troubleshooting snapshots", ex);
            }
        }
        if (number == 4) SaveSurroundings(a, folder, "surroundings.png");
        else if (number % 5 == 4) SaveSurroundings(a, folder, $"surroundings-{number:00}.png");
        var pixels = (int[])_areaPixels.Clone();
        int width = a.Width, height = a.Height;
        double seconds = TimingEngine.TicksToMs(TimingEngine.Now - _debugStartedAt) / 1000;
        string text = $"{Environment.NewLine}===== frame-{number:00}.png  at {seconds:0.0} s  (area {width} x {height} at {a.X}, {a.Y}; clicks so far {Interlocked.Read(ref _clicks)}){Environment.NewLine}"
                      + detector.DescribeLastReport() + Environment.NewLine
                      + (_debugClicks.Length > 0 ? $"Clicks since the previous frame:{Environment.NewLine}{_debugClicks}" : "");
        _debugClicks.Clear();
        Task.Run(() =>
        {
            try
            {
                TemplateImage.SavePng(Path.Combine(folder, $"frame-{number:00}.png"), pixels, width, height);
                lock (DebugFileLock) File.AppendAllText(Path.Combine(folder, "report.txt"), text);
            }
            catch (Exception ex)
            {
                Log.Error("Could not save a troubleshooting snapshot", ex);
            }
        });
    }

    /// <summary>
    /// Once per run: one picture of the watched area plus a border around it. The area itself
    /// often leaves out the game's labels and counters on purpose, and those are exactly what is
    /// needed when choosing a start/stop marker.
    /// </summary>
    /// <summary>
    /// When a recorded watch ends: one wider picture at that moment and a few more over the next
    /// seconds, with nothing being clicked. They show how the game finishes and what replaces it,
    /// which is what decides whether a marker can be trusted to stop the watch in time.
    /// </summary>
    private void RecordWatchEnd(AreaWatchAction a)
    {
        if (_debugFolder is not { } folder || _debugCount == 0) return;
        SaveSurroundings(a, folder, "surroundings-end.png");
        int run = _debugRun;
        Task.Run(async () =>
        {
            for (int seconds = 1; seconds <= 8; seconds++)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                if (_debugRun != run) return; // a new recording has begun
                SaveSurroundings(a, folder, $"surroundings-after-{seconds}s.png");
            }
        });
    }

    private void SaveSurroundings(AreaWatchAction a, string folder, string fileName)
    {
        const int border = 160;
        try
        {
            var (left, top) = Resolve(a.X, a.Y);
            var (vx, vy, vw, vh) = ScreenService.VirtualScreen;
            int x0 = Math.Max(vx, left - border), y0 = Math.Max(vy, top - border);
            int x1 = Math.Min(vx + vw, left + a.Width + border), y1 = Math.Min(vy + vh, top + a.Height + border);
            int[] pixels = [];
            if (x1 - x0 < 1 || y1 - y0 < 1 || !_screen.TryCapture(x0, y0, x1 - x0, y1 - y0, ref pixels)) return;
            int width = x1 - x0, height = y1 - y0;
            string note = $"{Environment.NewLine}{fileName} shows the screen from ({x0}, {y0}), {width} x {height}: the watched area plus a {border} px border.{Environment.NewLine}";
            Task.Run(() =>
            {
                try
                {
                    TemplateImage.SavePng(Path.Combine(folder, fileName), pixels, width, height);
                    lock (DebugFileLock) File.AppendAllText(Path.Combine(folder, "report.txt"), note);
                }
                catch (Exception ex)
                {
                    Log.Error("Could not save the surroundings snapshot", ex);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error("Could not capture the surroundings snapshot", ex);
        }
    }

    // ------------------------------------------------------------------ cursor helpers

    private bool MoveCursor(int x, int y)
    {
        if (_cfg.SmoothMoveMs > 0) return Glide(x, y, _cfg.SmoothMoveMs);
        SetCursor(x, y);
        return true;
    }

    private void SetCursor(int x, int y)
    {
        _input.MoveTo(x, y);
        _lastSetCursor = (x, y);
    }

    /// <summary>Moves the cursor to a point over <paramref name="durationMs"/> with an ease-in-out curve.</summary>
    private bool Glide(int x, int y, double durationMs)
    {
        var (sx, sy) = _input.GetCursor();
        if (sx == x && sy == y) return true;

        int steps = Math.Max(1, (int)(durationMs / GlideStepMs));
        double stepMs = durationMs / steps;
        for (int i = 1; i <= steps; i++)
        {
            if (!Advance(stepMs)) return false;
            double p = (double)i / steps;
            p = p * p * (3 - 2 * p); // smoothstep
            SetCursor((int)Math.Round(sx + (x - sx) * p), (int)Math.Round(sy + (y - sy) * p));
        }
        return true;
    }

    public void Dispose()
    {
        Stop();
        Thread? thread;
        lock (_gate) thread = _thread;
        thread?.Join(2000);
        _stop.Dispose();
    }
}
