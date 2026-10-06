using PizzaHeroClicker.Models;
using PizzaHeroClicker.Services;
using Xunit;
using static PizzaHeroClicker.Native.NativeMethods;

namespace PizzaHeroClicker.Tests;

public class KeyComboTests
{
    [Theory]
    [InlineData("F6", 0x75, KeyMods.None)]
    [InlineData("Ctrl+Shift+F6", 0x75, KeyMods.Ctrl | KeyMods.Shift)]
    [InlineData("ctrl + alt + a", 0x41, KeyMods.Ctrl | KeyMods.Alt)]
    [InlineData("5", 0x35, KeyMods.None)]
    [InlineData("Win+Space", 0x20, KeyMods.Win)]
    public void Parses(string text, int vk, KeyMods mods)
    {
        Assert.True(KeyCombo.TryParse(text, out var combo));
        Assert.Equal(new KeyCombo(vk, mods), combo);
    }

    [Theory]
    [InlineData("Ctrl+")]
    [InlineData("NotAKey")]
    [InlineData("A+B")]
    public void RejectsInvalid(string text) => Assert.False(KeyCombo.TryParse(text, out _));

    [Fact]
    public void EmptyMeansUnbound()
    {
        Assert.True(KeyCombo.TryParse("", out var combo));
        Assert.True(combo.IsEmpty);
    }

    [Fact]
    public void DisplayStringRoundTripsForEveryNamedKey()
    {
        for (int vk = 1; vk < 255; vk++)
        {
            var combo = new KeyCombo(vk, KeyMods.Ctrl | KeyMods.Shift);
            Assert.True(KeyCombo.TryParse(combo.ToString(), out var parsed), $"vk {vk} -> '{combo}'");
            // Some virtual keys alias to the same WPF key; the display string must still be stable.
            Assert.Equal(combo.ToString(), parsed.ToString());
        }
    }
}

public class PixelColorTests
{
    [Fact]
    public void ParsesAndFormatsHex()
    {
        Assert.True(PixelColor.TryParse("#1A2b3C", out var c));
        Assert.Equal(new PixelColor(0x1A, 0x2B, 0x3C), c);
        Assert.Equal("#1A2B3C", c.ToHex());
        Assert.False(PixelColor.TryParse("#12345", out _));
        Assert.False(PixelColor.TryParse("red", out _));
    }

    [Fact]
    public void ToleranceIsPerChannel()
    {
        var a = new PixelColor(100, 100, 100);
        Assert.True(a.Matches(new PixelColor(105, 95, 100), 5));
        Assert.False(a.Matches(new PixelColor(106, 100, 100), 5));
    }
}

