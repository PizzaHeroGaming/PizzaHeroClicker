using System.Diagnostics;
using PizzaHeroClicker.Engine;
using PizzaHeroClicker.Models;
using PizzaHeroClicker.Services;
using Xunit;
using Xunit.Abstractions;

namespace PizzaHeroClicker.Tests;

public class TemplateMatcherTests(ITestOutputHelper output)
{
    private const int W = 400, H = 300;
    private const int Space = 0x06070C, Grey = 0xB9B4A6;

    private static int[] Frame(int seed = 1)
    {
        var pixels = new int[W * H];
        Array.Fill(pixels, Space);
        var random = new Random(seed);
        for (int i = 0; i < 60; i++) pixels[random.Next(H) * W + random.Next(W)] = 0xE0E0FF; // stars
        return pixels;
    }

    /// <summary>Draws a 34 px shape centred on (cx, cy). Every shape here is the same colour.</summary>
    internal static void Draw(int[] pixels, int width, string shape, int cx, int cy, int rgb = Grey)
    {
        for (int dy = -17; dy <= 17; dy++)
        for (int dx = -17; dx <= 17; dx++)
        {
            bool on = shape switch
            {
                "disc" => dx * dx + dy * dy <= 17 * 17,
                "cross" => Math.Abs(dx) <= 4 || Math.Abs(dy) <= 4,
                "ring" => dx * dx + dy * dy <= 17 * 17 && dx * dx + dy * dy >= 10 * 10,
                _ => true, // square
            };
            if (on) pixels[(cy + dy) * width + cx + dx] = rgb;
        }
    }

    /// <summary>Snips a 46 px box around a point, as a user would with a little background showing.</summary>
    internal static (int[] Pixels, int Size) Snip(int[] pixels, int width, int cx, int cy)
    {
        const int size = 46;
        var crop = new int[size * size];
        for (int y = 0; y < size; y++) Array.Copy(pixels, (cy - 23 + y) * width + cx - 23, crop, y * size, size);
        return (crop, size);
    }

    private static PreparedTemplate Picture(string shape)
    {
        var canvas = new int[100 * 100];
        Array.Fill(canvas, Space);
        Draw(canvas, 100, shape, 50, 50);
        var (pixels, size) = Snip(canvas, 100, 50, 50);
        return PreparedTemplate.Create(pixels, size, size)!;
    }

    [Fact]
    public void FindsAPictureWhereItAppears()
    {
        var frame = Frame();
        Draw(frame, W, "cross", 251, 133);
        var matcher = new TemplateMatcher();
        matcher.SetFrame(frame, W, H);

        var match = matcher.FindFirst(Picture("cross"), 0.85, ScanPriority.Top);

        Assert.NotNull(match);
        Assert.InRange(match.Value.X, 250, 252);
        Assert.InRange(match.Value.Y, 132, 134);
        Assert.True(match.Value.Score > 0.97, $"score {match.Value.Score}");
    }

    [Fact]
    public void NothingMatchesInEmptySpace()
    {
        var matcher = new TemplateMatcher();
        matcher.SetFrame(Frame(), W, H);
        Assert.Null(matcher.FindFirst(Picture("disc"), 0.85, ScanPriority.Top));
    }

    [Theory]
    [InlineData("square", "disc")]
    [InlineData("square", "cross")]
    [InlineData("disc", "cross")]
    [InlineData("disc", "ring")]
    [InlineData("cross", "ring")]
    public void SameColouredShapesAreToldApart(string wanted, string present)
    {
        var frame = Frame();
        Draw(frame, W, present, 200, 150);
        var matcher = new TemplateMatcher();
        matcher.SetFrame(frame, W, H);

        // Looking for one shape must not report the other shape as it...
        var wrong = matcher.FindFirst(Picture(wanted), 0.85, ScanPriority.Top);
        // ...while looking for the shape that is really there finds it almost perfectly.
        var right = matcher.FindFirst(Picture(present), 0.85, ScanPriority.Top);

        output.WriteLine($"picture of a {wanted} against a {present}: {(wrong is null ? "no match" : wrong.Value.Score.ToString("0.000"))}; picture of a {present}: {right?.Score:0.000}");
        Assert.Null(wrong);
        Assert.NotNull(right);
    }

