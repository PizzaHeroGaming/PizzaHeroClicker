using System.Text;
using PizzaHeroClicker.Models;

namespace PizzaHeroClicker.Services;

/// <summary>
/// Compares a fixed piece of the screen with a stored picture of it, to tell whether a
/// particular screen (a mini game, a menu) is currently showing.
/// </summary>
public static class ScreenAnchor
{
    public const int MinSide = 8;
    public const int MaxSide = 600;
    private const int Tolerance = 40;

    /// <summary>
    /// Share (0..1) of pixels that are about the same in both. Counting matching pixels, rather
    /// than averaging differences, means something drifting across part of the marker only
    /// lowers the score in proportion to what it covers.
    /// </summary>
    public static double Similarity(int[] live, int[] reference)
    {
        int n = Math.Min(live.Length, reference.Length);
        if (n == 0) return 0;
        int same = 0;
        for (int i = 0; i < n; i++)
        {
            int a = live[i], b = reference[i];
            if (Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF)) <= Tolerance
                && Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF)) <= Tolerance
                && Math.Abs((a & 0xFF) - (b & 0xFF)) <= Tolerance) same++;
        }
        return (double)same / n;
    }
}

/// <summary>What one look at the area found. Produced for the editor's Test button.</summary>
public sealed class AreaReport
{
    public string Background = "";
    public int ObjectsFound;
    /// <summary>Per picture rule: rule index, best likeness seen (0..1, or NaN if not measured), and whether it matched.</summary>
    public List<(int Rule, double Best, bool Matched)> Pictures { get; } = new();
    public List<(int Rule, bool Matched)> Colours { get; } = new();
    public (int X, int Y, int Rule)? Target;

    /// <summary>Every object considered: where, how big, which picture it most resembles and how closely.</summary>
    /// <remarks>BestScore is NaN when the object was identified by a marker colour rather than a picture.</remarks>
    public List<(int X, int Y, int Pixels, int BestRule, double BestScore)> Objects { get; } = new();
    /// <summary>True when the backdrop is a busy scene and objects are found by their movement.</summary>
    public bool BusyScene;
    /// <summary>True while a busy scene is still being learned (nothing can be recognised yet).</summary>
    public bool Learning;
    /// <summary>True when so much of the area differs from the learned scene that the screen itself must have changed.</summary>
    public bool ScreenChanged;
    public int TooSmall, TooLarge, LargestPixels, MinPixels, MaxPixels;
}

/// <summary>
/// Everything an area watch needs to turn a captured frame into "click here with this button".
/// Built once per run (pictures decoded, colours parsed) and then asked about each frame.
/// Used by the engine and by the editor's Test button, so both always agree.
/// Not thread-safe: one instance per thread.
/// </summary>
public sealed class AreaDetector
{
    private const int ColourScanStep = 3;
    private const int MaxSizeFactor = 12; // objects more than this many times a picture's area are ignored
    private const double MinSizeShare = 0.25; // ...and so are objects well under the size of the smallest target (stars, sparks, digits)
    /// <summary>
    /// If less than this share of the area is the most common colour, the backdrop is treated as
    /// a busy scene and objects are found by movement instead of by standing out from one colour.
    /// </summary>
    private const double PlainBackdropShare = 0.60;
    /// <summary>
    /// Targets cover a few percent of the area. If more than this share of it differs from the
    /// learned scene, the screen has changed (the game ended, a menu opened) and nothing in it is
    /// treated as a target until the new screen has been learned.
    /// </summary>
    private const double ScreenChangedShare = 0.25;

    private readonly AreaWatchAction _action;
    private readonly AreaColorRule[] _colours;
    private readonly int[] _colourRule;                 // index into _colours -> index into Rules
    private readonly List<(PreparedTemplate Template, int Rule, double Threshold)> _exact = new();
    private readonly List<(int[] Pixels, int Width, int Height, int Rule, double Threshold)> _appearance = new();
    private readonly AreaScanner _scanner = new();
    private readonly TemplateMatcher _matcher = new();
    private readonly BlobFinder _blobFinder = new();
    private readonly SceneModel _scene = new();
    private const int SceneKey = -2; // stands in for "no single backdrop colour" in the signature cache

