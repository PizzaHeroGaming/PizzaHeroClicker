using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PizzaHeroClicker.Models;

/// <summary>
/// One step in a profile's action list. All positions are physical pixels: virtual-screen
/// coordinates by default, or offsets from the target window's client area when the profile
/// uses window-relative coordinates.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ClickAction), "click")]
[JsonDerivedType(typeof(KeyPressAction), "key")]
[JsonDerivedType(typeof(WaitAction), "wait")]
[JsonDerivedType(typeof(ScrollAction), "scroll")]
[JsonDerivedType(typeof(DragAction), "drag")]
[JsonDerivedType(typeof(MoveAction), "move")]
[JsonDerivedType(typeof(PixelWaitAction), "pixelWait")]
[JsonDerivedType(typeof(PixelClickAction), "pixelClick")]
[JsonDerivedType(typeof(AreaWatchAction), "areaWatch")]
public abstract partial class ActionBase : ObservableObject
{
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private bool _enabled = true;

    /// <summary>Delay after this action, replacing the global interval. Null = use the global interval.</summary>
    [ObservableProperty] private int? _intervalMs;

    /// <summary>When set (and greater than <see cref="IntervalMs"/>), the delay is random in that range.</summary>
    [ObservableProperty] private int? _intervalMaxMs;

    // Read-only properties are skipped by the JSON serialiser (IgnoreReadOnlyProperties).
    public abstract string TypeName { get; }
    public abstract string Summary { get; }

    /// <summary>Any change to an action changes its one-line description, so keep list rows in sync.</summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Summary) or nameof(IntervalText)) return;
        base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Summary)));
        base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(IntervalText)));
    }

    public string IntervalText => IntervalMs switch
    {
        null => "",
        int lo when IntervalMaxMs is int hi && hi > lo => $"then {lo}–{hi} ms",
        int lo => $"then {lo} ms",
    };

    /// <summary>Screen points this action touches, for the position overlay.</summary>
    public virtual IEnumerable<(int X, int Y)> GetPoints() => [];

    /// <summary>Shifts every position, used when switching between absolute and window-relative coordinates.</summary>
    public virtual void Offset(int dx, int dy) { }

    /// <summary>Clamps values loaded from disk into sane ranges.</summary>
    public virtual void Normalize()
    {
        Label ??= "";
        if (IntervalMs is < 0) IntervalMs = 0;
        if (IntervalMaxMs is < 0) IntervalMaxMs = null;
    }

    public ActionBase Clone() => ProfileJson.Clone(this);

    protected static string At(bool useCursor, int x, int y) => useCursor ? "at cursor" : $"at ({x}, {y})";
}

public partial class ClickAction : ActionBase
{
    /// <summary>Click wherever the cursor already is instead of moving to X/Y.</summary>
    [ObservableProperty] private bool _useCursor;
    [ObservableProperty] private int _x;
    [ObservableProperty] private int _y;
    [ObservableProperty] private ClickButton _button;
    [ObservableProperty] private ClickKind _kind;
    /// <summary>How long the button stays down, in ms. 0 = press and release immediately.</summary>
    [ObservableProperty] private int _holdMs;
    /// <summary>Random offset of +/- this many pixels. Null = use the profile's jitter.</summary>
    [ObservableProperty] private int? _jitterPx;

    public override string TypeName => "Click";

    public override string Summary =>
        (Kind == ClickKind.Double ? $"Double {Button.ToString().ToLowerInvariant()}" : Button.ToString())
        + $" click {At(UseCursor, X, Y)}" + (HoldMs > 0 ? $", hold {HoldMs} ms" : "");

    public override IEnumerable<(int X, int Y)> GetPoints() => UseCursor ? [] : [(X, Y)];

    public override void Offset(int dx, int dy) { X += dx; Y += dy; }

    public override void Normalize()
    {
        base.Normalize();
        HoldMs = Math.Max(0, HoldMs);
        if (JitterPx is < 0) JitterPx = 0;
    }
}

public partial class KeyPressAction : ActionBase
{
    /// <summary>One or more key combinations, pressed in order (a single entry is a plain key press).</summary>
    [ObservableProperty] private ObservableCollection<KeyCombo> _keys = new();
    [ObservableProperty] private int _holdMs;
    /// <summary>Pause between entries when <see cref="Keys"/> holds a sequence.</summary>
    [ObservableProperty] private int _gapMs = 50;

