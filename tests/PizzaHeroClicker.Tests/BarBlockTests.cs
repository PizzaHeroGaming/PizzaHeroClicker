using System.IO;
using PizzaHeroClicker.Engine;
using PizzaHeroClicker.Models;
using PizzaHeroClicker.Services;
using Xunit;

namespace PizzaHeroClicker.Tests;

/// <summary>A small "shield" game: a red bar on a circle, balls flying out from the middle.</summary>
internal static class ShieldGame
{
    public const int Radius = 150, Size = 2 * (Radius + BarBlockAction.LookMargin), Centre = Size / 2;
    public const int Yellow = 0xF8F86A, White = 0xFFFFFF, Cyan = 0x62D6FF;

    /// <summary>Four kinds of ball, most valuable first, told apart by the colour of their ring.</summary>
    public static IEnumerable<BlockTarget> FourKinds() =>
    [
        new() { Name = "green", Color = "#70F070", Tolerance = 60 },
        new() { Name = "purple", Color = "#D060F0", Tolerance = 60 },
        new() { Name = "yellow", Color = "#F8F86A", Tolerance = 45 },
        new() { Name = "cyan", Color = "#FFFFFF", Tolerance = 30 },
    ];

    public static BarBlockAction Action() => new()
    {
        CentreX = 500, CentreY = 400, Radius = Radius, PollMs = 10,
        Targets = new(ShieldGame.FourKinds()),
    };

    /// <summary>
    /// A busy backdrop with nothing in it that is white or yellow. With <paramref name="redClouds"/>,
    /// two patches of the bar's red lie on its circle, each bigger than the bar (as in the real game).
    /// </summary>
    public static int[] Backdrop(bool redClouds = false, bool solid = false)
    {
        var pixels = new int[Size * Size];
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
            pixels[y * Size + x] = (x / 9 + y / 13) % 3 switch { 0 => 0x101828, 1 => 0x30205A, _ => 0x183840 };
        if (redClouds)
        {
            foreach (var (from, to) in new[] { (200.0, 225.0), (330.0, 350.0) })
            for (double a = from; a <= to; a += 0.2)
            for (int r = Radius - 12; r <= Radius + 12; r++)
            {
                int x = Centre + (int)Math.Round(r * Math.Cos(a * Math.PI / 180)), y = Centre + (int)Math.Round(r * Math.Sin(a * Math.PI / 180));
                if (solid || (x + y) % 3 != 0) pixels[y * Size + x] = solid ? 0xF03838 : 0xD84838; // red close enough to the bar's: speckled, or solid
            }
        }
        return pixels;
    }

    public static void Bar(int[] pixels, double degrees, int offX = 0, int offY = 0, double halfWidth = 6, int colour = 0xF03232)
    {
        for (double a = degrees - halfWidth; a <= degrees + halfWidth; a += 0.2)
        for (int r = Radius - 3; r <= Radius + 3; r++)
        {
            int x = Centre + offX + (int)Math.Round(r * Math.Cos(a * Math.PI / 180)), y = Centre + offY + (int)Math.Round(r * Math.Sin(a * Math.PI / 180));
            pixels[y * Size + x] = colour;
        }
    }

    /// <summary>A ball: a coloured ring round a lighter middle.</summary>
    public static void Ball(int[] pixels, double degrees, double distance, int ring, int middle, int offX = 0, int offY = 0)
    {
        int cx = Centre + offX + (int)Math.Round(distance * Math.Cos(degrees * Math.PI / 180));
        int cy = Centre + offY + (int)Math.Round(distance * Math.Sin(degrees * Math.PI / 180));
        for (int y = cy - 11; y <= cy + 11; y++)
        for (int x = cx - 11; x <= cx + 11; x++)
        {
            if (x < 0 || y < 0 || x >= Size || y >= Size) continue;
            int d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
            if (d2 <= 121) pixels[y * Size + x] = d2 >= 64 ? ring : middle;
        }
    }
}

