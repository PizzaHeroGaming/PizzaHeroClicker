using PizzaHeroClicker.Models;
using PizzaHeroClicker.Services;
using Xunit;

namespace PizzaHeroClicker.Tests;

public class AreaScannerTests
{
    private const int W = 300, H = 200;
    private const int Rock = 0xC87830, Satellite = 0x40A8F0, Space = 0x05060A;

    private static readonly AreaColorRule[] Rules =
    [
        new(new PixelColor(0xC8, 0x78, 0x30), 20),   // 0: meteorite
        new(new PixelColor(0x40, 0xA8, 0xF0), 20),   // 1: satellite
    ];

    private static int[] Frame()
    {
        var pixels = new int[W * H];
        Array.Fill(pixels, Space);
        return pixels;
    }

    private static void Disc(int[] pixels, int cx, int cy, int radius, int rgb)
    {
        for (int y = cy - radius; y <= cy + radius; y++)
        for (int x = cx - radius; x <= cx + radius; x++)
        {
            if (x < 0 || y < 0 || x >= W || y >= H) continue;
            if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius) pixels[y * W + x] = rgb;
        }
    }

    [Fact]
    public void EmptySpaceHasNoTargets() =>
        Assert.Null(new AreaScanner().Find(Frame(), W, H, Rules, 3, 12, ScanPriority.Bottom));

    [Fact]
    public void FindsTheCentreAndTheRightRule()
    {
        var pixels = Frame();
        Disc(pixels, 120, 80, 9, Satellite);

        var target = new AreaScanner().Find(pixels, W, H, Rules, 3, 12, ScanPriority.Top);

        Assert.NotNull(target);
        Assert.Equal(1, target.Value.RuleIndex);
        Assert.InRange(target.Value.X, 118, 122);
        Assert.InRange(target.Value.Y, 78, 82);
    }

    [Theory]
    [InlineData(ScanPriority.Top, 60, 30)]
    [InlineData(ScanPriority.Bottom, 200, 170)]
    [InlineData(ScanPriority.Left, 20, 100)]
    [InlineData(ScanPriority.Right, 280, 90)]
    [InlineData(ScanPriority.Center, 200, 170)]  // 86 px from the middle (150, 100); the others are 114, 130 and 130
    public void PriorityPicksTheTargetNearestThatEdge(ScanPriority priority, int expectedX, int expectedY)
    {
        var pixels = Frame();
        Disc(pixels, 60, 30, 8, Rock);
        Disc(pixels, 200, 170, 8, Rock);
        Disc(pixels, 20, 100, 8, Satellite);
        Disc(pixels, 280, 90, 8, Rock);

        var target = new AreaScanner().Find(pixels, W, H, Rules, 3, 12, priority)!.Value;

        Assert.InRange(target.X, expectedX - 3, expectedX + 3);
        Assert.InRange(target.Y, expectedY - 3, expectedY + 3);
    }

    [Fact]
    public void TwoTargetsOfOneColourAreNotAveragedTogether()
    {
        var pixels = Frame();
        Disc(pixels, 50, 100, 8, Rock);
        Disc(pixels, 250, 100, 8, Rock);

        var target = new AreaScanner().Find(pixels, W, H, Rules, 3, 12, ScanPriority.Left)!.Value;

        // The click must land on the left rock, not in the empty space half way between them.
        Assert.InRange(target.X, 47, 53);
        Assert.Equal(Rock, pixels[target.Y * W + target.X]);
    }

    [Fact]
    public void SpecksAreIgnoredButARealTargetBehindThemIsStillFound()
    {
        var pixels = Frame();
        for (int i = 0; i < 30; i++) pixels[(3 + i * 3) * W + 9 + i * 6] = Rock; // single-pixel "stars" nearer the top
        Disc(pixels, 150, 150, 8, Rock);

        var target = new AreaScanner().Find(pixels, W, H, Rules, 3, 12, ScanPriority.Top);

        Assert.NotNull(target);
        Assert.InRange(target.Value.X, 147, 153);
        Assert.InRange(target.Value.Y, 147, 153);
    }

    [Fact]
    public void ColoursOutsideTheToleranceDoNotMatch()
    {
        var pixels = Frame();
        Disc(pixels, 100, 100, 9, 0xC8A830); // same red and blue as the rock, green is 48 off
        Assert.Null(new AreaScanner().Find(pixels, W, H, Rules, 3, 12, ScanPriority.Top));
    }

    [Fact]
    public void TheFirstRuleWinsWhereTwoRulesOverlap()
    {
        AreaColorRule[] overlapping = [new(new PixelColor(0xC0, 0x70, 0x30), 30), new(new PixelColor(0xC8, 0x78, 0x30), 30)];
        var pixels = Frame();
        Disc(pixels, 100, 100, 9, Rock);
        Assert.Equal(0, new AreaScanner().Find(pixels, W, H, overlapping, 3, 12, ScanPriority.Top)!.Value.RuleIndex);
    }
}
