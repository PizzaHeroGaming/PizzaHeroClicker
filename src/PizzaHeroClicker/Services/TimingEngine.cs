using System.Diagnostics;
using System.Runtime.InteropServices;
using static PizzaHeroClicker.Native.NativeMethods;

namespace PizzaHeroClicker.Services;

/// <summary>
/// Precise, interruptible waiting for the click engine.
///
/// THE PROBLEM. Thread.Sleep and ordinary timers wake on the system timer tick, which is
/// 15.6 ms by default and 1 ms at best. A 1 ms click interval needs far better than that.
/// Spinning in a loop is perfectly accurate but burns a whole CPU core.
///
/// THE APPROACH: a hybrid wait toward an ABSOLUTE deadline.
///   1. Kernel sleep for most of the wait using a high-resolution waitable timer
///      (Windows 10 1803+). It costs no CPU, but it only wakes on a 0.5 ms grid, so a wake-up
///      lands anywhere from 0 to ~0.5 ms after the requested time (occasionally later).
///   2. Therefore ask to be woken early, by a "margin" that covers that oversleep.
///   3. Spin on the Stopwatch for the remaining fraction of a millisecond to land exactly
///      on the deadline.
///
/// The margin adapts to the machine. After every kernel sleep we measure how late Windows
/// actually woke us and keep running averages of the oversleep and of its spread;
/// margin = average + K * spread. A quiet machine gets a small margin (less spinning, less
/// CPU); a loaded one gets a larger margin (accuracy is preserved).
///
/// K is the accuracy / CPU trade-off:
///   Balanced (default): covers roughly 95% of wake-ups. At a 1 ms interval this uses about
///     a third of one core; the rare uncovered wake-up makes that click a fraction of a
///     millisecond late.
///   Precise: covers the tail too. At a 1 ms interval this is effectively a pure spin
///     (one full core) with microsecond-level accuracy.
///
/// Deadlines are Stopwatch timestamps, never "sleep for N ms", so errors do not accumulate:
/// a late wake-up shortens the following wait and the average rate stays exact.
///
/// One instance is used by one thread at a time (the engine thread).
/// </summary>
public sealed class TimingEngine : IDisposable
{
    public static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

    private const double BalancedK = 2.0;
    private const double PreciseK = 6.0;
    private const double SmoothingSamples = 32;        // running averages span about this many sleeps
    private static readonly double MaxSample = TicksPerMs * 2.0;   // one freak 10 ms stall must not poison the average
    private static readonly long MinMargin = MsToTicks(0.05);
    private static readonly long MaxMargin = MsToTicks(3.0);
    private static readonly long Cushion = MsToTicks(0.02);
    // Not worth a kernel transition for less than this; just spin.
    private static readonly long MinSleep = MsToTicks(0.10);

    private readonly IntPtr[] _handles = new IntPtr[2];
    private IntPtr _timer;
    private double _oversleepAvg;      // ticks
    private double _oversleepSpread;   // ticks, mean absolute deviation
    private long _margin;
    private int _periodRequests;
    private volatile bool _precise;

    /// <summary>True when the OS gave us a high-resolution timer (otherwise ~1 ms granularity).</summary>
    public bool IsHighResolution { get; }

    /// <summary>Trade CPU for accuracy; see the class remarks. May be changed at any time from any thread.</summary>
    public bool Precise
    {
        get => _precise;
        set => _precise = value;
    }

    public TimingEngine()
    {
        _timer = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        IsHighResolution = _timer != IntPtr.Zero;
        if (!IsHighResolution)
        {
            // Older Windows: fall back to a normal waitable timer, which follows the system tick.
            _timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TIMER_ALL_ACCESS);
        }