public class BarBlockDetectorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(181)]
    [InlineData(359)]
    public void FindsTheBarsAngle(double degrees)
    {
        var detector = new BarBlockDetector(ShieldGame.Action());
        var frame = ShieldGame.Backdrop();
        ShieldGame.Bar(frame, degrees);
        detector.Look(frame, 0);
        Assert.NotNull(detector.BarAngle);
        Assert.InRange(Math.Abs(BarBlockDetector.Wrap(detector.BarAngle!.Value - degrees)), 0, 2);
    }

    [Fact]
    public void RedBackdropOnTheCircleIsTakenOffOnceTheBarHasMoved()
    {
        var detector = new BarBlockDetector(ShieldGame.Action());
        var first = ShieldGame.Backdrop(redClouds: true);
        ShieldGame.Bar(first, 90);
        detector.Look(first, 0);
        Assert.Equal(3, detector.Patches);                 // the bar and two backdrop patches
        Assert.NotEqual(90, Math.Round(detector.BarAngle!.Value)); // the biggest patch is not the bar: fooled, for now
        bool[] before = detector.MatchSnapshot;

        var second = ShieldGame.Backdrop(redClouds: true);
        ShieldGame.Bar(second, 140);                        // the bar moved; the backdrop did not
        detector.Look(second, 50);
        detector.LearnBackdrop(before, detector.MatchSnapshot);

        var third = ShieldGame.Backdrop(redClouds: true);
        ShieldGame.Bar(third, 215);                         // even right on top of a backdrop patch
        detector.Look(third, 100);
        Assert.InRange(Math.Abs(BarBlockDetector.Wrap(detector.BarAngle!.Value - 215)), 0, 3);
    }

    [Fact]
    public void StaysWithTheOneItIsAlreadyGoingFor()
    {
        var detector = new BarBlockDetector(ShieldGame.Action());
        for (long ms = 0; ms <= 2200; ms += 100)
        {
            var still = ShieldGame.Backdrop();
            ShieldGame.Bar(still, 110);
            detector.Look(still, ms);
        }
        // Two of the same kind, either side of the bar; the one at 200 degrees is a little further out.
        for (long ms = 2300; ms <= 2500; ms += 30)
        {
            var frame = ShieldGame.Backdrop();
            ShieldGame.Bar(frame, 110);
            double t = (ms - 2300) / 1000.0;
            ShieldGame.Ball(frame, 20, 60 + 60 * t, ShieldGame.White, ShieldGame.Cyan);
            ShieldGame.Ball(frame, 200, 70 + 60 * t, ShieldGame.White, ShieldGame.Cyan);
            detector.Look(frame, ms);
        }
        var fresh = detector.Choose(degreesPerSecond: 400);
        Assert.Equal(200, Math.Round(fresh!.Angle / 10) * 10);          // left to itself: the one out further
        var going = detector.Threats.Single(th => Math.Abs(BarBlockDetector.Wrap(th.Angle - 20)) < 5);
        var kept = detector.Choose(degreesPerSecond: 400, current: going);
        Assert.InRange(Math.Abs(BarBlockDetector.Wrap(kept!.Angle - 20)), 0, 5); // already going for the other: keeps going
    }

    [Theory]
    [InlineData(100, 90, 1, 10)]
    [InlineData(100, 90, 2, 10)]
    [InlineData(260, 90, 2, -10)]   // nearer the second bar, at 270
    [InlineData(0, 90, 2, 90)]      // exactly between: either way, 90 degrees
    [InlineData(200, 10, 3, -50)]   // bars at 10, 130 and 250
    public void TurnsTheNearestBarOntoTheTarget(double target, double bar, int bars, double expected) =>
        Assert.Equal(Math.Abs(expected), Math.Abs(BarBlockDetector.ToNearestBar(target, bar, bars)), 3);

    [Theory]
    [InlineData("90, 180", new double[] { 0, 90, 180 })]
    [InlineData("180;90 junk -90", new double[] { 0, 180, 90, 270 })]
    [InlineData("", new double[] { 0, 120, 240 })]   // empty: the Bars number, evenly spaced (3 here)
    [InlineData("0, 360", new double[] { 0, 120, 240 })] // only the picked bar's own place: as empty
    public void OtherBarsAreReadFromTheList(string otherBars, double[] expected) =>
        Assert.Equal(expected, new BarBlockAction { BarCount = 3, OtherBars = otherBars }.BarOffsets());

    [Theory]
    [InlineData(80, 0, -10)]    // bars at 0, 90, 180: the one at 90 is 10 short
    [InlineData(300, 0, -60)]   // nearest is the picked one, turning back 60
    [InlineData(240, 0, 60)]    // the opposite one, 60 on
    public void TurnsTheNearestOfUnevenlySpacedBarsOntoTheTarget(double target, double bar, double expected) =>
        Assert.Equal(expected, BarBlockDetector.ToNearestBar(target, bar, new double[] { 0, 90, 180 }), 3);

    [Fact]
    public void ASecondBarInATargetsColourIsNotTakenForATarget()
    {
        // The second bar is green, close to the green kind of thing to block. It moves round with the
        // first bar, staying the same distance out: it must not show up as something flying out.
        var detector = new BarBlockDetector(ShieldGame.Action());
        for (long ms = 0; ms <= 3000; ms += 30)
        {
            double angle = 40 + ms * 0.06;
            var frame = ShieldGame.Backdrop();
            ShieldGame.Bar(frame, angle);
            ShieldGame.Bar(frame, angle + 180, colour: 0x60F070);
            detector.Look(frame, ms);
        }
        Assert.Empty(detector.Threats);

        // A real green one flying out is still seen, all the way.
        for (long ms = 3030; ms <= 4500; ms += 30)
        {
            double angle = 40 + ms * 0.06;
            var frame = ShieldGame.Backdrop();
            ShieldGame.Bar(frame, angle);
            ShieldGame.Bar(frame, angle + 180, colour: 0x60F070);
            ShieldGame.Ball(frame, 300, 45 + (ms - 3030) * 0.06, 0x70F070, ShieldGame.White);
            detector.Look(frame, ms);
        }
        Assert.Contains(detector.Threats, t => t.Target == 0 && Math.Abs(BarBlockDetector.Wrap(t.Angle - 300)) < 4 && t.Distance > 110);
    }

    [Fact]
    public void AGlowWhereABallMeetsTheCircleIsNotTakenForAValuableOne()
    {
        // A common ball flies out and meets the circle; where it lands, something of a more valuable
        // kind's colour shows for a moment (a bar lighting up, a splash). It was never seen flying
        // out from the middle, so it is not something to steer for.
        var detector = new BarBlockDetector(ShieldGame.Action());
        for (long ms = 0; ms <= 2200; ms += 100)
        {
            var still = ShieldGame.Backdrop();
            ShieldGame.Bar(still, 0);
            detector.Look(still, ms);
        }
        for (long ms = 2230; ms <= 3400; ms += 30)
        {
            var frame = ShieldGame.Backdrop();
            ShieldGame.Bar(frame, 0);
            double out_ = 40 + (ms - 2230) * 0.12;
            if (out_ < 120) ShieldGame.Ball(frame, 128, out_, ShieldGame.White, ShieldGame.Cyan);
            else ShieldGame.Ball(frame, 129, 124, 0x70F070, ShieldGame.White);
            detector.Look(frame, ms);
            Assert.DoesNotContain(detector.Threats, t => t.Target == 0);
        }
    }

    [Fact]
    public void WhileWaitingOnOneItKnowsWhichComesNext()
    {
        // A common ball about to arrive by the bar, and a valuable one coming out further round:
        // block the first, and the plan knows the second is next (to drift towards it meanwhile).
        var detector = TwoBalls(90, (95, 102, ShieldGame.White, ShieldGame.Cyan), (160, 32, ShieldGame.Yellow, ShieldGame.White));
        var choice = detector.Choose(degreesPerSecond: 128);
        Assert.Equal(3, choice!.Target);
        Assert.Equal(2, detector.PlannedNext?.Target);
    }

    [Fact]
    public void BothOnlyIfTheBarCanGetGoingAgainInTime()
    {
        // The bar covers a common ball about to arrive; a valuable one arrives a moment later, 30
        // degrees round. With an instant restart there is just time for both. Allowing for the bar
        // to get going again after the block, there is not: then the valuable one is the one to have.
        var detector = TwoBalls(90, (93, 100, ShieldGame.White, ShieldGame.Cyan), (60, 70, ShieldGame.Yellow, ShieldGame.White));
        Assert.Equal(3, detector.Choose(degreesPerSecond: 64)!.Target);
        Assert.Equal(2, detector.Choose(degreesPerSecond: 64, restartSeconds: 0.2)!.Target);
    }

    [Fact]
    public void NoBarNoAngle()
    {
        var detector = new BarBlockDetector(ShieldGame.Action());
        detector.Look(ShieldGame.Backdrop(), 0);
        Assert.Null(detector.BarAngle);
    }

    /// <summary>
    /// Learns the backdrop with the bar at <paramref name="bar"/>, then shows two balls flying out at
    /// 60 px a second and returns the detector as it stands after the last look.
    /// </summary>
    private static BarBlockDetector TwoBalls(double bar, (double Angle, double From, int Ring, int Middle) a, (double Angle, double From, int Ring, int Middle) b)
    {
        var detector = new BarBlockDetector(ShieldGame.Action());
        for (long ms = 0; ms <= 2200; ms += 100)
        {
            var still = ShieldGame.Backdrop();
            ShieldGame.Bar(still, bar);
            detector.Look(still, ms);
        }
        for (long ms = 2300; ms <= 2600; ms += 30)
        {
            var frame = ShieldGame.Backdrop();
            ShieldGame.Bar(frame, bar);
            double t = (ms - 2300) / 1000.0;
            ShieldGame.Ball(frame, a.Angle, a.From + 60 * t, a.Ring, a.Middle);
            ShieldGame.Ball(frame, b.Angle, b.From + 60 * t, b.Ring, b.Middle);
            detector.Look(frame, ms);
        }
        return detector;
    }

    [Fact]
    public void SeesBallsByKind()
    {
        var detector = TwoBalls(0, (20, 72, ShieldGame.White, ShieldGame.Cyan), (300, 42, ShieldGame.Yellow, ShieldGame.White));
        Assert.Equal(2, detector.Threats.Count);
        Assert.Contains(detector.Threats, t => t.Target == 2 && Math.Abs(BarBlockDetector.Wrap(t.Angle - 300)) < 4); // yellow
        Assert.Contains(detector.Threats, t => t.Target == 3 && Math.Abs(BarBlockDetector.Wrap(t.Angle - 20)) < 4);  // cyan (white ring)
    }

    [Fact]
    public void DoesNotDropAnEasyBlockToFetchAValuableOneThereIsTimeForAfterwards()
    {
        // As in a real game: the bar is right by a common ball about to arrive, and a more valuable
        // one has just come out on the far side. Block the near one first, then go for the other.
        var detector = TwoBalls(90, (95, 102, ShieldGame.White, ShieldGame.Cyan), (290, 32, ShieldGame.Yellow, ShieldGame.White));
        var choice = detector.Choose(degreesPerSecond: 128);
        Assert.Equal(3, choice!.Target);
        Assert.InRange(Math.Abs(BarBlockDetector.Wrap(choice.Angle - 95)), 0, 4);
    }

    [Fact]
    public void WhenOnlyOneCanBeHadTheValuableOneWins()
    {
        // Equally far either side of the bar and arriving together: there is time for one.
        var detector = TwoBalls(110, (20, 82, ShieldGame.White, ShieldGame.Cyan), (200, 82, ShieldGame.Yellow, ShieldGame.White));
        Assert.Equal(2, detector.Choose(degreesPerSecond: 128)!.Target);
    }
}