    /// <summary>True when a frame's backdrop is a busy scene rather than mostly one colour.</summary>
    public static bool IsBusyScene(int[] pixels, int width, int height)
    {
        var finder = new BlobFinder();
        return finder.MarkAgainstColour(pixels, width, height, finder.DominantColour(pixels, width, height)) < PlainBackdropShare;
    }

    /// <summary>True while a busy scene is still being learned; the caller should keep feeding frames.</summary>
    public bool IsLearning { get; private set; }

    /// <summary>True when the last frame showed a different screen from the one learned, so nothing was offered.</summary>
    public bool IsScreenChanged { get; private set; }

    private bool _hasClicked;
    private bool _learnedBusy;          // what kind of backdrop the learned screen had...
    private int _learnedBackground;     // ...and its most common colour

    /// <summary>
    /// Tell the detector a target it reported was clicked. From then on a sudden change of the
    /// whole screen is taken to mean the game has ended, rather than that it is starting.
    /// </summary>
    public void NotifyClicked() => _hasClicked = true;
    private readonly List<(TemplateMatch Match, int Rule, int Width, int Height)> _exactMatches = new();
    private TargetSignature[] _signatures = [];
    private int _signatureBackground = -1;
    private int _minObjectPixels;
    private readonly int _maxObjectPixels;

    /// <summary>When true, <see cref="LastReport"/> is filled in by every <see cref="Find"/>.</summary>
    public bool CollectReport { get; set; }
    public AreaReport? LastReport { get; private set; }

    /// <summary>Throws InvalidOperationException with a user-readable message if a rule can't be used.</summary>
    public AreaDetector(AreaWatchAction action)
    {
        _action = action;
        if (action.Rules.Count == 0) throw new InvalidOperationException("The area watch has nothing to look for.");

        var colours = new List<AreaColorRule>();
        var colourRule = new List<int>();
        int largestPicture = 0;
        for (int i = 0; i < action.Rules.Count; i++)
        {
            var rule = action.Rules[i];
            if (!rule.IsImage)
            {
                if (!PixelColor.TryParse(rule.Color, out var color))
                    throw new InvalidOperationException($"'{rule.Color}' is not a valid colour (expected #RRGGBB).");
                colours.Add(new AreaColorRule(color, rule.Tolerance));
                colourRule.Add(i);
                continue;
            }

            if (!TemplateImage.TryDecode(rule.Image, out var pixels, out int w, out int h))
                throw new InvalidOperationException($"Picture {i + 1} of the area watch can't be read. Capture it again.");
            double threshold = rule.MatchPercent / 100.0;
            if (action.PictureMatch == PictureMatch.Exact)
            {
                if (PreparedTemplate.Create(pixels, w, h) is not { } template)
                    throw new InvalidOperationException($"Picture {i + 1} has no detail to match exactly. Capture it again.");
                _exact.Add((template, i, threshold));
            }
            else
            {
                _appearance.Add((pixels, w, h, i, threshold));
                largestPicture = Math.Max(largestPicture, w * h);
            }
        }
        _colours = colours.ToArray();
        _colourRule = colourRule.ToArray();
        // "The same target" = within about three quarters of a picture's size.
        if (largestPicture > 0) AvoidRadius = Math.Max(40, (int)(Math.Sqrt(largestPicture) * 0.75));
        _maxObjectPixels = Math.Max(1, largestPicture) * MaxSizeFactor;
    }

    /// <summary>
    /// The most urgent target in a frame of the action's area (row-major 0x00RRGGBB pixels,
    /// Width x Height). X/Y are in pixels from the area's top-left.
    /// </summary>
    /// <summary>How close to a just-clicked spot a target must be to count as "the same one".</summary>
    public int AvoidRadius { get; private set; } = 40;

