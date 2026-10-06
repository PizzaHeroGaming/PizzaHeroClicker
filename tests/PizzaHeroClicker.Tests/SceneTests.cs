using PizzaHeroClicker.Models;
using PizzaHeroClicker.Services;
using Xunit;

namespace PizzaHeroClicker.Tests;

/// <summary>
/// Busy backdrops (a painted scene, a planet, a HUD): targets are found by their movement
/// across a learned scene, and a marker colour such as a ring drawn around one kind of target
/// decides its button.
/// </summary>
public class SceneTests
{
    private const int W = 600, H = 400;
    private const int Rock = 0x6A4A3A, RockGlow = 0xE07A2A, Hull = 0xB0B0B8, Ring = 0xFF0000;

    /// <summary>A busy, unchanging scene: blotchy "nebula", a big "planet", a "HUD" bar.</summary>
    private static int[] Scene()
    {
        var pixels = new int[W * H];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int blotch = (x / 40 * 7 + y / 40 * 13) % 5;
            int colour = blotch switch { 0 => 0x101018, 1 => 0x2A2440, 2 => 0x403050, 3 => 0x182838, _ => 0x503828 };
            if ((x - 300) * (x - 300) + (y - 210) * (y - 210) < 110 * 110) colour = (x + y) % 60 < 30 ? 0x3A8090 : 0x70A0A0; // planet
            if (y < 24) colour = 0xE8E8E8; // HUD bar
            pixels[y * W + x] = colour;
        }
        return pixels;
    }

    private static void Disc(int[] pixels, int cx, int cy, int radius, int colour, int innerRadius = 0)
    {
        for (int y = Math.Max(0, cy - radius); y <= Math.Min(H - 1, cy + radius); y++)
        for (int x = Math.Max(0, cx - radius); x <= Math.Min(W - 1, cx + radius); x++)
        {
            int d = (x - cx) * (x - cx) + (y - cy) * (y - cy);
            if (d <= radius * radius && d >= innerRadius * innerRadius) pixels[y * W + x] = colour;
        }
    }

    private static void DrawRock(int[] pixels, int cx, int cy)
    {
        Disc(pixels, cx, cy, 20, Rock);
        Disc(pixels, cx - 6, cy + 4, 5, RockGlow);
        Disc(pixels, cx + 8, cy - 5, 4, RockGlow);
    }

    private static void DrawSatellite(int[] pixels, int cx, int cy, bool ringed)
    {
        Disc(pixels, cx, cy, 14, Hull);
        if (ringed) Disc(pixels, cx, cy, 30, Ring, 27);
    }

    /// <summary>A picture of the rock as snipped from a plain navy menu screen.</summary>
    private static string RockPicture()
    {
        const int size = 60;
        var canvas = new int[W * H];
        Array.Fill(canvas, 0x1C344C);
        DrawRock(canvas, 100, 100);
        var crop = new int[size * size];
        for (int y = 0; y < size; y++) Array.Copy(canvas, (70 + y) * W + 70, crop, y * size, size);
        return TemplateImage.Encode(crop, size, size);
    }

    private static AreaWatchAction Watch() => new()
    {
        Width = W, Height = H, Priority = ScanPriority.Center,
        Rules =
        {
            new AreaRule { Color = "#FF0000", Tolerance = 40, Button = ClickButton.Right },   // rule 0: the ring marks satellites
            new AreaRule { Image = RockPicture(), MatchPercent = 55, Button = ClickButton.Left }, // rule 1
        },
    };

    /// <summary>Feeds frames 100 ms apart; <paramref name="draw"/> adds the moving things for a given time.</summary>
    private static List<(long Ms, (int X, int Y, int Rule)? Target, AreaReport Report)> Play(AreaDetector detector, long fromMs, long toMs, Action<int[], long> draw)
    {
        detector.CollectReport = true;
        var results = new List<(long, (int X, int Y, int Rule)?, AreaReport)>();
        for (long ms = fromMs; ms <= toMs; ms += 100)
        {
            var frame = Scene();
            draw(frame, ms);
            var target = detector.Find(frame, ms);
            results.Add((ms, target, detector.LastReport!));
        }
        return results;
    }

    [Fact]
    public void AMovingRockIsFoundOnABusySceneAndTheSceneItselfIsNot()
    {
        var detector = new AreaDetector(Watch());
        // A rock crossing the scene, over the nebula and then over the planet, at 120 px a second.
        var results = Play(detector, 0, 4000, (frame, ms) => DrawRock(frame, 40 + (int)(ms * 0.12), 200));

        Assert.All(results, r => Assert.True(r.Report.BusyScene));
        Assert.True(results[0].Report.Learning);                       // nothing can be judged from one frame
        foreach (var (ms, target, report) in results.Where(r => r.Ms >= 2500))
        {
            Assert.True(target is not null, $"no target at {ms} ms");
            Assert.Equal(1, target.Value.Rule);
            Assert.InRange(target.Value.X, 40 + (int)(ms * 0.12) - 8, 40 + (int)(ms * 0.12) + 8);
            Assert.InRange(target.Value.Y, 192, 208);
            Assert.Equal(1, report.ObjectsFound);                      // the planet, nebula and HUD are scene, not objects
        }
    }

    [Fact]
    public void ATargetStillCrossingTheEdgeIsLeftUntilItIsFullyInside()
    {
        var detector = new AreaDetector(Watch());
        // The rock slides in from beyond the left edge at 60 px a second (radius 20).
        var results = Play(detector, 0, 5000, (frame, ms) => DrawRock(frame, -200 + (int)(ms * 0.06), 200));

        foreach (var (ms, target, _) in results)
        {
            int centreX = -200 + (int)(ms * 0.06);
            if (centreX < 24) Assert.True(target is null, $"clicked at {ms} ms while the rock was only partly in (centre x = {centreX})");
            if (centreX > 34) Assert.True(target is not null, $"not clicked at {ms} ms although the rock was fully in (centre x = {centreX})");
        }
    }

    [Fact]
    public void WhenTheWholeScreenChangesNothingOnTheNewScreenIsClicked()
    {
        var detector = new AreaDetector(Watch());
        var results = Play(detector, 0, 3000, (frame, ms) => DrawRock(frame, 60 + (int)(ms * 0.1), 320));
        Assert.NotNull(results[^1].Target); // playing normally...
        detector.NotifyClicked();           // ...and clicking

        // The game ends and an upgrade screen opens: a plain backdrop decorated with the very same rocks.
        static int[] Shop()
        {
            var shop = new int[W * H];
            Array.Fill(shop, 0x243048);
            for (int i = 0; i < 6; i++) DrawRock(shop, 80 + i * 90, 120 + i % 2 * 150);
            DrawSatellite(shop, 300, 60, ringed: true);
            return shop;
        }
        detector.CollectReport = true;
        for (long ms = 3100; ms <= 60_000; ms += ms < 9000 ? 100 : 2500)   // straight away, and a minute later
        {
            Assert.True(detector.Find(Shop(), ms) is null, $"clicked something on the new screen at {ms} ms");
            Assert.True(detector.LastReport!.ScreenChanged);
        }

        // If the game comes straight back, play carries on where it left off.
        var back = Scene();
        DrawRock(back, 200, 320);
        Assert.NotNull(detector.Find(back, 60_100));
        Assert.False(detector.IsScreenChanged);
    }

    [Fact]
    public void OnAPlainBackdropABigTargetVanishingIsNotMistakenForANewScreen()
    {
        // A small watched area around one large button-like target, which disappears when clicked
        // and comes back a little later. Every click changes well over a quarter of the area.
        const int w = 120, h = 100;
        var action = new AreaWatchAction
        {
            Width = w, Height = h,
            Rules = { new AreaRule { Color = "#E07A2A", Tolerance = 30, Button = ClickButton.Left } },
        };
        // A picture rule is needed for the appearance path; reuse the rock.
        action.Rules.Add(new AreaRule { Image = RockPicture(), MatchPercent = 55, Button = ClickButton.Left });
        var detector = new AreaDetector(action);

        int[] Frame(bool target)
        {
            var frame = new int[w * h];
            Array.Fill(frame, 0x1C344C);
            if (target)
                for (int y = 20; y < 80; y++)
                for (int x = 30; x < 90; x++)
                    frame[y * w + x] = 0xE07A2A; // 30% of the area
            return frame;
        }

        int found = 0;
        for (long ms = 0; ms <= 12_000; ms += 100)
        {
            bool showing = ms / 1000 % 2 == 0; // there for a second, gone for a second
            if (detector.Find(Frame(showing), ms) is not null)
            {
                found++;
                detector.NotifyClicked();
            }
            Assert.False(detector.IsScreenChanged, $"taken for a different screen at {ms} ms");
        }
        Assert.True(found > 40, $"the target was only found {found} times");
    }

    [Fact]
    public void AScreenChangeBeforeAnyClickIsTheGameOpeningAndIsLearnedAtOnce()
    {
        var detector = new AreaDetector(Watch());
        // The watch starts a moment early, while the previous (plain, empty) screen is still showing.
        var before = new int[W * H];
        Array.Fill(before, 0x243048);
        for (long ms = 0; ms <= 2000; ms += 100) Assert.Null(detector.Find(before, ms));

        // Then the game opens, with a rock flying. It must be picked up within about two seconds.
        (int X, int Y, int Rule)? target = null;
        long firstSeen = -1;
        for (long ms = 2100; ms <= 5000 && target is null; ms += 100)
        {
            var frame = Scene();
            DrawRock(frame, 60 + (int)((ms - 2100) * 0.1), 320);
            target = detector.Find(frame, ms);
            if (target is not null) firstSeen = ms;
        }
        Assert.NotNull(target);
        Assert.InRange(firstSeen, 2100, 4200);
    }

    [Fact]
    public void NothingIsClickedWhenNothingMoves()
    {
        var detector = new AreaDetector(Watch());
        var results = Play(detector, 0, 4000, (_, _) => { });
        Assert.All(results, r => Assert.Null(r.Target));
        Assert.Equal(0, results[^1].Report.ObjectsFound);
    }

    [Fact]
    public void ARingedSatelliteGetsTheMarkerColoursButtonAtItsCentre()
    {
        var detector = new AreaDetector(Watch());
        var results = Play(detector, 0, 4000, (frame, ms) =>
        {
            DrawSatellite(frame, 560 - (int)(ms * 0.1), 120, ringed: true);   // nearer the centre later on
            DrawRock(frame, 60, 330 - (int)(ms * 0.03));
        });

        var (_, target, report) = results[^1]; // at 4000 ms the satellite is at (160, 120), the rock at (60, 210)
        Assert.Equal(2, report.ObjectsFound);
        Assert.NotNull(target);
        Assert.Equal(0, target.Value.Rule);                 // right-click, because of the red ring
        Assert.InRange(target.Value.X, 152, 168);           // the ring's centre, where the satellite is
        Assert.InRange(target.Value.Y, 112, 128);
        Assert.Contains("carries colour #FF0000", detector.DescribeLastReport());
    }

    [Fact]
    public void WithoutItsRingASatelliteIsNotMistakenForARock()
    {
        var detector = new AreaDetector(Watch());
        var results = Play(detector, 0, 4000, (frame, ms) => DrawSatellite(frame, 560 - (int)(ms * 0.1), 120, ringed: false));
        Assert.Null(results[^1].Target);                    // grey, no ring: neither rule applies
        Assert.Equal(1, results[^1].Report.ObjectsFound);
    }

    [Fact]
    public void SomethingThatStopsBecomesPartOfTheScene()
    {
        var detector = new AreaDetector(Watch());
        // The rock flies in for two seconds, then sits still (a crater, wreckage).
        var results = Play(detector, 0, 14_000, (frame, ms) => DrawRock(frame, 40 + (int)(Math.Min(ms, 2000) * 0.12), 330));

        Assert.NotNull(results.First(r => r.Ms == 2600).Target);   // still fresh: seen
        Assert.Null(results[^1].Target);                           // long since settled: part of the scene
    }

    [Fact]
    public void APictureSnippedInTheGameHasItsSceneRemovedAndThenRecognisesThatTarget()
    {
        // Learn the scene while a pale rock flies across it, as the snip tool does before freezing.
        const int Pale = 0xA8A8A0, PaleShade = 0x585858;
        void DrawPale(int[] frame, int cx, int cy)
        {
            Disc(frame, cx, cy, 18, Pale);
            Disc(frame, cx + 6, cy + 5, 8, PaleShade);
        }
        var scene = new SceneModel();
        int[] frozen = [];
        for (long ms = 0; ms <= 3200; ms += 100)
        {
            frozen = Scene();
            DrawPale(frozen, 100 + (int)(ms * 0.1), 150); // ends at (420, 150), over the planet
            scene.Update(frozen, W, H, ms);
        }

        // Snip an 80 x 80 box around it from the frozen frame, then cut the scene away.
        const int sx = 380, sy = 110, size = 80;
        var snip = new int[size * size];
        for (int y = 0; y < size; y++) Array.Copy(frozen, (sy + y) * W + sx, snip, y * size, size);
        int kept = scene.KeyOutScene(snip, sx, sy, size, size, frozen, 0, 0, W, H);

        Assert.InRange(kept, 900, 2600);                                   // roughly the rock (about 1,000 px) plus a thin rim
        Assert.Equal(SceneModel.KeyColour, snip[0]);                       // corners are scene: keyed out
        Assert.Equal(Pale, snip[(150 - sy - 8) * size + (420 - sx - 8)]);  // the rock itself is untouched
        Assert.True(BlobFinder.IsBackground(SceneModel.KeyColour, BlobFinder.PictureBackdrop(snip, size, size)!.Value)); // the green is seen as the picture's surround

        // That picture now recognises pale rocks elsewhere, and does not claim the orange kind.
        var action = Watch();
        action.Rules.Add(new AreaRule { Image = TemplateImage.Encode(snip, size, size), MatchPercent = 55, Button = ClickButton.Middle }); // rule 2
        var detector = new AreaDetector(action);
        var results = Play(detector, 0, 4000, (frame, ms) =>
        {
            DrawPale(frame, 560 - (int)(ms * 0.1), 100);       // ends at (160, 100)
            DrawRock(frame, 60 + (int)(ms * 0.1), 320);        // ends at (460, 320)
        });

        var last = results[^1];
        Assert.Equal(2, last.Report.ObjectsFound);
        var byRule = last.Report.Objects.ToDictionary(o => o.BestRule, o => (o.X, o.Y, o.BestScore));
        Assert.InRange(byRule[2].X, 150, 172);                 // the pale rock goes to the new picture
        Assert.True(byRule[2].BestScore > 0.65, $"pale rock likeness {byRule[2].BestScore}");
        Assert.InRange(byRule[1].X, 450, 470);                 // the orange rock still goes to its own
    }

    [Fact]
    public void APlainBackdropStillFindsThingsThatAreNotMoving()
    {
        // The plain-backdrop method must be unaffected: a rock sitting still on navy is found at once.
        var detector = new AreaDetector(Watch()) { CollectReport = true };
        var frame = new int[W * H];
        Array.Fill(frame, 0x1C344C);
        DrawRock(frame, 320, 180);

        var target = detector.Find(frame, 0);

        Assert.False(detector.LastReport!.BusyScene);
        Assert.NotNull(target);
        Assert.Equal(1, target.Value.Rule);
        Assert.InRange(target.Value.X, 312, 328);
    }
}