[Collection("Timing")]
public class BarBlockEngineTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(33, false)] // the picture two frames behind the game, as a real screen capture can be
    [InlineData(66, false)] // four frames behind
    [InlineData(33, true)]  // and with red backdrop on the bar's circle, bigger than the bar, that hides it completely
    [InlineData(33, false, 10)] // and with the centre clicked 10 px off the real one
    // A long bar that turns slowly. Half of these balls are out of reach whatever it does (more than
    // the 100 degrees it can turn while one flies, plus its half-length, from where the last one was),
    // so here it must block the ones it can reach (allowing one for timing jitter).
    [InlineData(33, false, 0, 24, 150, 0.33)]
    // Shaped like a real game: a ball a second, each taking 3.4 s to arrive, so three or four are in
    // flight at once; a slow bar; every fourth ball a more valuable kind. With these angles at most 9
    // of the 12 can be reached; it must get close to that (allowing a ball for timing jitter on a busy
    // test machine), and every valuable one.
    [InlineData(33, false, 0, 6.5, 128, 0.55, 1000, 3400, 4)]
    // The same, with a second bar (green) opposite the first: nothing is ever more than 90 degrees from one.
    [InlineData(33, false, 0, 6.5, 128, 0.8, 1000, 3400, 4, 2)]
    // Three bars, not evenly spaced: a quarter turn on, and opposite.
    [InlineData(33, false, 0, 6.5, 128, 0.8, 1000, 3400, 4, 3, "90, 180")]
    public void SteersTheBarInFrontOfTheBallsAndLetsGoOfTheKeysAtTheEnd(int pictureLagMs, bool redClouds, int centreOffPx = 0, double barHalfWidth = 6,
        double turnSpeed = 400, double mustBlock = 0.8, int ballEveryMs = 700, int flightMs = 900, int valuableEvery = 0, int bars = 1, string otherBars = "")
    {
        var offsets = new BarBlockAction { BarCount = bars, OtherBars = otherBars }.BarOffsets();
        // A "shield" game run in real time: holding A turns the bar clockwise at 400 degrees a second,
        // D anticlockwise. A ball comes out of the middle every 0.7 s in a random direction and takes
        // 0.9 s to reach the bar's circle, so the bar can always get there in time if it sets off
        // promptly. Nothing tells the action which key does what: it finds out.
        double TurnSpeed = turnSpeed;
        double BallSpeed = ShieldGame.Radius / (flightMs / 1000.0);
        var input = new FakeInput();
        var screen = new FakeScreen();
        var windows = new FakeWindows();
        var rng = new Random(7);
        var balls = Enumerable.Range(0, 12).Select(i => (Born: 2500 + i * (double)ballEveryMs, Angle: rng.NextDouble() * 360, Valuable: valuableEvery > 0 && i % valuableEvery == 1)).ToList();

        object sync = new();
        double barAngle = 90, lastMs = 0;
        var history = new List<(double Ms, double Angle)>(); // where the bar was, for a picture that lags behind
        int direction = 0; // +1 clockwise (A), -1 anticlockwise (D)
        long start = TimingEngine.Now;
        double Now() => TimingEngine.TicksToMs(TimingEngine.Now - start);
        void Advance()
        {
            double ms = Now();
            barAngle = (barAngle + direction * TurnSpeed * (ms - lastMs) / 1000 + 360) % 360;
            lastMs = ms;
            history.Add((ms, barAngle));
        }
        input.KeyChanged = (key, down) =>
        {
            lock (sync)
            {
                Advance();
                if (key is null) direction = 0;
                else if (key.Value.Vk == 0x41) direction = down ? 1 : direction == 1 ? 0 : direction;
                else if (key.Value.Vk == 0x44) direction = down ? -1 : direction == -1 ? 0 : direction;
            }
        };

        var outcome = new Dictionary<int, bool>(); // ball -> blocked?
        screen.Capture = (_, _, w, h) =>
        {
            lock (sync)
            {
                Advance();
                double ms = lastMs;
                var frame = ShieldGame.Backdrop(redClouds, solid: true);
                double shown = history.LastOrDefault(h => h.Ms <= ms - pictureLagMs, history[0]).Angle;
                ShieldGame.Bar(frame, shown, centreOffPx, -centreOffPx / 2, barHalfWidth);
                foreach (double o in offsets.Skip(1)) ShieldGame.Bar(frame, shown + o, centreOffPx, -centreOffPx / 2, barHalfWidth, 0x40E050);
                for (int i = 0; i < balls.Count; i++)
                {
                    double distance = (ms - balls[i].Born) * BallSpeed / 1000;
                    if (distance < 0 || outcome.ContainsKey(i)) continue;
                    if (distance >= ShieldGame.Radius)
                    {
                        outcome[i] = Math.Abs(BarBlockDetector.ToNearestBar(balls[i].Angle, barAngle, offsets)) <= barHalfWidth + 2; // within a bar, give or take the ball's size
                        output.WriteLine($"ball {i} at {balls[i].Angle:0} deg reached the circle at {ms:0} ms; bar at {barAngle:0}: {(outcome[i] ? "blocked" : "missed")}");
                        continue;
                    }
                    if (balls[i].Valuable) ShieldGame.Ball(frame, balls[i].Angle, distance, ShieldGame.Yellow, ShieldGame.White, centreOffPx, -centreOffPx / 2);
                    else ShieldGame.Ball(frame, balls[i].Angle, distance, ShieldGame.White, ShieldGame.Cyan, centreOffPx, -centreOffPx / 2);
                }
                return frame;
            }
        };

        using var timing = new TimingEngine();
        using var engine = new ClickEngine(input, screen, windows, timing);
        string recording = Path.Combine(Path.GetTempPath(), $"phc-steer-{Guid.NewGuid():N}");
        if (redClouds) engine.AreaDebugFolder = recording; // the troubleshooting recorder, once
        using var finished = new ManualResetEventSlim();
        engine.Stopped += (_, _) => finished.Set();
        var profile = new Profile { IntervalMs = 10, StartDelayMs = 0, CornerStop = false, Repeat = { Mode = RepeatMode.Loops, Count = 1 } };
        var action = ShieldGame.Action();
        action.BarCount = bars;
        action.OtherBars = otherBars;
        action.WatchForMs = 2500 + 12 * ballEveryMs + flightMs;
        profile.Actions.Add(action);
        Assert.True(engine.Start(profile));
        Assert.True(finished.Wait(40_000), "the run did not finish");

        Assert.True(outcome.Count >= 10, $"only {outcome.Count} balls reached the circle");
        int blocked = outcome.Values.Count(b => b);
        output.WriteLine($"picture {pictureLagMs} ms behind{(redClouds ? ", red backdrop on the circle" : "")}{(centreOffPx != 0 ? $", centre {centreOffPx} px off" : "")}{(barHalfWidth != 6 ? $", bar {2 * barHalfWidth:0} deg long turning {turnSpeed:0} deg/s" : "")}{(flightMs != 900 ? $", a ball every {ballEveryMs} ms taking {flightMs} ms" : "")}{(offsets.Length > 1 ? $", {offsets.Length} bars at {string.Join("/", offsets)}" : "")}: blocked {blocked} of {outcome.Count}");
        Assert.True(blocked >= outcome.Count * mustBlock, $"blocked {blocked} of {outcome.Count}");
        var valuable = Enumerable.Range(0, balls.Count).Where(i => balls[i].Valuable && outcome.ContainsKey(i)).ToList();
        Assert.True(valuable.All(i => outcome[i]), $"missed a valuable one: {string.Join(", ", valuable.Where(i => !outcome[i]))}");
        Assert.Equal(0, input.HeldCount); // nothing left held down
        if (redClouds)
        {
            // The recorder wrote down every look, the calibration, and pictures of the circle.
            for (int i = 0; i < 50 && !File.Exists(Path.Combine(recording, "steer.txt")); i++) Thread.Sleep(20);
            string log = File.ReadAllText(Path.Combine(recording, "steer.txt"));
            Assert.Contains("calibration: held A", log);
            Assert.Contains("steering for kind 4", log);
            Assert.True(Directory.GetFiles(recording, "steer-*.png").Length >= 5);
            Directory.Delete(recording, recursive: true);
        }
    }
}
