using PizzaHeroClicker.Models;
using PizzaHeroClicker.Services;
using Xunit;
using Xunit.Abstractions;

namespace PizzaHeroClicker.Tests;

/// <summary>
/// Appearance matching: objects are separated from a plain backdrop and recognised by their
/// colour make-up, so rotation, size and partial pictures must not matter.
/// </summary>
public class AppearanceTests(ITestOutputHelper output)
{
    private const int W = 600, H = 400;
    private const int Navy = 0x1C344C;

    private static int[] Frame(int backdrop = Navy)
    {
        var pixels = new int[W * H];
        Array.Fill(pixels, backdrop);
        return pixels;
    }

    /// <summary>A repeatable surface grain, so drawn rocks are not a single flat colour (real sprites never are).</summary>
    private static int Grain(int rgb, double u, double v)
    {
        int n = (int)(Math.Floor(u / 3) * 7349 + Math.Floor(v / 3) * 9151);
        int shade = (int)((uint)(n * 2654435761u) >> 26) - 32; // -32..31: real sprite surfaces vary a lot
        int Channel(int shift) => Math.Clamp(((rgb >> shift) & 0xFF) + shade, 0, 255);
        return Channel(16) << 16 | Channel(8) << 8 | Channel(0);
    }

    /// <summary>
    /// Draws a target centred on (cx, cy), scaled and rotated. "orange" and "white" are mottled
    /// rocks; "satellite" is a grey body between two black panels.
    /// </summary>
    private static void Draw(int[] pixels, int width, int height, string kind, int cx, int cy, double scale = 1, double degrees = 0)
    {
        double cos = Math.Cos(degrees * Math.PI / 180), sin = Math.Sin(degrees * Math.PI / 180);
        int reach = (int)(45 * scale);
        for (int dy = -reach; dy <= reach; dy++)
        for (int dx = -reach; dx <= reach; dx++)
        {
            int x = cx + dx, y = cy + dy;
            if (x < 0 || y < 0 || x >= width || y >= height) continue;
            // Back into the target's own upright, unscaled coordinates.
            double u = (dx * cos + dy * sin) / scale, v = (-dx * sin + dy * cos) / scale;
            int colour = Colour(kind, u, v);
            if (colour >= 0) pixels[y * width + x] = colour;
        }
    }

    private static int Colour(string kind, double u, double v)
    {
        bool Ellipse(double ox, double oy, double rx, double ry) => (u - ox) * (u - ox) / (rx * rx) + (v - oy) * (v - oy) / (ry * ry) <= 1;
        switch (kind)
        {
            case "orange":
                if (!Ellipse(0, 0, 24, 18)) return -1;
                if (Ellipse(-8, 5, 5, 4) || Ellipse(9, -4, 4, 3)) return 0xE07A2A;   // glowing patches
                if (Ellipse(-10, -9, 8, 3)) return 0x807878;                         // grey streak
                return Grain(0x5A3830, u, v);
            case "white":
                if (!Ellipse(0, 0, 24, 18)) return -1;
                return Grain(Ellipse(-10, -6, 5, 4) || Ellipse(8, 7, 4, 3) ? 0xBCBCBC : 0xDCDCDC, u, v); // light, with grey dimples
            default: // satellite
                if (Math.Abs(u) <= 8 && Math.Abs(v) <= 12) return 0xB8B8B8;          // body
                if (Math.Abs(u) <= 30 && Math.Abs(v) <= 6) return 0x080808;          // panels
                return -1;
        }
    }

    /// <summary>A picture of the target as a user might snip it: upright, and optionally only its middle part.</summary>
    private static string Picture(string kind, bool cropped, int backdrop = Navy)
    {
        const int size = 100;
        var canvas = new int[size * size];
        Array.Fill(canvas, backdrop);
        Draw(canvas, size, size, kind, 50, 50);
        int w = cropped ? 36 : 70, h = cropped ? 22 : 46;
        var crop = new int[w * h];
        for (int y = 0; y < h; y++) Array.Copy(canvas, (50 - h / 2 + y) * size + 50 - w / 2, crop, y * w, w);
        return TemplateImage.Encode(crop, w, h);
    }