    /// <param name="nowMs">A millisecond clock for scene learning; leave out to use the system's.</param>
    /// <param name="avoid">Spots (area pixels) clicked a moment ago; targets within <see cref="AvoidRadius"/> of one are passed over.</param>
    public (int X, int Y, int Rule)? Find(int[] pixels, long nowMs = -1, IReadOnlyList<(int X, int Y)>? avoid = null)
    {
        if (nowMs < 0) nowMs = Environment.TickCount64;
        int width = _action.Width, height = _action.Height;
        var priority = _action.Priority;
        var report = CollectReport ? new AreaReport() : null;
        LastReport = report;

        (int Key, int X, int Y, int Rule)? best = null;
        void Offer(int x, int y, int rule)
        {
            if (avoid is not null)
            {
                foreach (var (ax, ay) in avoid)
                {
                    if (Math.Abs(x - ax) <= AvoidRadius && Math.Abs(y - ay) <= AvoidRadius) return; // just clicked there
                }
            }
            int key = AreaScanner.PriorityKey(priority, x, y, width, height);
            if (best is null || key < best.Value.Key) best = (key, x, y, rule);
        }

        // ---- colours
        // With pictures in play, colours are not searched for on their own: they act as MARKERS
        // inside the objects found below (see BlobFinder.Markers).
        bool coloursAreMarkers = _appearance.Count > 0;
        if (_colours.Length > 0 && !coloursAreMarkers)
        {
            var blob = _scanner.Find(pixels, width, height, _colours, ColourScanStep, _action.MinPixels, priority, avoid, AvoidRadius);
            if (blob is { } found) Offer(found.X, found.Y, _colourRule[found.RuleIndex]);
            if (report is not null)
            {
                foreach (int rule in _colourRule) report.Colours.Add((rule, blob is { } b && _colourRule[b.RuleIndex] == rule));
            }
        }

        // ---- pictures matched by appearance: find the objects, then ask which picture each resembles most
        if (_appearance.Count > 0)
        {
            // A plain backdrop (most of the area is one colour): objects are whatever differs from it.
            // A busy scene: learn what it looks like and treat whatever moves across it as an object.
            int background = _blobFinder.DominantColour(pixels, width, height);
            bool busy = _blobFinder.MarkAgainstColour(pixels, width, height, background) < PlainBackdropShare;

            // Has the screen itself changed? Targets cover a few percent of the area; if a quarter
            // of it suddenly differs from what was learned, something else is being shown. This
            // is checked whatever the backdrop, because the dangerous case is exactly a busy
            // game giving way to a plain screen decorated with pictures of the same targets.
            bool screenChanged = _scene.Ready && _blobFinder.MarkAgainstScene(pixels, width, height, _scene) > ScreenChangedShare;
            // One exception: a plain backdrop that is still the same plain colour is the same screen,
            // however much of it changed. (A small area around one large target loses a third of
            // its content every time that target is clicked away; that is not a new screen.)
            if (screenChanged && !busy && !_learnedBusy && BlobFinder.IsBackground(background, _learnedBackground)) screenChanged = false;
            if (screenChanged)
            {
                // Before anything has been clicked, a new screen is the game OPENING: learn it at once.
                // After clicking has begun it is the game CLOSING. The new screen is then never
                // accepted, however long it stays: it may be a shop where a click spends something.
                // Clicking resumes only if the screen that was learned comes back (the next game).
                if (!_hasClicked) _scene.Reset();
            }
            else
            {
                _scene.Update(pixels, width, height, nowMs); // kept current whatever the backdrop
                (_learnedBusy, _learnedBackground) = (busy, background);
            }
            IsScreenChanged = screenChanged;

            IsLearning = busy && !_scene.Ready && !screenChanged;
            if (screenChanged) _blobFinder.ClearMarks();                                        // nothing is a target right now
            else if (busy) _blobFinder.MarkAgainstScene(pixels, width, height, _scene);         // whatever moves
            else _blobFinder.MarkAgainstColour(pixels, width, height, background);              // whatever stands out
            EnsureSignatures(busy ? SceneKey : background);
            _blobFinder.Markers = _colours;
            var blobs = _blobFinder.Label(pixels, width, height, _minObjectPixels, _maxObjectPixels);
            var markerSeen = report is null ? null : new bool[_colours.Length];
            if (report is not null) (report.BusyScene, report.Learning, report.ScreenChanged) = (busy, IsLearning, screenChanged);
            var bestSeen = report is null ? null : new double[_appearance.Count];

            int considered = 0;
            foreach (var blob in blobs)
            {
                // A marker such as a ring can sit clear of the thing it surrounds, leaving that thing
                // as a separate object in the middle. It has already been dealt with via the marker,
                // so it must not also be judged by the pictures (and maybe get the other button).
                if (blob.Marker < 0 && IsInsideMarkedObject(blob, blobs)) continue;
                // A moving target still crossing the edge of the area is only partly visible: its
                // centre is not where it seems and it cannot be judged properly. Wait until it is in.
                if (busy && TouchesEdge(blob, width, height)) continue;
                considered++;

                if (blob.Marker >= 0)
                {
                    // Carries a marker colour: that settles what it is, whatever it looks like otherwise.
                    Offer(blob.X, blob.Y, _colourRule[blob.Marker]);
                    if (markerSeen is not null) markerSeen[blob.Marker] = true;
                    report?.Objects.Add((blob.X, blob.Y, blob.Pixels, _colourRule[blob.Marker], double.NaN));
                    continue;
                }

                int winner = -1;
                double winnerScore = 0;
                for (int i = 0; i < _appearance.Count; i++)
                {
                    double score = BlobFinder.Similarity(blob.Histogram, _signatures[i].Histogram);
                    if (bestSeen is not null && score > bestSeen[i]) bestSeen[i] = score;
                    if (score > winnerScore) (winner, winnerScore) = (i, score);
                }
                // The object is whichever picture it resembles MOST, and only if that is close enough.
                if (winner >= 0 && winnerScore >= _appearance[winner].Threshold) Offer(blob.X, blob.Y, _appearance[winner].Rule);
                report?.Objects.Add((blob.X, blob.Y, blob.Pixels, winner >= 0 ? _appearance[winner].Rule : -1, winnerScore));
            }

            if (report is not null)
            {
                report.Background = $"#{background & 0xFFFFFF:X6}";
                report.ObjectsFound = considered;
                (report.TooSmall, report.TooLarge, report.LargestPixels) = (_blobFinder.LastTooSmall, _blobFinder.LastTooLarge, _blobFinder.LastLargestPixels);
                (report.MinPixels, report.MaxPixels) = (_minObjectPixels, _maxObjectPixels);
                for (int i = 0; i < _appearance.Count; i++)
                    report.Pictures.Add((_appearance[i].Rule, bestSeen![i], bestSeen[i] >= _appearance[i].Threshold));
                for (int i = 0; i < _colours.Length; i++) report.Colours.Add((_colourRule[i], markerSeen![i]));
            }
        }

        // ---- pictures matched exactly
        if (_exact.Count > 0)
        {
            _matcher.SetFrame(pixels, width, height);
            _exactMatches.Clear();
            foreach (var (template, rule, threshold) in _exact)
            {
                var match = _matcher.FindFirst(template, threshold, priority);
                if (match is { } m) _exactMatches.Add((m, rule, template.Width, template.Height));
                report?.Pictures.Add((rule, match?.Score ?? double.NaN, match is not null));
            }

            // Two pictures can both resemble the same object. Where their matches overlap, the closer likeness wins.
            for (int i = 0; i < _exactMatches.Count; i++)
            {
                var m = _exactMatches[i];
                bool beaten = false;
                for (int j = 0; j < _exactMatches.Count && !beaten; j++)
                {
                    if (i == j) continue;
                    var other = _exactMatches[j];
                    bool overlap = Math.Abs(m.Match.X - other.Match.X) * 2 < Math.Min(m.Width, other.Width)
                                && Math.Abs(m.Match.Y - other.Match.Y) * 2 < Math.Min(m.Height, other.Height);
                    beaten = overlap && (other.Match.Score > m.Match.Score || (other.Match.Score == m.Match.Score && j < i));
                }
                if (!beaten) Offer(m.Match.X, m.Match.Y, m.Rule);
            }
        }

        (int X, int Y, int Rule)? target = best is var (_, bx, by, brule) ? (bx, by, brule) : null;
        if (report is not null) report.Target = target;
        return target;
    }