public class ProfileJsonTests
{
    [Fact]
    public void EveryActionTypeSurvivesARoundTrip()
    {
        var profile = new Profile { Name = "Round trip", IntervalMs = 250, Order = RunOrder.Random, JitterPx = 4 };
        profile.Actions.Add(new ClickAction { X = -1920, Y = 30, Button = ClickButton.Right, Kind = ClickKind.Double, HoldMs = 20, IntervalMs = 5, IntervalMaxMs = 9 });
        profile.Actions.Add(new KeyPressAction { Keys = { KeyCombo.Parse("Ctrl+C"), KeyCombo.Parse("Ctrl+V") }, HoldMs = 10 });
        profile.Actions.Add(new WaitAction { Ms = 750 });
        profile.Actions.Add(new ScrollAction { Direction = ScrollDirection.Up, Amount = 5 });
        profile.Actions.Add(new DragAction { X1 = 1, Y1 = 2, X2 = 3, Y2 = 4, DurationMs = 400 });
        profile.Actions.Add(new MoveAction { X = 9, Y = 8, DurationMs = 12 });
        profile.Actions.Add(new PixelWaitAction { X = 5, Y = 6, Color = "#FF8800", Tolerance = 3, OnTimeout = PixelTimeoutBehavior.Stop });
        profile.Actions.Add(new PixelClickAction { X = 7, Y = 8, Color = "#00FF00", ClickAtPixel = false, ClickX = 70, ClickY = 80 });
        profile.Hotkeys.Toggle = KeyCombo.Parse("Ctrl+F6");
        profile.Repeat.Mode = RepeatMode.UntilTime;
        profile.Window.Title = "Game";

        string json = ProfileJson.Serialize(profile);
        var copy = ProfileJson.Deserialize<Profile>(json)!;

        Assert.Equal(json, ProfileJson.Serialize(copy));
        Assert.Equal(8, copy.Actions.Count);
        Assert.Equal(profile.Actions.Select(a => a.GetType()), copy.Actions.Select(a => a.GetType()));
        Assert.Equal(-1920, ((ClickAction)copy.Actions[0]).X);
        Assert.Equal(2, ((KeyPressAction)copy.Actions[1]).Keys.Count);
        Assert.Contains("\"type\": \"click\"", json);
        Assert.Contains("\"toggle\": \"Ctrl+F6\"", json);
        Assert.DoesNotContain("summary", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("points", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("typeName", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMarkerComparisonCountsMatchingPixelsSoPartialCoverOnlyLowersItInProportion()
    {
        var reference = new int[100];
        for (int i = 0; i < 100; i++) reference[i] = i % 2 == 0 ? 0xFFFFFF : 0x103050;
        var live = (int[])reference.Clone();
        Assert.Equal(1.0, PizzaHeroClicker.Services.ScreenAnchor.Similarity(live, reference));

        for (int i = 0; i < 25; i++) live[i] = 0xE04030;                       // a rock covers a quarter of it
        Assert.Equal(0.75, PizzaHeroClicker.Services.ScreenAnchor.Similarity(live, reference), 2);

        for (int i = 0; i < 100; i++) live[i] = reference[i] == 0xFFFFFF ? 0xF0F0F0 : 0x182840; // slightly different rendering
        Assert.Equal(1.0, PizzaHeroClicker.Services.ScreenAnchor.Similarity(live, reference));

        Assert.True(PizzaHeroClicker.Services.ScreenAnchor.Similarity(new int[100], reference) < 0.05);  // a different screen
    }

    [Fact]
    public void NormalizeRepairsBadValues()
    {
        var profile = ProfileJson.Deserialize<Profile>("""{ "name": " ", "intervalMs": 0, "speedMultiplier": 0, "actions": [ { "type": "wait", "ms": -5 } ] }""")!;
        profile.Normalize();
        Assert.Equal("Default", profile.Name);
        Assert.Equal(1, profile.IntervalMs);
        Assert.Equal(0.1, profile.SpeedMultiplier);
        Assert.Equal(0, ((WaitAction)profile.Actions[0]).Ms);
    }

    [Fact]
    public void UntilTimeRollsOverToTomorrow()
    {
        var repeat = new RepeatSettings { UntilTime = "08:30" };
        var now = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 3, 1, 9, 0, 0)));
        long stop = repeat.ResolveUntilEpochMs(now)!.Value;
        Assert.Equal(TimeSpan.FromHours(23.5).TotalMilliseconds, stop - now.ToUnixTimeMilliseconds());
        Assert.Null(new RepeatSettings { UntilTime = "25:00" }.ResolveUntilEpochMs(now));
    }
}

public class CoordinateTests
{
    /// <summary>Windows maps a normalised value back to a pixel with a truncating divide (by 65536, or 65535 by some accounts).</summary>
    [Theory]
    [InlineData(1920)]
    [InlineData(2560)]
    [InlineData(3440)]
    [InlineData(5760)]   // three 1080p monitors
    [InlineData(7680)]
    [InlineData(1366)]
    public void NormalisedCoordinateMapsBackToTheSamePixel(int size)
    {
        for (int pixel = 0; pixel < size; pixel++)
        {
            long n = InputService.NormalizeAbsolute(pixel, size);
            Assert.InRange(n, 0, 65535);
            Assert.Equal(pixel, (int)(n * size / 65536));
            Assert.Equal(pixel, (int)(n * size / 65535));
        }
    }

    [Fact]
    public void StuckCornersIgnoreSeamsBetweenMonitors()
    {
        // A 1920x1080 primary with a second monitor to its LEFT (negative coordinates).
        RECT[] monitors =
        [
            new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
            new RECT { Left = -1920, Top = 0, Right = 0, Bottom = 1080 },
        ];

        Assert.True(ScreenService.IsStuckCorner(1919, 0, monitors, 2));      // outer top-right
        Assert.True(ScreenService.IsStuckCorner(-1920, 1079, monitors, 2));  // outer bottom-left, negative x
        Assert.False(ScreenService.IsStuckCorner(0, 0, monitors, 2));        // seam between the two monitors
        Assert.False(ScreenService.IsStuckCorner(-1, 1079, monitors, 2));    // other side of the seam
        Assert.False(ScreenService.IsStuckCorner(900, 0, monitors, 2));      // top edge, not a corner
        Assert.False(ScreenService.IsStuckCorner(900, 500, monitors, 2));
    }
}
