using PizzaHeroClicker.Models;

namespace PizzaHeroClicker.Services;

/// <summary>Something on its way out to the bar's circle.</summary>
/// <param name="Target">Index into <see cref="BarBlockAction.Targets"/>: what kind of thing it is (0 = most valuable).</param>
/// <param name="Angle">Where it is, in degrees round the centre (0 = right, 90 = down, as on screen).</param>
/// <param name="Distance">How far out it is, in pixels.</param>
/// <param name="SecondsToEscape">Roughly how long until it reaches the bar's circle; infinity if it is not known to be moving out.</param>
public sealed record BarThreat(int Target, double Angle, double Distance, int X, int Y, double SecondsToEscape);

/// <summary>
/// Works out, from one look at the square round a <see cref="BarBlockAction"/>'s circle, where the
/// bar is and what is flying out towards it. Pure computation: no I/O, no input.
/// </summary>
public sealed class BarBlockDetector
{
    private readonly BarBlockAction _action;
    private readonly int _size, _centre;
    private readonly PixelColor _barColour;
    private readonly (int Offset, int Bin)[] _barBand;   // pixels on the bar's circle, with their 1-degree bin
    private readonly int[] _bins = new int[360];
    private readonly BlobFinder _blobs = new();
    private readonly SceneModel _scene = new();
    private readonly TargetTracker _tracker = new();
    private readonly List<(int X, int Y)> _seen = new();

    /// <summary>How far either side of the circle the bar's pixels are looked for.</summary>
    private const int BarBand = 22;
    /// <summary>Fewer matching pixels than this on the circle and the bar is not considered found.</summary>
    public const int MinBarPixels = 30;

    public BarBlockDetector(BarBlockAction action)
    {
        _action = action;
        var box = action.LookBox;
        _size = box.Size;
        _centre = action.Radius + BarBlockAction.LookMargin;
        _barColour = PixelColor.TryParse(action.BarColor, out var c) ? c : new PixelColor(240, 50, 50);

        // Every pixel of the band round the bar's circle, worked out once.
        var band = new List<(int, int)>();
        int r = action.Radius, inner = Math.Max(0, r - BarBand), outer = r + BarBand;
        for (int y = -outer; y <= outer; y++)
        for (int x = -outer; x <= outer; x++)
        {
            int d2 = x * x + y * y;
            if (d2 < inner * inner || d2 > outer * outer) continue;
            int px = _centre + x, py = _centre + y;
            if (px < 0 || py < 0 || px >= _size || py >= _size) continue;
            int bin = ((int)Math.Floor(Math.Atan2(y, x) * 180 / Math.PI) + 360) % 360;
            band.Add((py * _size + px, bin));
        }
        _barBand = band.ToArray();

        _blobs.Markers = action.Targets
            .Select(t => new AreaColorRule(PixelColor.TryParse(t.Color, out var tc) ? tc : new PixelColor(255, 255, 255), t.Tolerance))
            .ToList();
    }

    /// <summary>The bar's angle at the last look (degrees, 0 = right, 90 = down), or null if it was not found.</summary>
    public double? BarAngle { get; private set; }
    /// <summary>How many of the bar's pixels were seen at the last look.</summary>
    public int BarPixels { get; private set; }
    public IReadOnlyList<BarThreat> Threats => _threats;
    private readonly List<BarThreat> _threats = new();
    /// <summary>True until the backdrop has been learned (about two seconds): until then nothing is found.</summary>
    public bool Learning => !_scene.Ready;

    /// <summary>Looks at one capture of <see cref="BarBlockAction.LookBox"/> (Size x Size pixels) taken at <paramref name="timeMs"/>.</summary>
    public void Look(int[] pixels, double timeMs)
    {
        _threats.Clear();
        if (pixels.Length < _size * _size) return;
        FindBar(pixels);
        FindThreats(pixels, timeMs);
    }

    private void FindBar(int[] pixels)
    {
        Array.Clear(_raw);
        int tol = _action.BarTolerance;
        foreach (var (offset, bin) in _barBand)
        {
            int p = pixels[offset];
            if (Math.Abs(((p >> 16) & 0xFF) - _barColour.R) <= tol
                && Math.Abs(((p >> 8) & 0xFF) - _barColour.G) <= tol
                && Math.Abs((p & 0xFF) - _barColour.B) <= tol) _raw[bin]++;
        }
        Patches = CountPatches(_raw);
        (BarAngle, BarPixels) = Locate(_raw, BarAngle);
    }

