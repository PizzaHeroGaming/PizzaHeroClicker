using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PizzaHeroClicker.Models;

/// <summary>
/// A start/stop marker: a piece of the screen that shows only while a game is on. Actions that
/// have one wait for it to appear, act only while it shows, and end once it has gone.
/// </summary>
public interface IHasMarker
{
    string AnchorImage { get; }
    int AnchorX { get; }
    int AnchorY { get; }
    int AnchorWidth { get; }
    int AnchorHeight { get; }
    int AnchorMatchPercent { get; }
    bool HasAnchor { get; }
}

/// <summary>One kind of thing to block, recognised by a colour it carries.</summary>
public partial class BlockTarget : ObservableObject
{
    [ObservableProperty] private string _name = "";
    /// <summary>"#RRGGBB".</summary>
    [ObservableProperty] private string _color = "#FFFFFF";
    /// <summary>Allowed difference per colour channel (0-255).</summary>
    [ObservableProperty] private int _tolerance = 40;
}

/// <summary>
/// Steers a bar round a circle with two keys to block things flying outward from its centre
/// (a "shield" style game). Each look it finds the bar's angle on its circle and the incoming
/// things, picks the one to block (the most valuable it can reach in time, otherwise the one
/// about to get past), and holds whichever key turns the bar towards it.
/// Positions use the same coordinates as everything else in the profile.
/// </summary>
public partial class BarBlockAction : ActionBase, IHasMarker
{
    /// <summary>The middle of the circle the bar travels round (where things fly out from).</summary>
    [ObservableProperty] private int _centreX;
    [ObservableProperty] private int _centreY;
    /// <summary>How far from the centre the bar travels, in pixels.</summary>
    [ObservableProperty] private int _radius = 300;
    /// <summary>The bar's colour, "#RRGGBB", and how far its pixels may differ from it.</summary>
    [ObservableProperty] private string _barColor = "#F03232";
    [ObservableProperty] private int _barTolerance = 40;
    /// <summary>Things nearer the centre than this are not chased yet (where they come out is often busy).</summary>
    [ObservableProperty] private int _innerRadius = 40;

    /// <summary>What to block, most valuable first: when there is time for only one, the first in the list wins.</summary>
    [ObservableProperty] private ObservableCollection<BlockTarget> _targets = new();

    /// <summary>The key that turns the bar clockwise, and the one that turns it anticlockwise. Checked at the start of each run.</summary>
    [ObservableProperty] private KeyCombo _clockwiseKey = new(0x41);      // A
    [ObservableProperty] private KeyCombo _anticlockwiseKey = new(0x44);  // D

    /// <summary>Close enough: within this many degrees of the target the bar is left where it is.</summary>
    [ObservableProperty] private int _aimToleranceDegrees = 3;

    /// <summary>
    /// How many bars travel round the circle together, evenly spaced (2 = one opposite the other).
    /// Only the bar that was picked is looked for; the others are where the spacing puts them, and
    /// whichever is nearest is used to block each thing.
    /// </summary>
    [ObservableProperty] private int _barCount = 1;

    /// <summary>
    /// Where the other bars sit when they are not evenly spaced: degrees clockwise round from the
    /// picked bar, separated by commas ("90, 180"). Empty: <see cref="BarCount"/> bars, evenly spaced.
    /// </summary>
    [ObservableProperty] private string _otherBars = "";

    /// <summary>
    /// Every bar's place, in degrees clockwise from the picked bar (which is 0, and always first):
    /// from <see cref="OtherBars"/> when it gives any, otherwise <see cref="BarCount"/> evenly spaced.
    /// </summary>
    public double[] BarOffsets()
    {
        var given = ParseOtherBars(OtherBars);
        if (given.Count > 0) return [0, .. given];
        int count = Math.Max(1, BarCount);
        return Enumerable.Range(0, count).Select(k => k * 360.0 / count).ToArray();
    }

    /// <summary>The angles in an "other bars" list, each brought into 0..360; anything unreadable, or 0, is left out.</summary>
    public static List<double> ParseOtherBars(string? text)
    {
        var angles = new List<double>();
        foreach (var part in (text ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double a)) continue;
            a = (a % 360 + 360) % 360;
            if (a > 0.5 && a < 359.5 && !angles.Any(b => Math.Abs(b - a) < 0.5)) angles.Add(a);
        }
        return angles;
    }

    /// <summary>Keep steering for this long. 0 = until the marker goes (or for ever without one).</summary>
    [ObservableProperty] private int _watchForMs;
    /// <summary>How often to look, in milliseconds.</summary>
    [ObservableProperty] private int _pollMs = 15;

    // Start/stop marker, as for an area watch.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnchor), nameof(AnchorText))]
    private string _anchorImage = "";
    [ObservableProperty] private int _anchorX;
    [ObservableProperty] private int _anchorY;
    [ObservableProperty] private int _anchorWidth;
    [ObservableProperty] private int _anchorHeight;
    [ObservableProperty] private int _anchorMatchPercent = 80;

    public bool HasAnchor => !string.IsNullOrEmpty(AnchorImage);
    public string AnchorText => HasAnchor
        ? "this is showing: starts when it appears, ends when it goes"
        : "(no marker: it starts straight away)";

    public override string TypeName => "Block";

    public override string Summary =>
        $"Steer {(BarOffsets().Length is var bars && bars == 1 ? "a bar" : $"{bars} bars")} round ({CentreX}, {CentreY}), {Radius} px out, with {ClockwiseKey.Display} / {AnticlockwiseKey.Display}"
        + (HasAnchor ? " while its marker shows" : "")
        + (WatchForMs > 0 ? $" for {(HasAnchor ? "up to " : "")}{WatchForMs / 1000.0:0.#} s" : "")
        + (Targets.Count == 0 ? ": nothing to block yet" : $": block {string.Join(", ", Targets.Select(t => t.Name.Length > 0 ? t.Name : t.Color))}");

    /// <summary>The square of screen the action looks at: the bar's whole circle plus a margin.</summary>
    public (int X, int Y, int Size) LookBox => (CentreX - Radius - LookMargin, CentreY - Radius - LookMargin, 2 * (Radius + LookMargin));
    public const int LookMargin = 50;

    public override IEnumerable<(int X, int Y)> GetPoints() => [(CentreX, CentreY), (CentreX + Radius, CentreY)];

    public override void Offset(int dx, int dy) { CentreX += dx; CentreY += dy; AnchorX += dx; AnchorY += dy; }

    public override void Normalize()
    {
        base.Normalize();
        Targets ??= new();
        AnchorImage ??= "";
        BarColor ??= "#F03232";
        AnchorMatchPercent = Math.Clamp(AnchorMatchPercent, 30, 100);
        Radius = Math.Clamp(Radius, 20, 4000);
        InnerRadius = Math.Clamp(InnerRadius, 0, Radius - 10);
        BarTolerance = Math.Clamp(BarTolerance, 0, 255);
        AimToleranceDegrees = Math.Clamp(AimToleranceDegrees, 1, 45);
        BarCount = Math.Clamp(BarCount, 1, 8);
        OtherBars ??= "";
        WatchForMs = Math.Max(0, WatchForMs);
        PollMs = Math.Clamp(PollMs, 5, 1000);
        foreach (var target in Targets)
        {
            target.Name ??= "";
            target.Color ??= "#FFFFFF";
            target.Tolerance = Math.Clamp(target.Tolerance, 0, 255);
        }
    }
}