    private static AreaWatchAction Watch(bool cropped = true, ScanPriority priority = ScanPriority.Center, int pictureBackdrop = Navy) => new()
    {
        Width = W, Height = H, Priority = priority,
        Rules =
        {
            new AreaRule { Image = Picture("satellite", cropped, pictureBackdrop), Button = ClickButton.Right },   // rule 0
            new AreaRule { Image = Picture("orange", cropped, pictureBackdrop), Button = ClickButton.Left },       // rule 1
            new AreaRule { Image = Picture("white", cropped, pictureBackdrop), Button = ClickButton.Left },        // rule 2
        },
    };

    [Theory]
    [InlineData("satellite", 0, Navy, 0x000000)]   // pictures snipped on navy (a menu), game played on black
    [InlineData("orange", 1, Navy, 0x000000)]
    [InlineData("white", 2, Navy, 0x000000)]
    // The other way round. (Not the satellite: black panels snipped on black cannot be told from
    // backdrop, so a picture should be taken where the backdrop differs from the target's own colours.)
    [InlineData("orange", 1, 0x000000, Navy)]
    [InlineData("white", 2, 0x000000, Navy)]
    [InlineData("satellite", 0, 0x30A040, 0x000000)]   // a green preview panel
    [InlineData("white", 2, 0x30A040, 0x000000)]
    public void PicturesSnippedOnOneBackdropWorkInAGameWithAnother(string kind, int expectedRule, int pictureBackdrop, int gameBackdrop)
    {
        // Whole-target pictures with their surround, as people normally snip them.
        var detector = new AreaDetector(Watch(cropped: false, pictureBackdrop: pictureBackdrop)) { CollectReport = true };
        var random = new Random(11);
        foreach (double degrees in new[] { 0, 50, 135, 260 })
        {
            var frame = Frame(gameBackdrop);
            for (int i = 0; i < 150; i++) frame[random.Next(H) * W + random.Next(W)] = 0xD8D8E8; // stars
            Draw(frame, W, H, kind, 310, 190, 1, degrees);

            var target = detector.Find(frame);

            string scores = string.Join(", ", detector.LastReport!.Pictures.Select(p => $"{p.Best:0.00}"));
            Assert.True(target is not null, $"{kind} at {degrees} deg was not found (likeness: {scores})");
            Assert.True(target.Value.Rule == expectedRule, $"{kind} at {degrees} deg was taken for rule {target.Value.Rule} (likeness: {scores})");
            Assert.InRange(target.Value.X, 295, 325);
            Assert.InRange(target.Value.Y, 175, 205);
            Assert.Equal(1, detector.LastReport.ObjectsFound); // the stars are not objects
        }
    }

    [Theory]
    [InlineData("satellite", 0)]
    [InlineData("orange", 1)]
    [InlineData("white", 2)]
    public void ATargetIsRecognisedAtAnyAngleAndSizeFromACroppedPicture(string kind, int expectedRule)
    {
        var detector = new AreaDetector(Watch(cropped: true)) { CollectReport = true };
        foreach (double degrees in new[] { 0, 30, 77, 145, 200, 290 })
        foreach (double scale in new[] { 0.7, 1.0, 1.4 })
        {
            var frame = Frame();
            Draw(frame, W, H, kind, 310, 190, scale, degrees);

            var target = detector.Find(frame);

            string scores = string.Join(", ", detector.LastReport!.Pictures.Select(p => $"{p.Best:0.00}"));
            Assert.True(target is not null, $"{kind} at {degrees} deg x{scale} was not found (likeness: {scores})");
            Assert.True(target.Value.Rule == expectedRule, $"{kind} at {degrees} deg x{scale} was taken for rule {target.Value.Rule} (likeness: {scores})");
            Assert.InRange(target.Value.X, 300, 320);
            Assert.InRange(target.Value.Y, 180, 200);
        }
        output.WriteLine($"{kind}: likeness to [satellite, orange, white] at the last pose = " +
                         string.Join(", ", detector.LastReport!.Pictures.Select(p => $"{p.Best:0.00}")));
    }