    /// <summary>
    /// Pixel counts of the bar's colour round its circle, one per degree, as last seen (before any
    /// backdrop is taken off). Used to learn the backdrop: see <see cref="LearnBackdrop"/>.
    /// </summary>
    public int[] RawBins => (int[])_raw.Clone();

    /// <summary>
    /// How many separate patches of the bar's colour lie on its circle (before the backdrop is
    /// taken off). More than one means the backdrop has that colour too somewhere on the circle.
    /// </summary>
    public int Patches { get; private set; }

    /// <summary>True once the backdrop's patches of the bar's colour are known and taken off.</summary>
    public bool KnowsBackdrop => _background is not null;

    /// <summary>
    /// The backdrop may carry the bar's colour on parts of the circle (red clouds behind a red
    /// bar). Given the counts with the bar in two places far enough apart, whatever was there both
    /// times is backdrop, and is taken off every look from now on, leaving only the bar.
    /// </summary>
    public void LearnBackdrop(int[] before, int[] after)
    {
        _background = new int[360];
        for (int i = 0; i < 360; i++) _background[i] = Math.Min(before[i], after[i]);
        BarAngle = null; // found afresh, now without the backdrop
    }

    /// <summary>Where the bar is in a set of counts, with the backdrop taken off: the run nearest <paramref name="near"/>, otherwise the biggest.</summary>
    public (double? Angle, int Pixels) Locate(int[] raw, double? near)
    {
        for (int i = 0; i < 360; i++)
            _bins[i] = _background is null ? raw[i] : Math.Max(0, raw[i] - _background[i] - BackdropSlack);
        (double Angle, int Count)? best = null;
        double bestScore = double.MaxValue;
        foreach (var run in Runs())
        {
            if (run.Count < MinBarPixels) continue;
            // Prefer the run nearest where it was last time (it only moves a few degrees between looks), otherwise the biggest.
            double score = near is double p ? Math.Abs(Wrap(run.Angle - p)) - run.Count * 1e-6 : -run.Count;
            if (best is null || score < bestScore) (best, bestScore) = (run, score);
        }
        return (best?.Angle, best?.Count ?? 0);
    }

    private int CountPatches(int[] raw)
    {
        Array.Copy(raw, _bins, 360);
        return Runs().Count(r => r.Count >= MinBarPixels);
    }

    /// <summary>Changes this small in a degree's count are noise, not the bar.</summary>
    public const int BackdropSlack = 2;
    private readonly int[] _raw = new int[360];
    private int[]? _background;

    /// <summary>Runs of non-empty 1-degree bins (allowing one-bin gaps, and wrapping past 360), with their mean angle.</summary>
    private IEnumerable<(double Angle, int Count)> Runs()
    {
        int start = Array.FindIndex(_bins, b => b == 0);
        if (start < 0) { yield return (0, _bins.Sum()); yield break; } // all the way round: not a bar
        int count = 0;
        double sx = 0, sy = 0;
        int gap = 0;
        for (int k = 1; k <= 361; k++)
        {
            int bin = (start + k) % 360;
            int n = k <= 360 ? _bins[bin] : 0;
            if (n > 0)
            {
                count += n;
                double a = (bin + 0.5) * Math.PI / 180;
                sx += n * Math.Cos(a);
                sy += n * Math.Sin(a);
                gap = 0;
                continue;
            }
            if (++gap <= 1 && k <= 360) continue;
            if (count > 0) yield return (Normalise(Math.Atan2(sy, sx) * 180 / Math.PI), count);
            (count, sx, sy) = (0, 0, 0);
        }
    }