    [Fact]
    public void PriorityChoosesTheMostUrgentOfSeveral()
    {
        var frame = Frame();
        Draw(frame, W, "disc", 60, 50);
        Draw(frame, W, "disc", 330, 240);
        Draw(frame, W, "disc", 200, 140);
        var matcher = new TemplateMatcher();
        matcher.SetFrame(frame, W, H);
        var disc = Picture("disc");

        Assert.InRange(matcher.FindFirst(disc, 0.85, ScanPriority.Bottom)!.Value.Y, 238, 242);
        Assert.InRange(matcher.FindFirst(disc, 0.85, ScanPriority.Top)!.Value.Y, 48, 52);
        Assert.InRange(matcher.FindFirst(disc, 0.85, ScanPriority.Right)!.Value.X, 328, 332);
        Assert.InRange(matcher.FindFirst(disc, 0.85, ScanPriority.Left)!.Value.X, 58, 62);
        Assert.InRange(matcher.FindFirst(disc, 0.85, ScanPriority.Center)!.Value.X, 198, 202); // the one in the middle
    }

    [Fact]
    public void ADimmerOrTintedCopyStillMatchesButADifferentColouredOneDoesNot()
    {
        var frame = Frame();
        Draw(frame, W, "cross", 100, 100, 0x8A8780);   // the same grey, noticeably dimmer
        Draw(frame, W, "cross", 300, 200, 0x2040E0);   // same shape, strongly blue
        var matcher = new TemplateMatcher();
        matcher.SetFrame(frame, W, H);
        var cross = Picture("cross");

        var first = matcher.FindFirst(cross, 0.85, ScanPriority.Top);
        Assert.NotNull(first);
        Assert.InRange(first.Value.X, 98, 102);   // the dim grey one matches

        // With only the blue cross on screen, the grey cross picture must not claim it.
        var blueOnly = Frame();
        Draw(blueOnly, W, "cross", 300, 200, 0x2040E0);
        matcher.SetFrame(blueOnly, W, H);
        Assert.Null(matcher.FindFirst(cross, 0.85, ScanPriority.Top));
    }

    [Fact]
    public void FlatOrTinyPicturesAreRejected()
    {
        var flat = new int[40 * 40];
        Array.Fill(flat, Grey);
        Assert.Null(PreparedTemplate.Create(flat, 40, 40));
        Assert.Null(PreparedTemplate.Create(new int[9], 3, 3));
    }

    [Fact]
    public void PicturesSurviveBeingStoredInAProfile()
    {
        var canvas = new int[100 * 100];
        Array.Fill(canvas, Space);
        Draw(canvas, 100, "ring", 50, 50);
        var (pixels, size) = Snip(canvas, 100, 50, 50);

        var rule = new AreaRule { Image = TemplateImage.Encode(pixels, size, size), Button = ClickButton.Right };
        var action = new AreaWatchAction { Rules = { rule } };
        var copy = (AreaWatchAction)action.Clone(); // JSON round trip, as when saving a profile

        Assert.True(copy.Rules[0].IsImage);
        Assert.True(TemplateImage.TryDecode(copy.Rules[0].Image, out var decoded, out int w, out int h));
        Assert.Equal((size, size), (w, h));
        Assert.Equal(pixels, decoded);
        Assert.False(TemplateImage.TryDecode("not an image", out _, out _, out _));
    }