    public override string TypeName => "Key";

    public override string Summary =>
        (Keys.Count == 0 ? "No keys set" : "Press " + string.Join(", ", Keys.Select(k => k.Display)))
        + (HoldMs > 0 ? $", hold {HoldMs} ms" : "");

    public override void Normalize()
    {
        base.Normalize();
        Keys ??= new();
        HoldMs = Math.Max(0, HoldMs);
        GapMs = Math.Max(0, GapMs);
    }
}

public partial class WaitAction : ActionBase
{
    [ObservableProperty] private int _ms = 1000;

    public override string TypeName => "Wait";
    public override string Summary => $"Wait {Ms} ms";

    public override void Normalize()
    {
        base.Normalize();
        Ms = Math.Max(0, Ms);
    }
}

public partial class ScrollAction : ActionBase
{
    [ObservableProperty] private ScrollDirection _direction = ScrollDirection.Down;
    /// <summary>Wheel notches.</summary>
    [ObservableProperty] private int _amount = 3;
    [ObservableProperty] private bool _useCursor = true;
    [ObservableProperty] private int _x;
    [ObservableProperty] private int _y;

    public override string TypeName => "Scroll";
    public override string Summary => $"Scroll {Direction.ToString().ToLowerInvariant()} {Amount} {At(UseCursor, X, Y)}";
    public override IEnumerable<(int X, int Y)> GetPoints() => UseCursor ? [] : [(X, Y)];
    public override void Offset(int dx, int dy) { X += dx; Y += dy; }

    public override void Normalize()
    {
        base.Normalize();
        Amount = Math.Clamp(Amount, 1, 1000);
    }
}

public partial class DragAction : ActionBase
{
    [ObservableProperty] private int _x1;
    [ObservableProperty] private int _y1;
    [ObservableProperty] private int _x2;
    [ObservableProperty] private int _y2;
    [ObservableProperty] private ClickButton _button;
    [ObservableProperty] private int _durationMs = 300;

    public override string TypeName => "Drag";
    public override string Summary => $"Drag ({X1}, {Y1}) to ({X2}, {Y2}) over {DurationMs} ms";
    public override IEnumerable<(int X, int Y)> GetPoints() => [(X1, Y1), (X2, Y2)];
    public override void Offset(int dx, int dy) { X1 += dx; Y1 += dy; X2 += dx; Y2 += dy; }

    public override void Normalize()
    {
        base.Normalize();
        DurationMs = Math.Max(0, DurationMs);
    }
}

/// <summary>Moves the cursor without clicking. Produced by the recorder when mouse movement is recorded.</summary>
public partial class MoveAction : ActionBase
{
    [ObservableProperty] private int _x;
    [ObservableProperty] private int _y;
    /// <summary>Glide time. 0 = jump.</summary>
    [ObservableProperty] private int _durationMs;

    public override string TypeName => "Move";
    public override string Summary => $"Move to ({X}, {Y})" + (DurationMs > 0 ? $" over {DurationMs} ms" : "");
    public override IEnumerable<(int X, int Y)> GetPoints() => [(X, Y)];
    public override void Offset(int dx, int dy) { X += dx; Y += dy; }

    public override void Normalize()
    {
        base.Normalize();
        DurationMs = Math.Max(0, DurationMs);
    }
}

/// <summary>Shared settings for the pixel-trigger actions.</summary>
public abstract partial class PixelActionBase : ActionBase
{
    [ObservableProperty] private int _x;
    [ObservableProperty] private int _y;
    /// <summary>Target colour as "#RRGGBB".</summary>
    [ObservableProperty] private string _color = "#FFFFFF";
    /// <summary>Allowed difference per colour channel (0–255).</summary>
    [ObservableProperty] private int _tolerance = 10;
    /// <summary>Give up after this long. 0 = wait forever.</summary>
    [ObservableProperty] private int _timeoutMs = 10000;
    [ObservableProperty] private PixelTimeoutBehavior _onTimeout = PixelTimeoutBehavior.Skip;
    /// <summary>How often the pixel is sampled.</summary>
    [ObservableProperty] private int _pollMs = 50;

