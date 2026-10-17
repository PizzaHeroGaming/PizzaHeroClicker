using System.IO;
using System.Text;
using PizzaHeroClicker.Models;
using PizzaHeroClicker.Services;

namespace PizzaHeroClicker.Engine;

public enum EngineState { Stopped, Countdown, Running, Paused, WaitingForWindow }

public enum StopReason { None, User, EmergencyHotkey, EmergencyCorner, Completed, PixelTimeout, Error }

/// <summary>A consistent-enough view of the engine for the UI to poll. Never blocks the engine.</summary>
public readonly record struct EngineSnapshot(
    EngineState State, long Actions, long Clicks, long TotalClicks, double MsUntilNext, int CurrentIndex, long RunStartEpochMs,
    bool OverOwnWindow, bool WaitingForMarker, bool DifferentScreen, int MarkerMatch);

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
    private volatile int _markerMatch = -1;   // how alike the marker spot was at the last look, in percent (-1 = not looked)
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
    private readonly Dictionary<BarBlockAction, BarBlockDetector> _barDetectors = new();
    private readonly Dictionary<IHasMarker, int[]?> _anchors = new();           // decoded start/stop markers
    // How far each marker was found from where it was snipped: the game window has moved by that
    // much, so the area it belongs to is moved with it. Found by searching around the snipped spot.
    private readonly Dictionary<IHasMarker, (int X, int Y)> _anchorShift = new();
    private readonly Dictionary<IHasMarker, long> _anchorNextSearch = new();
    private int[] _anchorRegion = [];
    private const int AnchorSearchPx = 40;       // how far from its snipped spot a marker is looked for
    private const int LingerPx = 15;             // something this close to a just-clicked spot is what was clicked, lingering
    private const double AnchorSearchEveryMs = 400;
    private int[] _anchorPixels = [];
    private const double AnchorCheckMs = 100;   // how often the marker is looked at while there is nothing to click
    private const double AnchorCoveredMs = 3000; // a marker hidden while the game screen is still there: at most this long
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
    private bool _debugFilesStarted;    // this run has replaced the previous run's files
    private readonly StringBuilder _debugLooks = new(); // looks.txt: time (ms) then every candidate's screen position
    private readonly StringBuilder _debugScreen = new(); // screen.txt: per look, how much of the area changed, and why nothing was clicked
    private readonly Queue<(double Ms, int[] Pixels, int Width, int Height)> _recentLooks = new();
    private const int RecentLooksKept = 45;
    // Close-ups round each click: "a" from the look that found the target, "b" a moment after the click.
    private const int MaxCloseUps = 150, CloseUpRadius = 70;
    private const double CloseUpAfterMs = 150;
    private int _debugCloseUps;
    private readonly List<(int N, int SeenX, int SeenY, int ClickX, int ClickY, long Due)> _closeUpsDue = new();
    private int _markerSnapshots;
    private long _markerSnapshotNextAt;
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
            _overOwnWindow, _waitingForMarker, _differentScreen, _markerMatch);
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
            _barDetectors.Clear();
            _anchors.Clear();
            _anchorShift.Clear();
            _anchorNextSearch.Clear();
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
            _markerMatch = -1;
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
        _releasedAll++;
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
        BarBlockAction a => DoBarBlock(a),
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
        if (!_detectors.TryGetValue(a, out var detector))
        {
            _detectors[a] = detector = new AreaDetector(a);
            detector.SpotIgnored += (x, y) => Log.Info($"Area watch: ignoring the spot at ({a.X + x}, {a.Y + y}) for the rest of this run. " +
                                                       "Something there stayed put while the game moved, so it is part of the game's display (a score, an icon), not a target.");
        }

        long TimeoutFromNow() => a.TimeoutMs > 0 ? TimingEngine.Now + TimingEngine.MsToTicks(a.TimeoutMs) : long.MaxValue;
        Interlocked.Exchange(ref _nextDue, 0);

        // Start/stop marker: first wait for that piece of the screen to appear.
        int[]? anchor = GetAnchor(a);
        if (anchor is not null)
        {
            switch (WaitForMarker(a, anchor, TimeoutFromNow(), a.PollMs))
            {
                case MarkerWait.Stopped:
                    return false;
                case MarkerWait.TimedOut when a.OnTimeout == PixelTimeoutBehavior.Stop:
                    Stop(StopReason.PixelTimeout);
                    return false;
                case MarkerWait.TimedOut:
                    _t = TimingEngine.Now;
                    return true;
            }
        }
        long anchorLastSeen = TimingEngine.Now, anchorNextCheck = 0;
        bool gameStillShowing = false; // the last look found the learned game screen, unchanged and still

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
                if (AnchorShowing(a, anchor))
                {
                    anchorLastSeen = TimingEngine.Now;
                }
                else
                {
                    // Gone, or only hidden? Something flying in front of the marker (a satellite over
                    // the score label) hides it while the game carries on underneath. When the watch
                    // can see its game screen is still there, unchanged and not moving, the marker is
                    // only covered: keep going for a while. When the game has really ended, the screen
                    // changes too, and the watch ends as quickly as before.
                    double goneMs = TimingEngine.TicksToMs(TimingEngine.Now - anchorLastSeen);
                    if ((goneMs >= AnchorGraceMs && !gameStillShowing) || goneMs >= AnchorCoveredMs)
                    {
                        _t = TimingEngine.Now; // the marker has gone: that screen is over
                        RecordWatchEnd(a);
                        return true;
                    }
                }
            }

            var (sx, sy) = _anchorShift.GetValueOrDefault(a);
            var (left, top) = Resolve(a.X + sx, a.Y + sy); // re-resolved every pass: the target window may move
            long lookedAt = TimingEngine.Now;
            bool captured = _screen.TryCapture(left, top, a.Width, a.Height, ref _areaPixels);
            bool record = captured && _debugFolder is not null && _debugCount < MaxDebugSnapshots && TimingEngine.Now >= _debugNextAt;
            detector.CollectReport = record;

            // Spots clicked within the last "leave it alone" period are passed over.
            long forget = TimingEngine.Now - TimingEngine.MsToTicks(a.RetargetDelayMs);
            _recentAreaClicks.RemoveAll(c => c.At < forget);
            _avoid.Clear();
            foreach (var c in _recentAreaClicks) _avoid.Add((c.X, c.Y));

            var found = captured ? detector.Find(_areaPixels, -1, _avoid) : null;
            gameStillShowing = captured && detector.KnowsItsScreen && !detector.IsScreenChanged;
            // A spot just clicked stays off-limits for as long as something is still sitting right
            // there: a destroyed target's explosion can outlast "leave that spot alone", and a click
            // on it is wasted (and may count as a miss). A missed target moves off and is free again.
            if (captured && _recentAreaClicks.Count > 0)
            {
                var offers = detector.LastOffers;
                for (int i = 0; i < _recentAreaClicks.Count; i++)
                {
                    var c = _recentAreaClicks[i];
                    foreach (var (ox, oy) in offers)
                    {
                        if (Math.Abs(ox - c.X) > LingerPx || Math.Abs(oy - c.Y) > LingerPx) continue;
                        _recentAreaClicks[i] = (c.X, c.Y, TimingEngine.Now);
                        break;
                    }
                }
            }
            if (captured && _debugFolder is not null)
            {
                double lookMs = TimingEngine.TicksToMs(lookedAt - _debugStartedAt);
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (_debugLooks.Length < 2_000_000)
                {
                    _debugLooks.Append(lookMs.ToString("0", inv));
                    foreach (var (ox, oy) in detector.LastOffers) _debugLooks.Append($" {a.X + sx + ox},{a.Y + sy + oy}");
                    _debugLooks.Append(Environment.NewLine);
                }
                if (_debugScreen.Length < 2_000_000)
                    _debugScreen.Append(string.Format(inv, "{0,7:0} ms  differs from scene {1,6:0.0%}  changed since last look {2,6:0.0%}  detail {3:0.000}{4}{5}",
                        lookMs, detector.LastChangedShare, detector.LastMovingShare, detector.LastDetail,
                        detector.IsScreenChanged ? "  DIFFERENT SCREEN" : "", found is var (fx, fy, _) ? $"  target ({a.X + sx + fx}, {a.Y + sy + fy})" : "")).Append(Environment.NewLine);
                // The last moment of the game, at full speed but a third of the size, saved when the watch ends.
                const int shrink = 3;
                int sw = a.Width / shrink, sh = a.Height / shrink;
                var small = _recentLooks.Count >= RecentLooksKept ? _recentLooks.Dequeue().Pixels : new int[sw * sh];
                if (small.Length != sw * sh) small = new int[sw * sh];
                for (int row = 0; row < sh; row++)
                for (int col = 0; col < sw; col++)
                    small[row * sw + col] = _areaPixels[row * shrink * a.Width + col * shrink];
                _recentLooks.Enqueue((lookMs, small, sw, sh));
            }
            _differentScreen = captured && detector.IsScreenChanged;
            if (record) SaveDebugSnapshot(a, detector);
            if (captured && _closeUpsDue.Count > 0)
            {
                for (int i = _closeUpsDue.Count - 1; i >= 0; i--)
                {
                    var c = _closeUpsDue[i];
                    if (TimingEngine.Now < c.Due) continue;
                    SaveCloseUp($"click-{c.N:000}-b.png", _areaPixels, a.Width, a.Height, c.SeenX, c.SeenY, c.ClickX, c.ClickY);
                    _closeUpsDue.RemoveAt(i);
                }
            }
            if (found is var (x, y, rule))
            {
                // The marker is looked at again immediately before every click. When the game's
                // screen closes, whatever replaces it (an upgrade shop, say) can contain things
                // that look like targets, and a single stray click there can cost something.
                // So: no marker, no click, with no grace period. Only ENDING the watch waits a
                // moment, so that something drifting across the marker does not end it early.
                if (anchor is not null)
                {
                    // A covered marker does not stop the click while this very look found the game
                    // screen intact (see the ending rule above); anything else does.
                    bool covered = gameStillShowing && TimingEngine.TicksToMs(TimingEngine.Now - anchorLastSeen) < AnchorCoveredMs;
                    if (!AnchorShowing(a, anchor, search: false) && !covered)
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
                int seenX = x, seenY = y;
                if (a.LeadMovingTargets) (x, y) = AimAhead(a, x, y, detector.TargetVelocity, lookedAt);
                // DoClick takes profile-space coordinates and resolves them itself.
                if (!DoClick(false, a.X + sx + x, a.Y + sy + y, a.Rules[rule].Button, ClickKind.Single, a.HoldMs, 0)) return false;
                detector.NotifyClicked(seenX, seenY);
                if (a.RetargetDelayMs > 0) _recentAreaClicks.Add((x, y, TimingEngine.Now));
                if (_debugFolder is not null && _debugClicks.Length < 20_000)
                {
                    double at = TimingEngine.TicksToMs(TimingEngine.Now - _debugStartedAt) / 1000;
                    var (vx, vy) = detector.TargetVelocity;
                    string aim = (x, y) == (seenX, seenY)
                        ? (vx, vy) == (0, 0) ? "" : "  (moving, not aimed ahead)"
                        : $"  (seen at ({a.X + sx + seenX}, {a.Y + sy + seenY}), moving {Math.Sqrt(vx * vx + vy * vy):0} px/s, aimed {x - seenX:+0;-0}, {y - seenY:+0;-0} ahead)";
                    string closeUp = "";
                    if (_debugCloseUps < MaxCloseUps)
                    {
                        // What was there when it was seen, and a moment after the click: did it die or fly on?
                        int n = ++_debugCloseUps;
                        SaveCloseUp($"click-{n:000}-a.png", _areaPixels, a.Width, a.Height, seenX, seenY, x, y);
                        _closeUpsDue.Add((n, seenX, seenY, x, y, TimingEngine.Now + TimingEngine.MsToTicks(CloseUpAfterMs)));
                        closeUp = $"  [click-{n:000}]";
                    }
                    _debugClicks.Append($"  {at:0.00} s  {a.Rules[rule].Button.ToString().ToLowerInvariant()}-click at ({a.X + sx + x}, {a.Y + sy + y}) for rule {rule + 1}{aim}{closeUp}{Environment.NewLine}");
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

    /// <summary>
    /// The screen shown to the capture is already a frame old, the capture and the search take a
    /// little while, and the game only acts on the click at its next frame. All that time the
    /// target keeps moving, so aim where it will be by then.
    /// </summary>
    private static (int X, int Y) AimAhead(AreaWatchAction a, int x, int y, (double X, double Y) velocity, long lookedAt)
    {
        const double FrameLagMs = 16;   // about one frame at 60 fps, before and after
        const double MaxLeadPx = 90;    // a guess that would move the click further than this is not trusted
        if (velocity == (0, 0)) return (x, y);
        double leadMs = TimingEngine.TicksToMs(TimingEngine.Now - lookedAt) + FrameLagMs;
        double dx = velocity.X * leadMs / 1000, dy = velocity.Y * leadMs / 1000;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length > MaxLeadPx) (dx, dy) = (dx * MaxLeadPx / length, dy * MaxLeadPx / length);
        return (Math.Clamp(x + (int)Math.Round(dx), 0, a.Width - 1), Math.Clamp(y + (int)Math.Round(dy), 0, a.Height - 1));
    }

    private enum MarkerWait { Appeared, TimedOut, Stopped }

    /// <summary>Waits for a start/stop marker to appear (status: WAITING FOR MARKER).</summary>
    private MarkerWait WaitForMarker(IHasMarker a, int[] anchor, long appearBy, int pollMs)
    {
        _waitingForMarker = true;
        long waitStarted = TimingEngine.Now, nextLog = waitStarted + TimingEngine.MsToTicks(10_000);
        int best = 0;
        try
        {
            while (!AnchorShowing(a, anchor))
            {
                best = Math.Max(best, _markerMatch);
                if (TimingEngine.Now >= nextLog)
                {
                    // Visible in the log without the recorder: a marker that never comes is the
                    // usual reason a watch "does nothing", and the number says how far off it is.
                    nextLog = TimingEngine.Now + TimingEngine.MsToTicks(10_000);
                    Log.Info($"Still waiting for the start/stop marker after {TimingEngine.TicksToMs(TimingEngine.Now - waitStarted) / 1000:0} s: " +
                             $"{_markerMatch}% alike now, best {best}%, needs {a.AnchorMatchPercent}%.");
                }
                if (_debugFolder is not null) SaveMarkerSnapshot(a);
                if (TimingEngine.Now >= appearBy) return MarkerWait.TimedOut;
                _t = TimingEngine.Now;
                if (!Advance(Math.Max(pollMs, 50), scaled: false)) return MarkerWait.Stopped;
                if (!Housekeeping()) return MarkerWait.Stopped;
            }
            return MarkerWait.Appeared;
        }
        finally
        {
            _waitingForMarker = false;
        }
    }

    // ------------------------------------------------------------------ steering a bar to block things

    private int _releasedAll; // counts the times Housekeeping let go of every key, so a holder knows its key is up
    private const double UnseenBarMs = 1500;   // how long to steer by reckoning alone while the bar cannot be seen
    private const double DriftMarginDegrees = 2; // drifting while covering one leaves it at least this far inside the bar's span
    private const double LastMomentSeconds = 0.3; // this close to arriving, a target the bar already covers is left alone

    /// <summary>
    /// Steers a bar round its circle to block what flies out from the centre (see <see cref="BarBlockAction"/>).
    /// Holds one of two keys at a time and lets go whenever it is lined up, unsure, or not allowed to act.
    /// </summary>
    private bool DoBarBlock(BarBlockAction a)
    {
        if (!_barDetectors.TryGetValue(a, out var detector)) _barDetectors[a] = detector = new BarBlockDetector(a);
        Interlocked.Exchange(ref _nextDue, 0);

        int[]? anchor = GetAnchor(a);
        if (anchor is not null && WaitForMarker(a, anchor, long.MaxValue, a.PollMs) != MarkerWait.Appeared) return false;

        long started = TimingEngine.Now, anchorLastSeen = started;
        long watchUntil = a.WatchForMs > 0 ? started + TimingEngine.MsToTicks(a.WatchForMs) : 0;
        var (bx, by, size) = a.LookBox;
        int[] pixels = [];

        // Troubleshooting recorder: every look written down, and a picture of the circle each second.
        var steerLog = _debugFolder is not null ? new StringBuilder() : null;
        int steerPictures = 0;
        long nextSteerPicture = 0;
        void Note(string line)
        {
            if (steerLog is null || steerLog.Length > 2_000_000) return;
            steerLog.Append($"{TimingEngine.TicksToMs(TimingEngine.Now - started),7:0} ms  {line}{Environment.NewLine}");
        }
        void Picture(string name)
        {
            if (_debugFolder is not { } folder || pixels.Length < size * size || !BeginDebugFiles(folder)) return;
            var copy = (int[])pixels.Clone();
            Task.Run(() =>
            {
                try { TemplateImage.SavePng(Path.Combine(folder, name), copy, size, size); }
                catch (Exception ex) { Log.Error("Could not save a steering picture", ex); }
            });
        }
        if (steerLog is not null)
            Note($"Block with a bar: looking at ({bx}, {by}), {size} x {size}; centre ({a.CentreX}, {a.CentreY}), circle {a.Radius} px, {a.BarCount} bar{(a.BarCount == 1 ? "" : "s")}, bar colour {a.BarColor} +-{a.BarTolerance}; " +
                 $"blocking {string.Join(", ", a.Targets.Select(t => $"{t.Color} +-{t.Tolerance}"))}. Angles: 0 = right, 90 = down (clockwise).");

        // What the action learns about the game at the start: which key turns the bar which way,
        // how fast, and how far behind the game the captured picture is.
        bool calibrated = false;
        double degreesPerSecond = 180, pictureLagMs = 30;
        double? lastBar = null;          // the bar's angle when last seen whole
        double usualBarPixels = 0;       // how many of its pixels show when nothing covers it
        BarThreat? chasing = null;       // what the bar is being steered towards
        long lastBarSeenAt = 0;
        KeyCombo increaseKey = a.ClockwiseKey, decreaseKey = a.AnticlockwiseKey; // screen angles grow clockwise
        double loopMs = a.PollMs;   // how long one look-and-decide takes, on average
        long previousLook = 0;

        // Every key press made, so the turning the picture does not show yet can be added on.
        var presses = new List<(long Down, long Up, int Way)>();
        KeyCombo? held = null;
        int heldWay = 0, releases = _releasedAll;

        // +1 = hold the key that grows the angle, -1 = the other one, 0 = neither.
        void Hold(int way)
        {
            long now = TimingEngine.Now;
            if (releases != _releasedAll) // a pause let go of everything
            {
                if (presses.Count > 0 && presses[^1].Up == long.MaxValue) presses[^1] = presses[^1] with { Up = now };
                (held, heldWay, releases) = (null, 0, _releasedAll);
            }
            if (heldWay == way) return;
            if (held is { } old)
            {
                _input.KeyComboUp(old);
                presses[^1] = presses[^1] with { Up = now };
            }
            held = way == 0 ? null : way > 0 ? increaseKey : decreaseKey;
            heldWay = way;
            if (held is { } key)
            {
                _input.KeyComboDown(key);
                presses.Add((now, long.MaxValue, way));
            }
            presses.RemoveAll(k => k.Up != long.MaxValue && now - k.Up > TimingEngine.MsToTicks(2000));
        }

        // Degrees the bar has been told to turn since a moment in time (positive = growing angle).
        double TurnedSince(long fromTicks)
        {
            long now = TimingEngine.Now;
            double total = 0;
            foreach (var (down, up, way) in presses)
            {
                long from = Math.Max(down, fromTicks), to = Math.Min(up, now);
                if (to > from) total += way * TimingEngine.TicksToMs(to - from) / 1000 * degreesPerSecond;
            }
            return total;
        }

        bool Look(out double? bar)
        {
            bar = null;
            var (left, top) = Resolve(bx, by);
            long at = TimingEngine.Now;
            if (!_screen.TryCapture(left, top, size, size, ref pixels)) return false;
            detector.Look(pixels, TimingEngine.TicksToMs(at - started));
            bar = detector.BarAngle;
            return true;
        }

        // Holds the "clockwise" key briefly while watching the bar: which way it really turns it,
        // how fast, how long the picture takes to show it moving, and, from the two places it was
        // seen, which patches of its colour on the circle are only backdrop (they stayed put).
        bool Calibrate()
        {
            const double PressMs = 200, WatchMs = 350;
            int[] before = detector.RawBins;
            bool[] beforeMatch = detector.MatchSnapshot;
            var samples = new List<(double Ms, int[] Bins)>();
            _input.KeyComboDown(a.ClockwiseKey);
            long down = TimingEngine.Now;
            bool released = false;
            try
            {
                while (TimingEngine.TicksToMs(TimingEngine.Now - down) < WatchMs)
                {
                    if (!released && TimingEngine.TicksToMs(TimingEngine.Now - down) >= PressMs)
                    {
                        _input.KeyComboUp(a.ClockwiseKey);
                        released = true;
                    }
                    long at = TimingEngine.Now;
                    if (Look(out _)) samples.Add((TimingEngine.TicksToMs(at - down), detector.RawBins));
                    _t = TimingEngine.Now;
                    if (!Advance(5, scaled: false)) return false;
                }
            }
            finally
            {
                if (!released) _input.KeyComboUp(a.ClockwiseKey);
            }
            if (samples.Count == 0) return true;

            // Where the colour left from and where it arrived: the bar's old and new places.
            int[] after = samples[^1].Bins;
            var (left, leftCount) = Shift(before, after);
            var (arrived, arrivedCount) = Shift(after, before);
            Note($"calibration: held {a.ClockwiseKey.Display} {PressMs} ms; {samples.Count} looks; colour left {leftCount} px around {left:0} deg, arrived {arrivedCount} px around {arrived:0} deg; " +
                 $"patches on the circle {detector.Patches}; before {Bins(before)}; after {Bins(after)}");
            Picture("steer-calibrated.png");
            if (leftCount < BarBlockDetector.MinBarPixels || arrivedCount < BarBlockDetector.MinBarPixels)
            {
                Log.Info($"Block: {a.ClockwiseKey.Display} did not visibly move the bar while it was held; assuming it turns clockwise, at about {degreesPerSecond:0} degrees a second." +
                         (detector.Patches > 1 ? $" There are {detector.Patches} patches of the bar's colour on its circle, so it may be steering a patch of backdrop: check the bar's colour." : ""));
                return true;
            }
            double turned = BarBlockDetector.Wrap(arrived - left);
            if (turned < 0) (increaseKey, decreaseKey) = (a.AnticlockwiseKey, a.ClockwiseKey);
            degreesPerSecond = Math.Clamp(Math.Abs(turned) / (PressMs / 1000), 20, 3000);
            // When the picture first showed the colour leaving its old place.
            int firstMoved = samples.FindIndex(p => Shift(before, p.Bins).Count >= leftCount * 0.25);
            if (firstMoved >= 0) pictureLagMs = Math.Clamp(samples[firstMoved].Ms - 0.25 * 10 / degreesPerSecond * 1000, 0, 200);
            bool learned = Math.Abs(turned) >= 12; // moved clear of where it was: what stayed is backdrop
            if (learned) detector.LearnBackdrop(beforeMatch, detector.MatchSnapshot);
            Log.Info($"Block: {a.ClockwiseKey.Display} turns the bar {(turned > 0 ? "clockwise" : "anticlockwise")} at about {degreesPerSecond:0} degrees a second; " +
                     $"the picture shows it about {pictureLagMs:0} ms after the key goes down." +
                     (detector.Patches > 1 ? learned
                         ? $" {detector.Patches - 1} patch{(detector.Patches == 2 ? "" : "es")} of the bar's colour that stayed put will be ignored as backdrop."
                         : " Other patches of the bar's colour are on its circle, and it did not move far enough to tell them apart." : ""));
            return true;
        }

        // Where the bar's colour is round the circle, in 10-degree steps, for the recorder.
        static string Bins(int[] bins) => string.Join(" ", Enumerable.Range(0, 36)
            .Select(k => (k, n: bins.Skip(k * 10).Take(10).Sum())).Where(t => t.n > 0).Select(t => $"{t.k * 10}:{t.n}"));

        // Where some colour was in "from" but not in "to": its mean angle and how much of it.
        static (double Angle, int Count) Shift(int[] from, int[] to)
        {
            double sx = 0, sy = 0;
            int count = 0;
            for (int i = 0; i < 360; i++)
            {
                int d = from[i] - to[i] - BarBlockDetector.BackdropSlack;
                if (d <= 0) continue;
                count += d;
                double r = (i + 0.5) * Math.PI / 180;
                sx += d * Math.Cos(r);
                sy += d * Math.Sin(r);
            }
            return (count == 0 ? 0 : (Math.Atan2(sy, sx) * 180 / Math.PI + 360) % 360, count);
        }

        try
        {
            while (true)
            {
                if (watchUntil != 0 && TimingEngine.Now >= watchUntil) break;

                // No marker, no steering, straight away; and once it has been gone a moment, the game is over.
                if (anchor is not null)
                {
                    if (AnchorShowing(a, anchor)) anchorLastSeen = TimingEngine.Now;
                    else
                    {
                        Hold(0);
                        if (TimingEngine.TicksToMs(TimingEngine.Now - anchorLastSeen) >= AnchorGraceMs) break;
                        _t = TimingEngine.Now;
                        if (!Advance(a.PollMs, scaled: false)) return false;
                        if (!Housekeeping()) return false;
                        continue;
                    }
                }

                long lookedAt = TimingEngine.Now;
                if (previousLook != 0) loopMs += (TimingEngine.TicksToMs(lookedAt - previousLook) - loopMs) * 0.2;
                previousLook = lookedAt;
                bool looked = Look(out var seen);
                if (looked && steerLog is not null)
                {
                    if (TimingEngine.Now >= nextSteerPicture && steerPictures < 40)
                    {
                        nextSteerPicture = TimingEngine.Now + TimingEngine.MsToTicks(1000);
                        Picture($"steer-{++steerPictures:00}.png");
                    }
                    string threats = string.Join("; ", detector.Threats.Select(t => $"kind {t.Target + 1} at {t.Angle:0} deg, {t.Distance:0} px out, crosses in {t.SecondsToEscape:0.00} s"));
                    Note($"bar {(seen is double sb ? $"{sb:0.0} deg ({detector.BarPixels} px, {detector.BarWidthDegrees:0} deg wide)" : "NOT FOUND")}, patches {detector.Patches}, holding {(heldWay > 0 ? increaseKey.Display : heldWay < 0 ? decreaseKey.Display : "nothing")}{(detector.Learning ? ", still learning the backdrop" : "")}; " +
                         (threats.Length > 0 ? "flying out: " + threats : "nothing flying out"));
                }
                // Where the bar is now: as seen (a moment ago) plus the turning asked for since. If it
                // cannot be seen (passing over backdrop of its own colour, say), from where it was last
                // seen plus every turn asked for since, for a while: steering on is how it gets clear.
                double? estimate = null;
                // A bar only partly in view (something flying over it, often the very thing it is
                // about to block) seems to sit wherever its visible part is. Don't trust that: reckon.
                bool wholeBar = detector.BarPixels >= usualBarPixels * 0.6;
                if (looked && seen is not null && detector.BarPixels > 0)
                    usualBarPixels = detector.BarPixels > usualBarPixels ? detector.BarPixels : usualBarPixels + (detector.BarPixels - usualBarPixels) * 0.01;
                if (looked && seen is double bar && (wholeBar || !calibrated || lastBar is null))
                {
                    (lastBar, lastBarSeenAt) = (bar, lookedAt);
                    estimate = bar + TurnedSince(lookedAt - TimingEngine.MsToTicks(pictureLagMs));
                }
                else if (calibrated && lastBar is double last && TimingEngine.TicksToMs(lookedAt - lastBarSeenAt) < UnseenBarMs)
                {
                    estimate = last + TurnedSince(lastBarSeenAt - TimingEngine.MsToTicks(pictureLagMs));
                }
                detector.ExpectedAngle = estimate is double e ? (e % 360 + 360) % 360 : null;

                if (estimate is not double barNow)
                {
                    Hold(0); // cannot see the bar: do nothing rather than guess
                }
                else if (!calibrated)
                {
                    calibrated = true;
                    if (!Calibrate()) return false;
                }
                else
                {
                    chasing = detector.Choose(degreesPerSecond, (barNow % 360 + 360) % 360, chasing,
                        restartSeconds: (pictureLagMs + Math.Max(loopMs, a.PollMs)) / 1000);
                    if (chasing is not { } threat)
                    {
                        Hold(0); // nothing coming: stay put
                    }
                    else
                    {
                        double error = BarBlockDetector.ToNearestBar(threat.Angle, barNow, a.BarCount);
                        Note($"  steering for kind {threat.Target + 1} at {threat.Angle:0} deg; bar now about {barNow:0} deg; {error:+0;-0} deg to go");
                        int way = Math.Sign(error);
                        // Close enough once it is safely within the bar's span: a long bar need not
                        // be centred on it. When moving, aim for halfway between there and dead centre.
                        double within = Math.Max(a.AimToleranceDegrees, detector.BarWidthDegrees / 2 - BarBlockDetector.EdgeMarginDegrees);
                        double distance = Math.Abs(error) - within / 2;
                        if (Math.Abs(error) <= within)
                        {
                            Hold(0);
                            // Covered. If the plan has another to reach next, drift towards it while
                            // waiting, keeping this one inside the bar's span: a head start on the next.
                            // Not in its last moment unless the whole bar is in view: a part-hidden bar's
                            // place is reckoned, and only a sure place is safe to edge away from.
                            if (detector.PlannedNext is { } next && (threat.SecondsToEscape > LastMomentSeconds || wholeBar))
                            {
                                int toNext = Math.Sign(BarBlockDetector.ToNearestBar(next.Angle, barNow, a.BarCount));
                                double keep = Math.Max(0, within - DriftMarginDegrees);
                                double drift = keep + toNext * error; // degrees the bar can move toNext and still cover it
                                if (toNext != 0 && drift >= 2)
                                {
                                    Hold(toNext);
                                    _t = TimingEngine.Now;
                                    bool ok = Advance(drift / degreesPerSecond * 1000, scaled: false);
                                    Hold(0);
                                    if (!ok) return false;
                                }
                            }
                        }
                        else if (threat.SecondsToEscape < LastMomentSeconds && Math.Abs(error) <= within + BarBlockDetector.EdgeMarginDegrees)
                        {
                            Hold(0); // it is arriving now and the bar is covering it: no last-moment corrections
                        }
                        else if (distance > degreesPerSecond * Math.Max(loopMs, a.PollMs) / 1000)
                        {
                            Hold(way); // a long way to go: keep the key down until the next look
                        }
                        else
                        {
                            // Nearly there: a tap of just the right length, instead of holding on until
                            // the next look and sailing past.
                            Hold(0);
                            double tapMs = distance / degreesPerSecond * 1000;
                            if (tapMs >= 3)
                            {
                                Hold(way);
                                _t = TimingEngine.Now;
                                bool ok = Advance(tapMs, scaled: false);
                                Hold(0);
                                if (!ok) return false;
                            }
                        }
                    }
                }

                _t = TimingEngine.Now;
                if (!Advance(a.PollMs, scaled: false)) return false;
                if (!Housekeeping()) return false; // a pause or lost focus lets go of every key
            }
        }
        finally
        {
            Hold(0);
            if (steerLog is { Length: > 0 } && _debugFolder is { } folder && BeginDebugFiles(folder))
            {
                string text = steerLog.ToString();
                Task.Run(() =>
                {
                    try { lock (DebugFileLock) File.AppendAllText(Path.Combine(folder, "steer.txt"), text); }
                    catch (Exception ex) { Log.Error("Could not write the steering log", ex); }
                });
            }
        }
        _t = TimingEngine.Now;
        return true;
    }

    /// <summary>The action's start/stop marker as pixels, decoded once per run. Null if it has none.</summary>
    private int[]? GetAnchor(IHasMarker a)
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

    /// <param name="search">
    /// Also look around the snipped spot if the marker is not where it was last seen. Off for the
    /// check right before a click, which must be quick; the regular checks do the searching.
    /// </param>
    private bool AnchorShowing(IHasMarker a, int[] anchor, bool search = true)
    {
        var (sx, sy) = _anchorShift.GetValueOrDefault(a);
        var (x, y) = Resolve(a.AnchorX + sx, a.AnchorY + sy);
        if (!_screen.TryCapture(x, y, a.AnchorWidth, a.AnchorHeight, ref _anchorPixels))
        {
            _markerMatch = 0;
            return false;
        }
        double alike = ScreenAnchor.Similarity(_anchorPixels, anchor) * 100;
        _markerMatch = (int)alike;
        if (alike >= a.AnchorMatchPercent) return true;
        if (!search) return false;

        // Not where it was last seen. If the game window has been nudged since the marker was
        // snipped, the marker (and the whole game screen with it) is a little way off: look around.
        if (TimingEngine.Now < _anchorNextSearch.GetValueOrDefault(a)) return false;
        _anchorNextSearch[a] = TimingEngine.Now + TimingEngine.MsToTicks(AnchorSearchEveryMs);
        var (ox, oy) = Resolve(a.AnchorX - AnchorSearchPx, a.AnchorY - AnchorSearchPx);
        int rw = a.AnchorWidth + 2 * AnchorSearchPx, rh = a.AnchorHeight + 2 * AnchorSearchPx;
        if (!_screen.TryCapture(ox, oy, rw, rh, ref _anchorRegion)) return false;
        if (ScreenAnchor.FindNear(_anchorRegion, rw, rh, anchor, a.AnchorWidth, a.AnchorHeight, a.AnchorMatchPercent / 100.0) is not var (fx, fy, share))
            return false;

        var shift = (fx - AnchorSearchPx, fy - AnchorSearchPx);
        _markerMatch = (int)(share * 100);
        if (shift == (sx, sy)) return true; // where it was all along: it had just not quite appeared at the first look
        _anchorShift[a] = shift;
        Log.Info($"Found the start/stop marker {Math.Abs(shift.Item1)} px {(shift.Item1 < 0 ? "left" : "right")} and {Math.Abs(shift.Item2)} px {(shift.Item2 < 0 ? "up" : "down")} " +
                 $"from where it was snipped ({share:P0} alike). The game window has probably moved; the watched area is moved with it for this run.");
        return true;
    }

    /// <summary>
    /// While a recorded run waits for its marker: about once a second, a picture of the marker's
    /// spot with a border around it, and how alike it was. Without these, a marker that never
    /// matches leaves an empty recording.
    /// </summary>
    private void SaveMarkerSnapshot(IHasMarker a)
    {
        const int border = 120, maxSnapshots = 30;
        if (_markerSnapshots >= maxSnapshots || TimingEngine.Now < _markerSnapshotNextAt) return;
        _markerSnapshotNextAt = TimingEngine.Now + TimingEngine.MsToTicks(1000);
        string folder = _debugFolder!;
        if (!BeginDebugFiles(folder)) return;
        int number = ++_markerSnapshots;

        var (shx, shy) = _anchorShift.GetValueOrDefault(a);
        var (mx, my) = Resolve(a.AnchorX + shx, a.AnchorY + shy);
        var (vx, vy, vw, vh) = ScreenService.VirtualScreen;
        int x0 = Math.Max(vx, mx - border), y0 = Math.Max(vy, my - border);
        int x1 = Math.Min(vx + vw, mx + a.AnchorWidth + border), y1 = Math.Min(vy + vh, my + a.AnchorHeight + border);
        int[] pixels = [];
        if (x1 - x0 < 1 || y1 - y0 < 1 || !_screen.TryCapture(x0, y0, x1 - x0, y1 - y0, ref pixels)) return;
        int width = x1 - x0, height = y1 - y0;
        double seconds = TimingEngine.TicksToMs(TimingEngine.Now - _debugStartedAt) / 1000;
        string text = $"{Environment.NewLine}marker-{number:00}.png at {seconds:0.0} s: waiting for the start/stop marker. The spot it should be in was {_markerMatch}% alike (needs {a.AnchorMatchPercent}%). " +
                      $"The picture shows the screen from ({x0}, {y0}), {width} x {height}; the marker belongs at ({mx}, {my}), {a.AnchorWidth} x {a.AnchorHeight}.{Environment.NewLine}";
        Task.Run(() =>
        {
            try
            {
                TemplateImage.SavePng(Path.Combine(folder, $"marker-{number:00}.png"), pixels, width, height);
                lock (DebugFileLock) File.AppendAllText(Path.Combine(folder, "report.txt"), text);
            }
            catch (Exception ex)
            {
                Log.Error("Could not save a marker snapshot", ex);
            }
        });
    }

    /// <summary>
    /// A small picture centred on where a target was seen, with the click marked by a magenta cross,
    /// written in the background.
    /// </summary>
    private void SaveCloseUp(string name, int[] frame, int width, int height, int cx, int cy, int markX, int markY)
    {
        if (_debugFolder is not { } folder || !BeginDebugFiles(folder)) return;
        const int r = CloseUpRadius, size = 2 * r;
        var pixels = new int[size * size];
        for (int y = 0; y < size; y++)
        {
            int fy = cy - r + y;
            if (fy < 0 || fy >= height) continue;
            for (int x = 0; x < size; x++)
            {
                int fx = cx - r + x;
                if (fx >= 0 && fx < width) pixels[y * size + x] = frame[fy * width + fx];
            }
        }
        int mx = markX - cx + r, my = markY - cy + r;
        for (int d = -6; d <= 6; d++)
        {
            if (mx + d >= 0 && mx + d < size && my >= 0 && my < size) pixels[my * size + mx + d] = 0xFF00FF;
            if (my + d >= 0 && my + d < size && mx >= 0 && mx < size) pixels[(my + d) * size + mx] = 0xFF00FF;
        }
        Task.Run(() =>
        {
            try
            {
                TemplateImage.SavePng(Path.Combine(folder, name), pixels, size, size);
            }
            catch (Exception ex)
            {
                Log.Error("Could not save a click close-up", ex);
            }
        });
    }

    /// <summary>
    /// The first time a run writes anything, the previous run's recording makes way. Done lazily so
    /// a run that never reaches an area watch does not wipe the last useful recording.
    /// </summary>
    private bool BeginDebugFiles(string folder)
    {
        if (_debugFilesStarted) return true;
        _debugFilesStarted = true;
        try
        {
            foreach (string old in Directory.EnumerateFiles(folder, "*.png")) File.Delete(old);
            File.Delete(Path.Combine(folder, "looks.txt"));
            File.Delete(Path.Combine(folder, "screen.txt"));
            File.Delete(Path.Combine(folder, "steer.txt"));
            File.WriteAllText(Path.Combine(folder, "report.txt"),
                $"Area watch troubleshooting run, started {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
            Log.Info($"Area watch troubleshooting snapshots are being saved to {folder}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not clear the previous troubleshooting snapshots", ex);
            return true; // carry on: new files simply sit beside the old ones
        }
    }

    private void StartDebugRecording()
    {
        _debugFolder = null;
        _debugRun++;
        _debugCount = 0;
        _debugFilesStarted = false;
        _debugLooks.Clear();
        _debugScreen.Clear();
        _recentLooks.Clear();
        _debugCloseUps = 0;
        _closeUpsDue.Clear();
        _markerSnapshots = 0;
        _markerSnapshotNextAt = 0;
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
        BeginDebugFiles(folder);
        if (number == 4) SaveSurroundings(a, folder, "surroundings.png");
        else if (number % 5 == 4) SaveSurroundings(a, folder, $"surroundings-{number:00}.png");
        var pixels = (int[])_areaPixels.Clone();
        int width = a.Width, height = a.Height;
        double seconds = TimingEngine.TicksToMs(TimingEngine.Now - _debugStartedAt) / 1000;
        string text = $"{Environment.NewLine}===== frame-{number:00}.png  at {seconds:0.0} s  (area {width} x {height} at {a.X}, {a.Y}; clicks so far {Interlocked.Read(ref _clicks)}){Environment.NewLine}"
                      + detector.DescribeLastReport() + Environment.NewLine
                      + (_debugClicks.Length > 0 ? $"Clicks since the previous frame:{Environment.NewLine}{_debugClicks}" : "");
        _debugClicks.Clear();
        string looks = _debugLooks.ToString();
        _debugLooks.Clear();
        Task.Run(() =>
        {
            try
            {
                TemplateImage.SavePng(Path.Combine(folder, $"frame-{number:00}.png"), pixels, width, height);
                lock (DebugFileLock)
                {
                    File.AppendAllText(Path.Combine(folder, "report.txt"), text);
                    File.AppendAllText(Path.Combine(folder, "looks.txt"), looks);
                }
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
        // The last moment before the end, look by look, and whatever was logged since the last frame.
        var lastLooks = _recentLooks.ToList();
        _recentLooks.Clear();
        string looksTail = _debugLooks.ToString(), screenText = _debugScreen.ToString();
        _debugLooks.Clear();
        _debugScreen.Clear();
        Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < lastLooks.Count; i++)
                {
                    var (ms, px, w, h) = lastLooks[i];
                    TemplateImage.SavePng(Path.Combine(folder, $"end-{i + 1:00}-{ms:0}ms.png"), px, w, h);
                }
                lock (DebugFileLock)
                {
                    File.AppendAllText(Path.Combine(folder, "looks.txt"), looksTail);
                    File.AppendAllText(Path.Combine(folder, "screen.txt"), screenText);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not save the end of the watch", ex);
            }
        });
        // Clicks made after the last saved frame are otherwise never written down.
        if (_debugClicks.Length > 0)
        {
            string tail = $"{Environment.NewLine}Clicks after the last frame, up to the end of the watch:{Environment.NewLine}{_debugClicks}";
            _debugClicks.Clear();
            Task.Run(() =>
            {
                try
                {
                    lock (DebugFileLock) File.AppendAllText(Path.Combine(folder, "report.txt"), tail);
                }
                catch (Exception ex)
                {
                    Log.Error("Could not write the last clicks to the report", ex);
                }
            });
        }
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