    [Fact]
    public void TheWhiteRockAndTheSatelliteAreNotConfused()
    {
        // Both are light grey / white; the satellite also has black panels. This is the pair that shares colours.
        var frame = Frame();
        Draw(frame, W, H, "white", 150, 120, 1.1, 40);
        Draw(frame, W, H, "satellite", 450, 280, 1.0, 110);
        var action = Watch(priority: ScanPriority.Left);

        var first = new AreaDetector(action).Find(frame)!.Value;
        Assert.Equal(2, first.Rule);                 // the white rock, on the left
        Assert.InRange(first.X, 140, 160);

        action.Priority = ScanPriority.Right;
        var second = new AreaDetector(action).Find(frame)!.Value;
        Assert.Equal(0, second.Rule);                // the satellite, on the right
        Assert.InRange(second.X, 440, 460);
    }

    [Fact]
    public void CenterPriorityTakesTheTargetNearestTheMiddle()
    {
        var frame = Frame();
        Draw(frame, W, H, "orange", 60, 60);
        Draw(frame, W, H, "satellite", 360, 230, 1, 25);   // closest to (300, 200)
        Draw(frame, W, H, "white", 540, 340);

        var target = new AreaDetector(Watch()).Find(frame)!.Value;

        Assert.Equal(0, target.Rule);
        Assert.InRange(target.X, 350, 370);
    }

    [Fact]
    public void HugeObjectsSpecksAndUnknownThingsAreLeftAlone()
    {
        var frame = Frame();
        // A "planet": far larger than any picture, even though it is rock-coloured.
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
            if ((x - 300) * (x - 300) + (y - 200) * (y - 200) <= 120 * 120) frame[y * W + x] = 0x4A3028;
        // Stars.
        var random = new Random(5);
        for (int i = 0; i < 80; i++) frame[random.Next(H) * W + random.Next(W)] = 0xFFFFFF;
        // Something that is none of the targets: a bright green block.
        for (int y = 30; y < 60; y++)
        for (int x = 500; x < 540; x++)
            frame[y * W + x] = 0x20E040;

        var detector = new AreaDetector(Watch()) { CollectReport = true };
        Assert.Null(detector.Find(frame));
        Assert.Contains("would not click", detector.DescribeLastReport());
    }

    [Fact]
    public void TheTestReportSaysWhatWasSeen()
    {
        var frame = Frame();
        Draw(frame, W, H, "satellite", 200, 150, 1, 60);
        var action = Watch();
        action.X = 1000;
        action.Y = 500;
        var detector = new AreaDetector(action) { CollectReport = true };

        detector.Find(frame);
        string text = detector.DescribeLastReport();
        output.WriteLine(text);

        Assert.Contains("Plain backdrop, colour #1C344C", text);
        Assert.Contains("objects seen in the area: 1", text);
        Assert.Contains("MATCH  Picture 1 (right click)", text);
        Assert.Contains("no     Picture 3 (left click)", text);
        Assert.Contains("right-click rule 1 at (12", text); // area position + where it was found
    }

    [Fact]
    public void BadRulesAreReportedInPlainWords()
    {
        var empty = new AreaWatchAction();
        Assert.Contains("nothing to look for", Assert.Throws<InvalidOperationException>(() => new AreaDetector(empty)).Message);

        var broken = new AreaWatchAction { Rules = { new AreaRule { Image = "not a picture" } } };
        Assert.Contains("Picture 1", Assert.Throws<InvalidOperationException>(() => new AreaDetector(broken)).Message);
    }
}
