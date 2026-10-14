using PizzaHeroClicker.Services;
using Xunit;

namespace PizzaHeroClicker.Tests;

public class TargetTrackerTests
{
    private const double LookMs = 30;

    /// <summary>Looks every 30 ms at objects whose measured centres wobble by up to <paramref name="wobble"/> px.</summary>
    private static (double X, double Y) Follow(TargetTracker tracker, Func<double, (double X, double Y)[]> positions, double untilMs, int wobble, int seed = 1)
    {
        var rng = new Random(seed);
        (int X, int Y) last = default;
        for (double t = 0; t <= untilMs; t += LookMs)
        {
            var seen = positions(t).Select(p => ((int)Math.Round(p.X) + rng.Next(-wobble, wobble + 1), (int)Math.Round(p.Y) + rng.Next(-wobble, wobble + 1))).ToList();
            tracker.Update(seen, t);
            last = seen[0];
        }
        return tracker.VelocityOf(last.Item1, last.Item2);
    }

    [Fact]
    public void ASteadilyMovingTargetGetsItsSpeedDespiteWobble()
    {
        var tracker = new TargetTracker();
        var v = Follow(tracker, t => [(100 + 0.6 * t, 300 - 0.2 * t)], 400, wobble: 3); // 600 and -200 px/s
        Assert.InRange(v.X, 500, 700);
        Assert.InRange(v.Y, -300, -100);
    }

    [Fact]
    public void NoSpeedIsGivenBeforeTheTargetHasBeenFollowedForAWhile()
    {
        var tracker = new TargetTracker();
        Assert.Equal((0.0, 0.0), Follow(tracker, t => [(100 + 0.6 * t, 300)], 2 * LookMs, wobble: 0)); // three looks, 60 ms
    }

    [Fact]
    public void ATargetStandingStillWithWobbleIsNotThoughtToBeMoving()
    {
        var tracker = new TargetTracker();
        var v = Follow(tracker, _ => [(400, 400)], 400, wobble: 2);
        Assert.True(Math.Sqrt(v.X * v.X + v.Y * v.Y) < 40, $"a still target read as moving at {v}");
    }

    [Fact]
    public void ErraticJumpsGiveNoSpeedRatherThanAWildOne()
    {
        // The two-look estimate this replaced read jumps like these as hundreds of px/s.
        var tracker = new TargetTracker();
        var v = Follow(tracker, t => [(400 + ((int)(t / LookMs) % 2 == 0 ? 0 : 14), 400)], 400, wobble: 0);
        Assert.Equal((0.0, 0.0), v);
    }

    [Fact]
    public void TwoTargetsPassingCloseByAreNotMixedUp()
    {
        // One going right, one going left, passing 30 px apart.
        var tracker = new TargetTracker();
        var v = Follow(tracker, t => [(100 + 0.5 * t, 300), (400 - 0.5 * t, 330)], 600, wobble: 2);
        Assert.InRange(v.X, 400, 600); // the first one, still heading right at 500 px/s
        Assert.InRange(Math.Abs(v.Y), 0, 80);
    }
}