    public override IEnumerable<(int X, int Y)> GetPoints() => [(X, Y)];
    public override void Offset(int dx, int dy) { X += dx; Y += dy; }

    protected string Condition => $"({X}, {Y}) is {Color} ±{Tolerance}";

    public override void Normalize()
    {
        base.Normalize();
        Color ??= "#FFFFFF";
        Tolerance = Math.Clamp(Tolerance, 0, 255);
        TimeoutMs = Math.Max(0, TimeoutMs);
        PollMs = Math.Clamp(PollMs, 5, 60000);
    }
}

/// <summary>Blocks until a screen pixel matches a colour, then carries on.</summary>
public partial class PixelWaitAction : PixelActionBase
{
    public override string TypeName => "Pixel wait";
    public override string Summary => $"Wait until {Condition}";
}

/// <summary>Blocks until a screen pixel matches a colour, then clicks.</summary>
public partial class PixelClickAction : PixelActionBase
{
    [ObservableProperty] private ClickButton _button;
    [ObservableProperty] private ClickKind _kind;
    [ObservableProperty] private int _holdMs;
    /// <summary>Click the watched pixel itself. When false, click <see cref="ClickX"/>/<see cref="ClickY"/>.</summary>
    [ObservableProperty] private bool _clickAtPixel = true;
    [ObservableProperty] private int _clickX;
    [ObservableProperty] private int _clickY;

    public override string TypeName => "Pixel click";

    public override string Summary =>
        $"When {Condition}, {Button.ToString().ToLowerInvariant()} click" + (ClickAtPixel ? " it" : $" ({ClickX}, {ClickY})");

    public override IEnumerable<(int X, int Y)> GetPoints() => ClickAtPixel ? [(X, Y)] : [(X, Y), (ClickX, ClickY)];

    public override void Offset(int dx, int dy)
    {
        base.Offset(dx, dy);
        ClickX += dx;
        ClickY += dy;
    }

    public override void Normalize()
    {
        base.Normalize();
        HoldMs = Math.Max(0, HoldMs);
    }
}

/// <summary>
/// One thing an area watch looks for, and the button to click it with. A rule is either a
/// PICTURE of the target (recognised by its shape; used when <see cref="Image"/> is set) or a COLOUR.
/// </summary>
public partial class AreaRule : ObservableObject
{
    /// <summary>A snipped picture of the target as base64 PNG. Empty = this is a colour rule.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImage))]
    private string _image = "";
    /// <summary>How closely something must resemble the picture, in percent (100 = identical).</summary>
    [ObservableProperty] private int _matchPercent = 55;

    /// <summary>"#RRGGBB".</summary>
    [ObservableProperty] private string _color = "#FF0000";
    /// <summary>Allowed difference per colour channel (0-255).</summary>
    [ObservableProperty] private int _tolerance = 25;

    [ObservableProperty] private ClickButton _button = ClickButton.Left;

    public bool IsImage => !string.IsNullOrEmpty(Image);
}

/// <summary>
/// Watches a rectangle of the screen and clicks things that appear in it, choosing the mouse
/// button by what each thing looks like (for example: left-click meteorites, right-click satellites).
/// By default each run of the action waits for one target and clicks it, and looping the action
/// list keeps clearing targets as they arrive. With <see cref="WatchForMs"/> set it instead stays
/// put and keeps clicking targets for that long, which suits "do some set-up clicks once, then
/// play for 30 seconds".
/// </summary>
public partial class AreaWatchAction : ActionBase
{
    [ObservableProperty] private int _x;
    [ObservableProperty] private int _y;
    [ObservableProperty] private int _width = 400;
    [ObservableProperty] private int _height = 300;

    /// <summary>
    /// Pictures and colours to look for. Where two pictures both resemble a spot, the closer
    /// likeness wins; where two colours both match a pixel, the earlier rule wins.
    /// </summary>
    [ObservableProperty] private ObservableCollection<AreaRule> _rules = new();

    /// <summary>
    /// Stay on this action, clicking target after target, for this long before moving on.
    /// 0 = click one target, then carry on with the next action.
    /// </summary>
    [ObservableProperty] private int _watchForMs;

