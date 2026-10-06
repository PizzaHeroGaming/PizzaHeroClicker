using PizzaHeroClicker.Engine;
using PizzaHeroClicker.Models;
using PizzaHeroClicker.Services;
using Xunit;
using Xunit.Abstractions;

namespace PizzaHeroClicker.Tests;

// Timing tests measure the real clock, so they must not compete with each other for CPU.
[CollectionDefinition("Timing", DisableParallelization = true)]
public class TimingCollection;

[Collection("Timing")]
public class TimingEngineTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1.0, false)]
    [InlineData(1.0, true)]
    [InlineData(2.0, false)]
    [InlineData(5.0, false)]
    [InlineData(16.0, false)]
    public void HitsAbsoluteDeadlines(double intervalMs, bool precise)
    {
        using var timing = new TimingEngine { Precise = precise };
        using var stop = new ManualResetEvent(false);
        IntPtr handle = stop.SafeWaitHandle.DangerousGetHandle();
        timing.BeginHighResolution();

        int count = (int)Math.Clamp(1500 / intervalMs, 60, 1500);
        var late = new double[count];
        var cpuBefore = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
        long start = TimingEngine.Now, t = start;
        for (int i = 0; i < count; i++)
        {
            t += TimingEngine.MsToTicks(intervalMs);
            Assert.True(timing.WaitUntil(t, handle));
            late[i] = TimingEngine.TicksToMs(TimingEngine.Now - t);
        }
        double elapsed = TimingEngine.TicksToMs(TimingEngine.Now - start);
        double cpu = (System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds;
        timing.EndHighResolution();

        Array.Sort(late);
        output.WriteLine($"interval {intervalMs} ms x {count} ({(precise ? "precise" : "balanced")}): high-res={timing.IsHighResolution}, margin={timing.MarginMs:0.000} ms, " +
                         $"late median={late[count / 2]:0.0000} ms, p95={late[(int)(count * 0.95)]:0.0000} ms, p99={late[(int)(count * 0.99)]:0.0000} ms, max={late[^1]:0.0000} ms, " +
                         $"CPU={cpu / elapsed:P0} of one core");

        Assert.True(late[0] >= 0, "woke before the deadline");
        Assert.InRange(elapsed, count * intervalMs, count * intervalMs + 5); // no accumulated drift
        Assert.True(late[count / 2] < 0.1, $"median lateness {late[count / 2]} ms");
    }

    [Fact]
    public void StopSignalInterruptsALongWait()
    {
        using var timing = new TimingEngine();
        using var stop = new ManualResetEvent(false);
        var timer = new Timer(_ => stop.Set(), null, 50, Timeout.Infinite);
        long start = TimingEngine.Now;
        bool completed = timing.WaitUntil(start + TimingEngine.MsToTicks(10_000), stop.SafeWaitHandle.DangerousGetHandle());
        timer.Dispose();

        Assert.False(completed);
        Assert.InRange(TimingEngine.TicksToMs(TimingEngine.Now - start), 30, 500);
    }
}

[Collection("Timing")]
public class ClickEngineTests(ITestOutputHelper output)
{
    private readonly FakeInput _input = new();
    private readonly FakeScreen _screen = new();
    private readonly FakeWindows _windows = new();

    private (StopReason Reason, string? Error) Run(Profile profile, int timeoutMs = 10_000, Action<ClickEngine>? during = null)
    {
        using var timing = new TimingEngine();
        using var engine = new ClickEngine(_input, _screen, _windows, timing);
        using var finished = new ManualResetEventSlim();
        (StopReason, string?) result = default;
        engine.Stopped += (reason, error) => { result = (reason, error); finished.Set(); };

        Assert.True(engine.Start(profile));
        during?.Invoke(engine);
        Assert.True(finished.Wait(timeoutMs), "engine did not stop in time");
        return result;
    }

