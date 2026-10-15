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

    public static void Bar(int[] pixels, double degrees, int offX = 0, int offY = 0)
    {
        for (double a = degrees - 6; a <= degrees + 6; a += 0.2)
        for (int r = Radius - 3; r <= Radius + 3; r++)
        {
            int x = Centre + offX + (int)Math.Round(r * Math.Cos(a * Math.PI / 180)), y = Centre + offY + (int)Math.Round(r * Math.Sin(a * Math.PI / 180));
            pixels[y * Size + x] = 0xF03232;
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

    [Fact]
    public void NoBarNoAngle()
    {
        var detector = new BarBlockDetector(ShieldGame.Action());
        detector.Look(ShieldGame.Backdrop(), 0);
        Assert.Null(detector.BarAngle);
    }

    [Fact]
    public void SeesBallsByKindAndGoesForTheValuableOneItCanReach()
    {
        var detector = new BarBlockDetector(ShieldGame.Action());
        for (long ms = 0; ms <= 2200; ms += 100)
        {
            var still = ShieldGame.Backdrop();
            ShieldGame.Bar(still, 0);
            detector.Look(still, ms); // learn the backdrop
        }

        // A common (white and cyan) ball further out at 20 degrees, a yellow one at 300 degrees.
        BarThreat? choice = null;
        for (long ms = 2300; ms <= 2600; ms += 30)
        {
            var frame = ShieldGame.Backdrop();
            ShieldGame.Bar(frame, 0);
            double t = (ms - 2300) / 1000.0;
            ShieldGame.Ball(frame, 20, 90 + 60 * t, ShieldGame.White, ShieldGame.Cyan);
            ShieldGame.Ball(frame, 300, 60 + 60 * t, ShieldGame.Yellow, ShieldGame.White);
            detector.Look(frame, ms);
            choice = detector.Choose(degreesPerSecond: 400);
        }
        Assert.Equal(2, detector.Threats.Count);
        Assert.Contains(detector.Threats, t => t.Target == 2 && Math.Abs(BarBlockDetector.Wrap(t.Angle - 300)) < 4); // yellow
        Assert.Contains(detector.Threats, t => t.Target == 3 && Math.Abs(BarBlockDetector.Wrap(t.Angle - 20)) < 4);  // cyan (white ring)
        Assert.NotNull(choice);
        Assert.Equal(2, choice!.Target); // the bar can reach either in time, so the more valuable yellow one

        // Turning slowly it could only reach the near one in time: then that one.
        Assert.Equal(3, detector.Choose(degreesPerSecond: 30)!.Target);
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
    public void SteersTheBarInFrontOfTheBallsAndLetsGoOfTheKeysAtTheEnd(int pictureLagMs, bool redClouds, int centreOffPx = 0)
    {
        // A "shield" game run in real time: holding A turns the bar clockwise at 400 degrees a second,
        // D anticlockwise. A ball comes out of the middle every 0.7 s in a random direction and takes
        // 0.9 s to reach the bar's circle, so the bar can always get there in time if it sets off
        // promptly. Nothing tells the action which key does what: it finds out.
        const double TurnSpeed = 400, BallSpeed = ShieldGame.Radius / 0.9;
        var input = new FakeInput();
        var screen = new FakeScreen();
        var windows = new FakeWindows();
        var rng = new Random(7);
        var balls = Enumerable.Range(0, 12).Select(i => (Born: 2500 + i * 700.0, Angle: rng.NextDouble() * 360)).ToList();

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
                ShieldGame.Bar(frame, shown, centreOffPx, -centreOffPx / 2);
                for (int i = 0; i < balls.Count; i++)
                {
                    double distance = (ms - balls[i].Born) * BallSpeed / 1000;
                    if (distance < 0 || outcome.ContainsKey(i)) continue;
                    if (distance >= ShieldGame.Radius)
                    {
                        outcome[i] = Math.Abs(BarBlockDetector.Wrap(barAngle - balls[i].Angle)) <= 8; // the bar is 12 degrees wide
                        output.WriteLine($"ball {i} at {balls[i].Angle:0} deg reached the circle at {ms:0} ms; bar at {barAngle:0}: {(outcome[i] ? "blocked" : "missed")}");
                        continue;
                    }
                    ShieldGame.Ball(frame, balls[i].Angle, distance, ShieldGame.White, ShieldGame.Cyan, centreOffPx, -centreOffPx / 2);
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
        action.WatchForMs = 11_500;
        profile.Actions.Add(action);
        Assert.True(engine.Start(profile));
        Assert.True(finished.Wait(20_000), "the run did not finish");

        Assert.True(outcome.Count >= 10, $"only {outcome.Count} balls reached the circle");
        int blocked = outcome.Values.Count(b => b);
        output.WriteLine($"picture {pictureLagMs} ms behind{(redClouds ? ", red backdrop on the circle" : "")}{(centreOffPx != 0 ? $", centre {centreOffPx} px off" : "")}: blocked {blocked} of {outcome.Count}");
        Assert.True(blocked >= outcome.Count * 0.8, $"blocked {blocked} of {outcome.Count}");
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