    private static bool TouchesEdge(Blob blob, int width, int height)
    {
        const int margin = BlobFinder.Step * 2;
        return blob.Left < margin || blob.Top < margin || blob.Right >= width - margin || blob.Bottom >= height - margin;
    }

    private static bool IsInsideMarkedObject(Blob blob, IReadOnlyList<Blob> all)
    {
        foreach (var other in all)
        {
            if (other.Marker >= 0 && blob.X >= other.Left && blob.X <= other.Right && blob.Y >= other.Top && blob.Y <= other.Bottom)
                return true;
        }
        return false;
    }

    /// <summary>
    /// A picture's signature leaves out anything the colour of the game's backdrop, which is
    /// only known from a live frame. Signatures are rebuilt if that backdrop changes.
    /// </summary>
    private void EnsureSignatures(int background)
    {
        if (_signatures.Length == _appearance.Count && background == _signatureBackground) return;

        _signatures = new TargetSignature[_appearance.Count];
        for (int i = 0; i < _appearance.Count; i++)
        {
            var (pixels, w, h, _, _) = _appearance[i];
            int? own = BlobFinder.PictureBackdrop(pixels, w, h);
            // In a busy scene there is no single game backdrop colour to leave out.
            var signature = BlobFinder.Describe(pixels, w, h, own, background == SceneKey ? null : background);
            // Nearly nothing left? Then the target itself is close to the game's backdrop colour;
            // fall back to describing all of it rather than nothing.
            if (signature.Pixels < 16) signature = BlobFinder.Describe(pixels, w, h, own, null);
            if (signature.Pixels < 16) signature = BlobFinder.Describe(pixels, w, h, null, null);
            _signatures[i] = signature;
        }
        _signatureBackground = background;
        // Scale the "too small to be a target" limit to the pictures: a couple of neighbouring stars
        // can otherwise pass for a tiny grey object.
        int smallest = _signatures.Length == 0 ? 0 : _signatures.Min(s => s.Pixels);
        _minObjectPixels = Math.Max(_action.MinPixels, (int)(smallest * MinSizeShare));
    }