        // Starting guesses for a timer on a 0.5 ms grid (or a 1 ms grid for the fallback).
        double grid = TicksPerMs * (IsHighResolution ? 0.5 : 1.0);
        _oversleepAvg = grid / 2;
        _oversleepSpread = grid / 4;
        UpdateMargin();
    }

    public static long Now => Stopwatch.GetTimestamp();

    public static long MsToTicks(double ms) => (long)(ms * TicksPerMs);

    public static double TicksToMs(long ticks) => ticks / TicksPerMs;

    /// <summary>
    /// Opts the process out of Windows 11 power throttling. Without this, a process whose
    /// windows are minimised or covered (i.e. us, while a game is focused) gets its timer
    /// resolution requests ignored and its threads scheduled on efficiency cores.
    /// </summary>
    public static void DisablePowerThrottling()
    {
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = 1,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
                StateMask = 0, // controlled bits cleared = "never throttle these"
            };
            SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not disable power throttling: {ex.Message}"); // pre-Windows 10 1709
        }
    }

    /// <summary>
    /// Requests a 1 ms system tick for the duration of a run. The high-resolution timer does
    /// not need it, but the fallback timer and everything else in the process benefit.
    /// Calls are reference counted and must be paired with <see cref="EndHighResolution"/>.
    /// </summary>
    public void BeginHighResolution()
    {
        if (Interlocked.Increment(ref _periodRequests) == 1) timeBeginPeriod(1);
    }

    public void EndHighResolution()
    {
        if (Interlocked.Decrement(ref _periodRequests) == 0) timeEndPeriod(1);
    }

    /// <summary>
    /// Blocks until the Stopwatch timestamp <paramref name="deadline"/>, or until
    /// <paramref name="stopHandle"/> (a manual-reset event) is signalled.
    /// Returns false if the wait was interrupted by the stop signal.
    /// </summary>
    public bool WaitUntil(long deadline, IntPtr stopHandle)
    {
        while (true)
        {
            long now = Now;
            long remaining = deadline - now;
            if (remaining <= 0) return true;
            if (remaining < MinSleep || _timer == IntPtr.Zero) break; // too short to sleep at all

            long sleep = remaining - _margin;
            if (sleep < MinSleep)
            {
                // We would like to sleep but the margin forbids it. With no sleeps there are no
                // new measurements, so a margin inflated by a bad moment would stay inflated
                // (and keep a core spinning) forever. Let the spread relax a little instead.
                _oversleepSpread *= 1 - 1 / (SmoothingSamples * 2);
                UpdateMargin();
                break;
            }

            // Phase 1: kernel sleep. Negative due time = relative, in 100 ns units.
            long due = -(long)(sleep * (10_000_000.0 / Stopwatch.Frequency));
            if (due >= 0) break;
            if (!SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) break;

            _handles[0] = stopHandle; // index 0 wins if both are signalled, so a stop is never missed
            _handles[1] = _timer;
            uint result = WaitForMultipleObjects(2, _handles, false, INFINITE);
            if (result == WAIT_OBJECT_0) return false;
            if (result != WAIT_OBJECT_0 + 1) break; // WAIT_FAILED: degrade to spinning rather than hang

            // Phase 2: learn from how late the wake-up was.
            Learn(Now - (now + sleep));
            // Loop: normally the remainder is now smaller than the margin and we fall to the spin.
        }

        // Phase 3: spin to the deadline. At most a few milliseconds, so the stop signal is
        // not polled here; the caller checks it between waits.
        while (Now < deadline) Thread.SpinWait(8);
        return true;
    }

    private void Learn(long oversleep)
    {
        double sample = Math.Clamp(oversleep, 0, MaxSample);
        _oversleepAvg += (sample - _oversleepAvg) / SmoothingSamples;
        _oversleepSpread += (Math.Abs(sample - _oversleepAvg) - _oversleepSpread) / SmoothingSamples;
        UpdateMargin();
    }

    private void UpdateMargin()
    {
        double k = _precise ? PreciseK : BalancedK;
        _margin = Math.Clamp((long)(_oversleepAvg + k * _oversleepSpread) + Cushion, MinMargin, MaxMargin);
    }

    /// <summary>Current early-wake margin in ms (diagnostics and tests).</summary>
    public double MarginMs => TicksToMs(_margin);

    public void Dispose()
    {
        if (_timer != IntPtr.Zero)
        {
            CloseHandle(_timer);
            _timer = IntPtr.Zero;
        }
        while (Volatile.Read(ref _periodRequests) > 0) EndHighResolution();
    }
}