    private static Profile Plain(long intervalMs, long count) => new()
    {
        IntervalMs = intervalMs,
        StartDelayMs = 0,
        CornerStop = false,
        Repeat = { Mode = RepeatMode.Count, Count = count },
    };

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(10, 150)]
    public void PlainClickerKeepsItsInterval(int intervalMs, int count)
    {
        var (reason, error) = Run(Plain(intervalMs, count));
        Assert.Equal(StopReason.Completed, reason);
        Assert.Null(error);

        var clicks = _input.Snapshot().Where(e => e.Event.StartsWith("click")).Select(e => e.Ticks).ToList();
        Assert.Equal(count, clicks.Count);

        var gaps = clicks.Zip(clicks.Skip(1), (a, b) => TimingEngine.TicksToMs(b - a)).OrderBy(g => g).ToList();
        double total = TimingEngine.TicksToMs(clicks[^1] - clicks[0]);
        double mean = total / (count - 1);
        output.WriteLine($"{intervalMs} ms x {count}: mean gap {mean:0.0000} ms, min {gaps[0]:0.000}, median {gaps[gaps.Count / 2]:0.000}, " +
                         $"p99 {gaps[(int)(gaps.Count * 0.99)]:0.000}, max {gaps[^1]:0.000}");

        Assert.InRange(mean, intervalMs * 0.99, intervalMs * 1.01);
        Assert.InRange(gaps[gaps.Count / 2], intervalMs - 0.15, intervalMs + 0.15);
    }

    [Fact]
    public void SequenceRunsInOrderWithHoldsAndOverrides()
    {
        var profile = Plain(5, 1);
        profile.Repeat.Mode = RepeatMode.Loops;
        profile.Repeat.Count = 2;
        profile.Actions.Add(new ClickAction { X = 10, Y = 20, HoldMs = 30 });
        profile.Actions.Add(new KeyPressAction { Keys = { KeyCombo.Parse("Ctrl+C") } });
        profile.Actions.Add(new ScrollAction { Direction = ScrollDirection.Up, Amount = 2 });
        profile.Actions.Add(new ClickAction { X = 1, Y = 1, Enabled = false });
        profile.Actions.Add(new DragAction { X1 = 0, Y1 = 0, X2 = 100, Y2 = 0, DurationMs = 40, IntervalMs = 0 });

        Assert.Equal(StopReason.Completed, Run(profile).Reason);

        var events = _input.Snapshot();
        var names = events.Select(e => e.Event).Where(e => !e.StartsWith("move") && e != "releaseAll").ToList();
        string[] onePass = ["down Left", "up Left", "keydown Ctrl+C", "keyup Ctrl+C", "scroll Up 2", "down Left", "up Left"];
        Assert.Equal(onePass.Concat(onePass), names);

        // The 30 ms hold is honoured.
        double hold = TimingEngine.TicksToMs(events.First(e => e.Event == "up Left").Ticks - events.First(e => e.Event == "down Left").Ticks);
        Assert.InRange(hold, 29.5, 32);
        // The drag ends exactly on its target, and the disabled click never ran.
        Assert.Contains(events, e => e.Event == "move 100,0");
        Assert.DoesNotContain(events, e => e.Event == "move 1,1");
        Assert.Equal(0, _input.HeldCount);
    }

    [Fact]
    public void StopDuringAHoldReleasesTheButton()
    {
        var profile = Plain(5, 100);
        profile.Actions.Add(new ClickAction { UseCursor = true, HoldMs = 5000 });

        var (reason, _) = Run(profile, during: engine =>
        {
            Thread.Sleep(100);
            engine.Stop(StopReason.EmergencyHotkey);
        });

        Assert.Equal(StopReason.EmergencyHotkey, reason);
        Assert.Equal("releaseAll", _input.Snapshot()[^1].Event);
        Assert.Equal(0, _input.HeldCount);
    }

    [Fact]
    public void PauseHoldsTheRunAndResumeContinuesIt()
    {
        var profile = Plain(10, 40);
        long pausedAt = 0, resumedAt = 0;
        var (reason, _) = Run(profile, during: engine =>
        {
            Thread.Sleep(100);
            engine.TogglePause();
            Thread.Sleep(100); // let the pause take effect
            Assert.Equal(EngineState.Paused, engine.State);
            pausedAt = TimingEngine.Now;
            Thread.Sleep(300);
            resumedAt = TimingEngine.Now;
            engine.TogglePause();
        });

        Assert.Equal(StopReason.Completed, reason);
        var clicks = _input.Snapshot().Where(e => e.Event.StartsWith("click")).ToList();
        Assert.Equal(40, clicks.Count);
        Assert.DoesNotContain(clicks, c => c.Ticks > pausedAt && c.Ticks < resumedAt);
    }

    [Fact]
    public void NeverClicksItsOwnWindowAndResumesWhenTheCursorLeaves()
    {
        _windows.OwnWindowEverywhere = true; // the cursor is sitting on the clicker's own window
        var (reason, _) = Run(Plain(1, 200), during: engine =>
        {
            Thread.Sleep(200);
            Assert.DoesNotContain(_input.Snapshot(), e => e.Event.StartsWith("click"));
            Assert.True(engine.GetSnapshot().OverOwnWindow);
            Assert.Equal(EngineState.Running, engine.State);
            _windows.OwnWindowEverywhere = false; // the user moves the mouse onto the target
        });

        Assert.Equal(StopReason.Completed, reason);
        Assert.Equal(200, _input.Snapshot().Count(e => e.Event.StartsWith("click")));
    }

    [Fact]
    public void EmergencyCornerStopsTheRun()
    {
        var profile = Plain(5, 100_000);
        profile.CornerStop = true;
        _screen.Corner = (x, y) => x == 0 && y == 0;

        var (reason, _) = Run(profile, during: _ =>
        {
            Thread.Sleep(150);
            _input.Cursor = (0, 0); // the user shoves the mouse into the corner
        });
        Assert.Equal(StopReason.EmergencyCorner, reason);
    }

    [Fact]
    public void WindowTargetPausesOnFocusLossAndUsesRelativeCoordinates()
    {
        var profile = Plain(10, 20);
        profile.Window.Enabled = true;
        profile.Window.Title = "Game";
        profile.Window.RelativeCoordinates = true;
        profile.Actions.Add(new ClickAction { X = 5, Y = 6 });
        _windows.Origin = (-300, 40);
        _windows.Foreground = false;

        var (reason, _) = Run(profile, during: engine =>
        {
            Thread.Sleep(200);
            Assert.Equal(EngineState.WaitingForWindow, engine.State);
            Assert.DoesNotContain(_input.Snapshot(), e => e.Event.StartsWith("click"));
            _windows.Foreground = true;
        });

        Assert.Equal(StopReason.Completed, reason);
        Assert.Contains(_input.Snapshot(), e => e.Event == "move -295,46");
    }

    [Fact]
    public void PixelTriggersWaitSkipAndStop()
    {
        long matchAfter = TimingEngine.Now + TimingEngine.MsToTicks(150);
        _screen.Pixel = (x, _) => x == 50 && TimingEngine.Now >= matchAfter ? new PixelColor(250, 10, 10) : new PixelColor(0, 0, 0);

        var profile = Plain(1, 1);
        profile.Repeat.Mode = RepeatMode.Loops;
        profile.Actions.Add(new PixelClickAction { X = 50, Y = 5, Color = "#FF0000", Tolerance = 12, PollMs = 10, TimeoutMs = 5000 });
        profile.Actions.Add(new PixelClickAction { X = 60, Y = 5, Color = "#FF0000", PollMs = 10, TimeoutMs = 80, OnTimeout = PixelTimeoutBehavior.Skip });
        profile.Actions.Add(new PixelWaitAction { X = 60, Y = 5, Color = "#FF0000", PollMs = 10, TimeoutMs = 80, OnTimeout = PixelTimeoutBehavior.Stop });
        profile.Actions.Add(new ClickAction { X = 999, Y = 999 });

        var (reason, _) = Run(profile);

        Assert.Equal(StopReason.PixelTimeout, reason);
        var events = _input.Snapshot();
        var click = Assert.Single(events, e => e.Event.StartsWith("click"));   // only the first pixel click fired
        Assert.True(click.Ticks >= matchAfter);
        Assert.Contains(events, e => e.Event == "move 50,5");
        Assert.DoesNotContain(events, e => e.Event == "move 999,999");
    }

    [Fact]
    public void AreaWatchClicksEachTargetWithItsOwnButton()
    {
        // A 100 x 60 area at (-500, 40) on a monitor left of the primary, holding a red blob and a blue one.
        const int W = 100, H = 60;
        var frame = new int[W * H];
        void Blob(int cx, int cy, int rgb)
        {
            for (int y = cy - 4; y <= cy + 4; y++)
            for (int x = cx - 4; x <= cx + 4; x++)
                frame[y * W + x] = rgb;
        }
        Blob(20, 30, 0xE04030);   // red-ish: left-click rule
        Blob(70, 15, 0x3060E0);   // blue-ish: right-click rule
        _screen.Capture = (x, y, w, h) => x == -500 && y == 40 && w == W && h == H ? (int[])frame.Clone() : null;

        // Clicking a target makes it vanish, as in the game.
        var clicks = new List<(ClickButton Button, int X, int Y)>();
        _input.Clicked = (button, cx, cy) =>
        {
            clicks.Add((button, cx + 500, cy - 40));
            Blob(cx + 500, cy - 40, 0);
        };

        var profile = Plain(10, 1);
        profile.Repeat.Mode = RepeatMode.Infinite;
        profile.Actions.Add(new AreaWatchAction
        {
            X = -500, Y = 40, Width = W, Height = H, Priority = ScanPriority.Top, PollMs = 10, MinPixels = 10,
            TimeoutMs = 300, OnTimeout = PixelTimeoutBehavior.Stop,
            Rules =
            {
                new AreaRule { Color = "#E8432E", Tolerance = 30, Button = ClickButton.Left },
                new AreaRule { Color = "#2E6BE8", Tolerance = 30, Button = ClickButton.Right },
            },
        });

        Assert.Equal(StopReason.PixelTimeout, Run(profile).Reason); // nothing left to click, so the timeout ended the run
        Assert.Equal(2, clicks.Count);
        // Top priority: the blue blob (y = 15) is dealt with before the red one (y = 30).
        Assert.Equal(ClickButton.Right, clicks[0].Button);
        Assert.InRange(clicks[0].X, 68, 72);
        Assert.InRange(clicks[0].Y, 13, 17);
        Assert.Equal(ClickButton.Left, clicks[1].Button);
        Assert.InRange(clicks[1].X, 18, 22);
        Assert.InRange(clicks[1].Y, 28, 32);
    }

    [Fact]
    public void AreaWatchCanStayPutForASetTimeThenLetTheListFinish()
    {
        // Set-up click once, then watch for 400 ms clicking every target, then the run ends (1 loop).
        const int W = 60, H = 40;
        var frame = new int[W * H];
        for (int y = 15; y <= 24; y++)
        for (int x = 25; x <= 34; x++)
            frame[y * W + x] = 0xE04030; // a target that is always there (the game keeps sending them)
        _screen.Capture = (_, _, _, _) => (int[])frame.Clone();

        var profile = Plain(10, 1);
        profile.Repeat.Mode = RepeatMode.Loops;
        profile.Repeat.Count = 1;
        profile.Actions.Add(new ClickAction { X = 5, Y = 5 });                      // set-up click
        profile.Actions.Add(new AreaWatchAction
        {
            X = 100, Y = 100, Width = W, Height = H, WatchForMs = 400, PollMs = 10, MinPixels = 10,
            RetargetDelayMs = 0, // this "game" always has a fresh target in the same place
            Rules = { new AreaRule { Color = "#E8432E", Tolerance = 30, Button = ClickButton.Right } },
        });
        profile.Actions.Add(new ClickAction { X = 7, Y = 7, Button = ClickButton.Middle });   // after the game

        long start = TimingEngine.Now;
        Assert.Equal(StopReason.Completed, Run(profile).Reason);
        double elapsed = TimingEngine.TicksToMs(TimingEngine.Now - start);

        var clicks = _input.Snapshot().Where(e => e.Event.StartsWith("click")).Select(e => e.Event).ToList();
        Assert.Equal("click Left", clicks[0]);                    // the set-up click, exactly once
        Assert.Equal("click Middle", clicks[^1]);                 // the closing click, exactly once
        Assert.Equal(1, clicks.Count(c => c == "click Left"));
        Assert.Equal(1, clicks.Count(c => c == "click Middle"));
        int watched = clicks.Count(c => c == "click Right");
        Assert.InRange(watched, 15, 45);                          // one every ~10 ms for 400 ms
        Assert.InRange(elapsed, 400, 700);
    }

    [Fact]
    public void ATargetThatLingersAfterBeingClickedIsNotClickedAgainStraightAway()
    {
        // The target stays on screen after the click (a hit rock fading out). Clicking it again
        // every 10 ms would be 60 wasted clicks; with a 250 ms "leave it alone" there are about 3.
        const int W = 200, H = 80;
        var frame = new int[W * H];
        for (int y = 35; y <= 44; y++)
        for (int x = 30; x <= 39; x++)
            frame[y * W + x] = 0xE04030;
        _screen.Capture = (_, _, _, _) => (int[])frame.Clone();

        var clicks = new List<(long Ticks, int X)>();
        _input.Clicked = (_, cx, _) =>
        {
            clicks.Add((TimingEngine.Now, cx - 100));
            if (clicks.Count == 2)
            {
                // A second, separate target appears elsewhere: it must be clicked without any wait.
                for (int y = 35; y <= 44; y++)
                for (int x = 150; x <= 159; x++)
                    frame[y * W + x] = 0xE04030;
            }
        };

        var profile = Plain(10, 1);
        profile.Repeat.Mode = RepeatMode.Loops;
        profile.Actions.Add(new AreaWatchAction
        {
            X = 100, Y = 100, Width = W, Height = H, WatchForMs = 600, RetargetDelayMs = 250, PollMs = 10, MinPixels = 10,
            Priority = ScanPriority.Left,
            Rules = { new AreaRule { Color = "#E8432E", Tolerance = 30, Button = ClickButton.Left } },
        });

        Assert.Equal(StopReason.Completed, Run(profile).Reason);

        Assert.InRange(clicks.Count, 4, 7);    // not ~60
        var onFirst = clicks.Where(c => c.X < 100).Select(c => c.Ticks).ToList();
        for (int i = 1; i < onFirst.Count; i++)
            Assert.True(TimingEngine.TicksToMs(onFirst[i] - onFirst[i - 1]) >= 245, "the same spot was clicked again too soon");
        // The new target was dealt with promptly, while the first spot was still being left alone.
        var onSecond = clicks.Where(c => c.X >= 100).ToList();
        Assert.NotEmpty(onSecond);
        Assert.InRange(TimingEngine.TicksToMs(onSecond[0].Ticks - clicks[1].Ticks), 0, 120);
    }

    [Fact]
    public void AMarkerStartsTheWatchWhenItAppearsAndEndsItWhenItGoes()
    {
        // The area always has a target. The marker (a 20 x 10 label at 500, 20) decides when clicking happens.
        const int W = 60, H = 40;
        var frame = new int[W * H];
        for (int y = 15; y <= 24; y++)
        for (int x = 25; x <= 34; x++)
            frame[y * W + x] = 0xE04030;
        var label = new int[20 * 10];
        for (int i = 0; i < label.Length; i++) label[i] = i % 3 == 0 ? 0xFFFFFF : 0x202020;
        var somethingElse = new int[20 * 10];

        volatile_markerShowing = false;
        _screen.Capture = (x, y, w, h) => (w, h) == (20, 10)
            ? (x == 500 && y == 20 && volatile_markerShowing ? (int[])label.Clone() : (int[])somethingElse.Clone())
            : (int[])frame.Clone();

        var profile = Plain(10, 1);
        profile.Repeat.Mode = RepeatMode.Loops;
        profile.Actions.Add(new AreaWatchAction
        {
            X = 100, Y = 100, Width = W, Height = H, PollMs = 10, MinPixels = 10, RetargetDelayMs = 0,
            AnchorImage = TemplateImage.Encode(label, 20, 10), AnchorX = 500, AnchorY = 20, AnchorWidth = 20, AnchorHeight = 10,
            Rules = { new AreaRule { Color = "#E8432E", Tolerance = 30, Button = ClickButton.Left } },
        });

        long shownAt = 0, hiddenAt = 0;
        long start = TimingEngine.Now;
        var (reason, _) = Run(profile, during: engine =>
        {
            Thread.Sleep(300);
            Assert.DoesNotContain(_input.Snapshot(), e => e.Event.StartsWith("click"));   // waiting for the marker
            Assert.True(engine.GetSnapshot().WaitingForMarker);
            shownAt = TimingEngine.Now;
            volatile_markerShowing = true;       // the game screen opens
            Thread.Sleep(500);
            hiddenAt = TimingEngine.Now;
            volatile_markerShowing = false;      // ...and closes
        });
        long end = TimingEngine.Now;

        Assert.Equal(StopReason.Completed, reason);
        var clicks = _input.Snapshot().Where(e => e.Event.StartsWith("click")).Select(e => e.Ticks).ToList();
        Assert.True(clicks.Count > 20, $"only {clicks.Count} clicks while the marker was showing");
        Assert.All(clicks, t => Assert.True(t >= shownAt, "clicked before the marker appeared"));
        // Clicking stops the moment the marker goes: at most the one click already under way.
        // (The target is still on screen the whole time, so only the marker is holding it back.)
        int late = clicks.Count(t => TimingEngine.TicksToMs(t - hiddenAt) > 5);
        Assert.True(late == 0, $"{late} clicks landed more than 5 ms after the marker disappeared");
        // The watch itself ends about half a second later, not immediately and not never.
        Assert.InRange(TimingEngine.TicksToMs(end - hiddenAt), 400, 1200);
        _ = start;
    }

    private volatile bool volatile_markerShowing;

    [Fact]
    public void DurationLimitAndBurstModeWork()
    {
        var profile = Plain(5, 1);
        profile.Repeat.Mode = RepeatMode.Duration;
        profile.Repeat.DurationMs = 400;
        profile.Burst.Enabled = true;
        profile.Burst.Count = 3;
        profile.Burst.PauseMs = 100;

        long start = TimingEngine.Now;
        Assert.Equal(StopReason.Completed, Run(profile).Reason);
        Assert.InRange(TimingEngine.TicksToMs(TimingEngine.Now - start), 380, 600);

        var clicks = _input.Snapshot().Where(e => e.Event.StartsWith("click")).Select(e => e.Ticks).ToList();
        var gaps = clicks.Zip(clicks.Skip(1), (a, b) => TimingEngine.TicksToMs(b - a)).ToList();
        // Pattern: 5, 5, 100, 5, 5, 100, ...
        Assert.InRange(gaps[0], 4, 7);
        Assert.InRange(gaps[1], 4, 7);
        Assert.InRange(gaps[2], 98, 104);
        Assert.InRange(gaps[5], 98, 104);
    }

    [Fact]
    public void AllActionsDisabledIsReportedAsAnError()
    {
        var profile = Plain(5, 1);
        profile.Actions.Add(new ClickAction { Enabled = false });
        var (reason, error) = Run(profile);
        Assert.Equal(StopReason.Error, reason);
        Assert.Contains("disabled", error);
    }
}