    [Fact]
    public void ALargeAreaWithSeveralPicturesIsFastEnoughToRunManyTimesASecond()
    {
        const int width = 1200, height = 800;
        var frame = new int[width * height];
        Array.Fill(frame, Space);
        var random = new Random(3);
        for (int i = 0; i < 400; i++) frame[random.Next(height) * width + random.Next(width)] = 0xE0E0FF;
        Draw(frame, width, "disc", 300, 200);
        Draw(frame, width, "cross", 900, 600);
        Draw(frame, width, "square", 600, 400);
        Draw(frame, width, "ring", 1000, 150);
        PreparedTemplate[] pictures = [Picture("disc"), Picture("cross"), Picture("square"), Picture("ring")];
        var matcher = new TemplateMatcher();

        matcher.SetFrame(frame, width, height);
        foreach (var p in pictures) Assert.NotNull(matcher.FindFirst(p, 0.85, ScanPriority.Bottom)); // warm up + correctness

        var clock = Stopwatch.StartNew();
        const int passes = 10;
        for (int i = 0; i < passes; i++)
        {
            matcher.SetFrame(frame, width, height);
            foreach (var p in pictures) matcher.FindFirst(p, 0.85, ScanPriority.Bottom);
        }
        double perPass = clock.Elapsed.TotalMilliseconds / passes;
        output.WriteLine($"1200 x 800 area, 4 pictures: {perPass:0.0} ms per pass");
        Assert.True(perPass < 150, $"{perPass} ms per pass");
    }
}

[Collection("Timing")]
public class AreaWatchPictureEngineTests
{
    [Fact]
    public void SameColouredTargetsGetTheButtonOfThePictureTheyLookLike()
    {
        const int W = 300, H = 200;
        var frame = new int[W * H];
        void Redraw(bool square, bool disc)
        {
            Array.Fill(frame, 0x06070C);
            if (square) TemplateMatcherTests.Draw(frame, W, "square", 80, 60);
            if (disc) TemplateMatcherTests.Draw(frame, W, "disc", 220, 140);
        }
        Redraw(true, true);
        string Encode(int cx, int cy)
        {
            var (pixels, size) = TemplateMatcherTests.Snip(frame, W, cx, cy);
            return TemplateImage.Encode(pixels, size, size);
        }

        // Whatever is clicked disappears, as in the game.
        bool squareAlive = true, discAlive = true;
        var clicks = new List<(int X, int Y, string Event)>();
        var input = new FakeInput();
        input.Clicked = (button, cx, cy) =>
        {
            clicks.Add((cx - 1000, cy - 500, $"click {button}"));
            if (Math.Abs(cx - 1000 - 220) < 20) discAlive = false; else squareAlive = false;
            Redraw(squareAlive, discAlive);
        };
        var screen = new FakeScreen { Capture = (_, _, _, _) => (int[])frame.Clone() };
        var profile = new Profile { IntervalMs = 10, StartDelayMs = 0, CornerStop = false };
        profile.Actions.Add(new AreaWatchAction
        {
            X = 1000, Y = 500, Width = W, Height = H, Priority = ScanPriority.Bottom, PollMs = 10,
            TimeoutMs = 300, OnTimeout = PixelTimeoutBehavior.Stop,
            PictureMatch = PictureMatch.Exact, // same colour, different outline
            Rules =
            {
                new AreaRule { Image = Encode(80, 60), MatchPercent = 85, Button = ClickButton.Left },      // squares: left click
                new AreaRule { Image = Encode(220, 140), MatchPercent = 85, Button = ClickButton.Right },   // discs: right click
            },
        });

        using var timing = new TimingEngine();
        using var engine = new ClickEngine(input, screen, new FakeWindows(), timing);
        using var finished = new ManualResetEventSlim();
        StopReason reason = default;
        engine.Stopped += (r, _) => { reason = r; finished.Set(); };
        Assert.True(engine.Start(profile));
        Assert.True(finished.Wait(10_000));

        Assert.Equal(StopReason.PixelTimeout, reason);
        Assert.Equal(2, clicks.Count);
        // Bottom priority: the disc (lower on screen) first, with the RIGHT button, on its centre.
        Assert.Equal("click Right", clicks[0].Event);
        Assert.InRange(clicks[0].X, 218, 222);
        Assert.InRange(clicks[0].Y, 138, 142);
        // Then the square with the LEFT button.
        Assert.Equal("click Left", clicks[1].Event);
        Assert.InRange(clicks[1].X, 78, 82);
        Assert.InRange(clicks[1].Y, 58, 62);
    }
}