    private void FindThreats(int[] pixels, double timeMs)
    {
        _threats.Clear();
        _seen.Clear();
        long ms = (long)timeMs;
        if (!_scene.Ready)
        {
            _scene.Update(pixels, _size, _size, ms);
            return;
        }
        _blobs.MarkAgainstScene(pixels, _size, _size, _scene);
        _scene.Update(pixels, _size, _size, ms);
        var blobs = _blobs.Label(pixels, _size, _size, 60, _size * _size / 20);

        double r = _action.Radius;
        var current = new List<BarThreat>();
        foreach (var blob in blobs)
        {
            if (blob.Marker < 0) continue; // moving, but not the colour of anything to block
            double dx = blob.X - _centre, dy = blob.Y - _centre;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            // Not out yet; or on the bar's circle, where the moving thing is the bar itself (which may
            // carry highlights of a target's colour), or something already reaching it.
            if (distance < _action.InnerRadius || distance > r - BarBand / 2) continue;
            double angle = Normalise(Math.Atan2(dy, dx) * 180 / Math.PI);
            _seen.Add((blob.X, blob.Y));
            current.Add(new BarThreat(blob.Marker, angle, distance, blob.X, blob.Y, double.PositiveInfinity));
        }

        // How fast is each one heading out? From where it has been over the last few looks.
        _tracker.Update(_seen, timeMs);
        foreach (var t in current)
        {
            var (vx, vy) = _tracker.VelocityOf(t.X, t.Y);
            double outward = (vx * (t.X - _centre) + vy * (t.Y - _centre)) / Math.Max(1, t.Distance);
            if (outward > 5) _typicalSpeed += (outward - _typicalSpeed) * 0.2; // most fly at much the same speed
            double speed = outward > 5 ? outward : _typicalSpeed;
            Remember(t, timeMs + (r - t.Distance) / speed * 1000, timeMs);
        }

        // Things fly straight out, so where one will cross the circle is known from the moment it is
        // seen. The last stretch is the hardest to see (it meets the bar there), so each is kept in
        // mind until it is due to cross, even after it has been lost from view.
        _pending.RemoveAll(p => timeMs > p.CrossesAtMs + 40 || timeMs - p.LastSeenMs > 700);
        foreach (var p in _pending)
            _threats.Add(p.Threat with { SecondsToEscape = Math.Max(0, (p.CrossesAtMs - timeMs) / 1000) });
    }

    private sealed class Pending
    {
        public BarThreat Threat = null!;
        public double CrossesAtMs, LastSeenMs;
    }
    private readonly List<Pending> _pending = new();
    private double _typicalSpeed = 300;

    private void Remember(BarThreat t, double crossesAtMs, double nowMs)
    {
        foreach (var p in _pending)
        {
            // The same one as before: same kind, same direction, further out.
            if (p.Threat.Target != t.Target || Math.Abs(Wrap(p.Threat.Angle - t.Angle)) > 6 || t.Distance < p.Threat.Distance - 10) continue;
            (p.Threat, p.CrossesAtMs, p.LastSeenMs) = (t, crossesAtMs, nowMs);
            return;
        }
        _pending.Add(new Pending { Threat = t, CrossesAtMs = crossesAtMs, LastSeenMs = nowMs });
    }

    /// <summary>
    /// Which threat to steer to: of those the bar can get to before they escape (turning at
    /// <paramref name="degreesPerSecond"/>), the most valuable, nearest escape first; if it can
    /// reach none in time, the one that will escape soonest.
    /// </summary>
    public BarThreat? Choose(double degreesPerSecond)
    {
        if (_threats.Count == 0) return null;
        double bar = BarAngle ?? 0;
        double Escape(BarThreat t) => t.SecondsToEscape;
        bool Reachable(BarThreat t) => BarAngle is null || Math.Abs(Wrap(t.Angle - bar)) / Math.Max(1, degreesPerSecond) <= Escape(t);

        var reachable = _threats.Where(Reachable).ToList();
        if (reachable.Count > 0)
            return reachable.OrderBy(t => t.Target).ThenBy(Escape).First();
        return _threats.OrderBy(Escape).First();
    }

    /// <summary>An angle difference brought into -180..180 degrees.</summary>
    public static double Wrap(double degrees)
    {
        degrees %= 360;
        if (degrees > 180) degrees -= 360;
        if (degrees <= -180) degrees += 360;
        return degrees;
    }

    private static double Normalise(double degrees) => (degrees % 360 + 360) % 360;
}