    /// <summary>Plain-language summary of <see cref="LastReport"/> for the Test button.</summary>
    public string DescribeLastReport()
    {
        if (LastReport is not { } report) return "";
        var text = new StringBuilder();
        var rules = _action.Rules;
        string Button(int rule) => rules[rule].Button.ToString().ToLowerInvariant();

        if (_appearance.Count > 0)
        {
            text.AppendLine(report.BusyScene
                ? $"Busy backdrop: finding things by their movement.  Moving objects seen: {report.ObjectsFound}."
                : $"Plain backdrop, colour {report.Background}.  Separate objects seen in the area: {report.ObjectsFound}.");
            if (report.Learning) text.AppendLine("Still learning what the scene looks like (takes about two seconds).");
            if (report.ScreenChanged) text.AppendLine("This is a different screen from the one the watch was playing on, so nothing on it is treated as a target.");
            if (report.TooSmall + report.TooLarge > 0)
                text.AppendLine($"Ignored for size: {report.TooSmall} too small (under {report.MinPixels:N0} px), {report.TooLarge} too large (over {report.MaxPixels:N0} px; the largest was {report.LargestPixels:N0} px).");
            foreach (var (objectX, objectY, pixels, bestRule, bestScore) in report.Objects.Take(25))
            {
                string what = double.IsNaN(bestScore)
                    ? $"carries colour {rules[bestRule].Color} (rule {bestRule + 1})"
                    : $"most like picture {bestRule + 1} ({bestScore:P0})";
                text.AppendLine($"  object at ({_action.X + objectX}, {_action.Y + objectY}), {pixels:N0} px: {what}");
            }
            text.AppendLine();
        }
        foreach (var (rule, best, matched) in report.Pictures)
        {
            string likeness = double.IsNaN(best) ? "no match" : $"best likeness {best:P0}";
            text.AppendLine($"{(matched ? "MATCH " : "no    ")} Picture {rule + 1} ({Button(rule)} click): {likeness}, needs {rules[rule].MatchPercent}%");
        }
        foreach (var (rule, matched) in report.Colours)
            text.AppendLine($"{(matched ? "MATCH " : "no    ")} Colour {rules[rule].Color} ({Button(rule)} click)");

        text.AppendLine();
        if (report.Target is var (x, y, target))
        {
            text.AppendLine($"It would {Button(target)}-click rule {target + 1} at ({_action.X + x}, {_action.Y + y}).");
        }
        else
        {
            text.AppendLine("It would not click anything right now.");
            if (_appearance.Count > 0 && report.BusyScene && report.ObjectsFound == 0)
                text.AppendLine("Nothing was moving. Against a busy backdrop, targets are only seen while they move; test while the game is running.");
            else if (_appearance.Count > 0 && report.ObjectsFound == 0)
                text.AppendLine("No objects stood out from the backdrop. Check that the area covers the game and a target was visible. This method needs a plain backdrop.");
            else if (report.Pictures.Count > 0)
                text.AppendLine("If a target was on screen, set its picture's % a little below the likeness shown above.");
        }
        return text.ToString().TrimEnd();
    }
}
