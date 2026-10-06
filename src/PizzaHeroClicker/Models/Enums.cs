using System.Globalization;

namespace PizzaHeroClicker.Models;

public enum ClickButton { Left, Right, Middle }

public enum ClickKind { Single, Double }

public enum RunOrder { Sequential, Random }

public enum ScrollDirection { Up, Down, Left, Right }

public enum PixelTimeoutBehavior { Skip, Stop }

/// <summary>When a run ends on its own.</summary>
public enum RepeatMode
{
    Infinite,
    /// <summary>Stop after N executed actions (N clicks in plain clicker mode).</summary>
    Count,
    /// <summary>Stop after N full passes through the action list.</summary>
    Loops,
    Duration,
    /// <summary>Stop at a clock time (today, or tomorrow if that time has already passed).</summary>
    UntilTime,
}

public enum HotkeyMode { Toggle, Hold }

/// <summary>
/// Which target an area watch deals with first: the one nearest this edge of the area, or
/// nearest its centre (for something in the middle being approached from all sides).
/// </summary>
public enum ScanPriority { Top, Bottom, Left, Right, Center }

/// <summary>How an area watch compares what it sees with its target pictures.</summary>
public enum PictureMatch
{
    /// <summary>
    /// By colour make-up, after separating each object from a plain backdrop. Copes with
    /// targets that spin, change size, or were only partly captured.
    /// </summary>
    Appearance,
    /// <summary>Pixel pattern for pixel pattern. Tells apart same-coloured shapes, but the target must not rotate or resize.</summary>
    Exact,
}

/// <summary>An RGB colour sampled from the screen.</summary>
public readonly record struct PixelColor(byte R, byte G, byte B)
{
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>True when every channel is within <paramref name="tolerance"/> of the other colour.</summary>
    public bool Matches(PixelColor other, int tolerance) =>
        Math.Abs(R - other.R) <= tolerance && Math.Abs(G - other.G) <= tolerance && Math.Abs(B - other.B) <= tolerance;

    public static bool TryParse(string? text, out PixelColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return false;
        color = new PixelColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }
}

/// <summary>Value lists for combo boxes.</summary>
public static class EnumValues
{
    public static ClickButton[] ClickButtons { get; } = Enum.GetValues<ClickButton>();
    public static ClickKind[] ClickKinds { get; } = Enum.GetValues<ClickKind>();
    public static RunOrder[] RunOrders { get; } = Enum.GetValues<RunOrder>();
    public static ScrollDirection[] ScrollDirections { get; } = Enum.GetValues<ScrollDirection>();
    public static PixelTimeoutBehavior[] PixelTimeoutBehaviors { get; } = Enum.GetValues<PixelTimeoutBehavior>();
    public static RepeatMode[] RepeatModes { get; } = Enum.GetValues<RepeatMode>();
    public static HotkeyMode[] HotkeyModes { get; } = Enum.GetValues<HotkeyMode>();
    public static ScanPriority[] ScanPriorities { get; } = Enum.GetValues<ScanPriority>();
    public static PictureMatch[] PictureMatches { get; } = Enum.GetValues<PictureMatch>();
}