    /// <summary>
    /// Optional start/stop marker: a snipped picture (base64 PNG) of a fixed piece of the screen
    /// that is showing whenever the game is. When set, the watch waits for it to appear, keeps
    /// going while it is there, and ends shortly after it disappears. Empty = no marker.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnchor), nameof(AnchorText))]
    private string _anchorImage = "";
    /// <summary>Where the marker sits (same coordinate space as the area).</summary>
    [ObservableProperty] private int _anchorX;
    [ObservableProperty] private int _anchorY;
    [ObservableProperty] private int _anchorWidth;
    [ObservableProperty] private int _anchorHeight;
    /// <summary>Share of the marker's pixels that must still look the same for it to count as showing.</summary>
    [ObservableProperty] private int _anchorMatchPercent = 80;

    public bool HasAnchor => !string.IsNullOrEmpty(AnchorImage);
    public string AnchorText => HasAnchor
        ? "this is showing: starts when it appears, ends when it goes"
        : "(no marker: it starts straight away)";

    /// <summary>
    /// After clicking a target, leave that spot alone for this long. Stops a second click landing
    /// on a target that is already hit and still fading out (a wasted click, which many games
    /// count as a miss). A target that was missed and keeps moving leaves the spot and is
    /// clicked again. 0 = no waiting.
    /// </summary>
    [ObservableProperty] private int _retargetDelayMs = 400;

    /// <summary>How pictures are compared with what is on screen.</summary>
    [ObservableProperty] private PictureMatch _pictureMatch = PictureMatch.Appearance;

    /// <summary>When several targets are visible, click the one nearest this edge (or the centre) first.</summary>
    [ObservableProperty] private ScanPriority _priority = ScanPriority.Bottom;
    /// <summary>A target must have at least this many matching pixels, so stray specks are ignored.</summary>
    [ObservableProperty] private int _minPixels = 12;
    [ObservableProperty] private int _holdMs;
    /// <summary>How often the area is checked while nothing is there.</summary>
    [ObservableProperty] private int _pollMs = 25;
    /// <summary>Give up waiting for a target after this long. 0 = wait forever.</summary>
    [ObservableProperty] private int _timeoutMs;
    [ObservableProperty] private PixelTimeoutBehavior _onTimeout = PixelTimeoutBehavior.Skip;

    public override string TypeName => "Area watch";

    public override string Summary =>
        $"Watch {Width}×{Height} at ({X}, {Y}){(HasAnchor ? " while its marker shows" : "")}{(WatchForMs > 0 ? $" for {(HasAnchor ? "up to " : "")}{WatchForMs / 1000.0:0.#} s" : "")}: "
        + (Rules.Count == 0
            ? "nothing to look for yet"
            : string.Join(", ", Rules.Select(r => $"{(r.IsImage ? "picture" : r.Color)} {r.Button.ToString().ToLowerInvariant()}")));

    public override IEnumerable<(int X, int Y)> GetPoints() => [(X, Y), (X + Width - 1, Y + Height - 1)];

    public override void Offset(int dx, int dy) { X += dx; Y += dy; AnchorX += dx; AnchorY += dy; }

    public override void Normalize()
    {
        base.Normalize();
        Rules ??= new();
        AnchorImage ??= "";
        AnchorMatchPercent = Math.Clamp(AnchorMatchPercent, 30, 100);
        Width = Math.Clamp(Width, 1, 8192);
        Height = Math.Clamp(Height, 1, 8192);
        MinPixels = Math.Clamp(MinPixels, 1, 100_000);
        WatchForMs = Math.Max(0, WatchForMs);
        RetargetDelayMs = Math.Clamp(RetargetDelayMs, 0, 60_000);
        HoldMs = Math.Max(0, HoldMs);
        PollMs = Math.Clamp(PollMs, 5, 60_000);
        TimeoutMs = Math.Max(0, TimeoutMs);
        foreach (var rule in Rules)
        {
            rule.Color ??= "#FF0000";
            rule.Image ??= "";
            rule.Tolerance = Math.Clamp(rule.Tolerance, 0, 255);
            rule.MatchPercent = Math.Clamp(rule.MatchPercent, 30, 100);
        }
    }
}
