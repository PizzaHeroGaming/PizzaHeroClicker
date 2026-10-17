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

    /// <summary>
    /// How far either side of the circle the bar's pixels are looked for. Generous: the centre is
    /// clicked by hand and can easily be ten or twenty pixels off the true one.
    /// </summary>
    private const int BarBand = 40;
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
        _match = new bool[_barBand.Length];

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
        Array.Clear(_counted);
        int tol = _action.BarTolerance;
        for (int i = 0; i < _barBand.Length; i++)
        {
            var (offset, bin) = _barBand[i];
            int p = pixels[offset];
            bool match = Math.Abs(((p >> 16) & 0xFF) - _barColour.R) <= tol
                && Math.Abs(((p >> 8) & 0xFF) - _barColour.G) <= tol
                && Math.Abs((p & 0xFF) - _barColour.B) <= tol;
            _match[i] = match;
            if (!match) continue;
            _raw[bin]++;
            // A pixel the backdrop itself has in the bar's colour says nothing about the bar.
            if (_static is null || !_static[i]) _counted[bin]++;
        }
        Patches = CountPatches(_raw);
        (BarAngle, BarPixels) = Locate(_counted, BarAngle ?? ExpectedAngle);
    }

    /// <summary>
    /// Where the bar should be, worked out by the caller from the keys it has pressed. Used to pick
    /// the right patch of colour after the bar has been out of sight for a moment.
    /// </summary>
    public double? ExpectedAngle { get; set; }

    /// <summary>For every pixel of the band round the circle, whether it had the bar's colour at the last look.</summary>
    public bool[] MatchSnapshot => (bool[])_match.Clone();

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
    public bool KnowsBackdrop => _static is not null;

    /// <summary>
    /// The backdrop may carry the bar's colour on parts of the circle (red clouds behind a red
    /// bar). Given which pixels had the bar's colour with the bar in two places far enough apart,
    /// the pixels that had it both times are backdrop, and are left out from now on. Pixel by
    /// pixel, so that the bar passing over such a patch is still seen by the rest of its pixels.
    /// </summary>
    public void LearnBackdrop(bool[] before, bool[] after)
    {
        if (before.Length != _match.Length || after.Length != _match.Length) return;
        _static = new bool[_match.Length];
        for (int i = 0; i < _static.Length; i++) _static[i] = before[i] && after[i];
        BarAngle = null; // found afresh, now without the backdrop
    }

    /// <summary>Where the bar is in a set of counts: the run nearest <paramref name="near"/>, otherwise the biggest.</summary>
    private (double? Angle, int Pixels) Locate(int[] counts, double? near)
    {
        Array.Copy(counts, _bins, 360);
        (double Angle, int Count, int Span)? best = null;
        double bestScore = double.MaxValue;
        foreach (var run in Runs())
        {
            if (run.Count < MinBarPixels) continue;
            // Prefer the run nearest where it was last time (it only moves a few degrees between looks), otherwise the biggest.
            double score = near is double p ? Math.Abs(Wrap(run.Angle - p)) - run.Count * 1e-6 : -run.Count;
            if (best is null || score < bestScore) (best, bestScore) = (run, score);
        }
        if (best is { Span: < 90 } found) // a "bar" round a quarter of the circle is not one
            BarWidthDegrees = found.Span >= BarWidthDegrees ? found.Span : BarWidthDegrees + (found.Span - BarWidthDegrees) * 0.02;
        return (best?.Angle, best?.Count ?? 0);
    }

    private int CountPatches(int[] raw)
    {
        Array.Copy(raw, _bins, 360);
        return Runs().Count(r => r.Count >= MinBarPixels);
    }

    /// <summary>
    /// How far inside the bar's ends something must arrive to count as blocked by it, in degrees:
    /// the ends are not trusted to the last pixel.
    /// </summary>
    public const double EdgeMarginDegrees = 3;

    /// <summary>Changes this small in a degree's count are noise, not the bar.</summary>
    public const int BackdropSlack = 2;
    private readonly int[] _raw = new int[360], _counted = new int[360];
    private readonly bool[] _match;
    private bool[]? _static;

    /// <summary>Runs of non-empty 1-degree bins (allowing one-bin gaps, and wrapping past 360), with their mean angle.</summary>
    private IEnumerable<(double Angle, int Count, int Span)> Runs()
    {
        int start = Array.FindIndex(_bins, b => b == 0);
        if (start < 0) { yield return (0, _bins.Sum(), 360); yield break; } // all the way round: not a bar
        int count = 0, first = -1, last = -1;
        double sx = 0, sy = 0;
        int gap = 0;
        for (int k = 1; k <= 361; k++)
        {
            int bin = (start + k) % 360;
            int n = k <= 360 ? _bins[bin] : 0;
            if (n > 0)
            {
                count += n;
                if (first < 0) first = k;
                last = k;
                double a = (bin + 0.5) * Math.PI / 180;
                sx += n * Math.Cos(a);
                sy += n * Math.Sin(a);
                gap = 0;
                continue;
            }
            if (++gap <= 1 && k <= 360) continue;
            if (count > 0) yield return (Normalise(Math.Atan2(sy, sx) * 180 / Math.PI), count, last - first + 1);
            (count, sx, sy, first, last) = (0, 0, 0, -1, -1);
        }
    }

    /// <summary>
    /// How many degrees of the circle the bar covers. Anything arriving within that span is
    /// blocked, so a long bar need not be centred on its target. Learned as the bar is seen: it
    /// grows to the widest seen at once, and shrinks only slowly (the bar can look short for a
    /// moment while part of it is over backdrop of its own colour).
    /// </summary>
    public double BarWidthDegrees { get; private set; }

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
            Remember(t, timeMs + (r - t.Distance) / speed * 1000, timeMs, outward > 5);
        }

        // Things fly straight out, so where one will cross the circle is known from the moment it is
        // seen. The last stretch is the hardest to see (it meets the bar there), so each is kept in
        // mind until it is due to cross, even after it has been lost from view.
        _pending.RemoveAll(p => timeMs > p.CrossesAtMs + 40 || timeMs - p.LastSeenMs > 700);
        foreach (var p in _pending)
        {
            // Out near the bar's circle, only something seen flying outward counts. Whatever stays the
            // same distance out and only moves round is a bar (another bar, perhaps, whose colour is
            // close to something worth blocking), not a threat.
            if (p.Threat.Distance > r * 0.7 && !p.MovingOut && p.Threat.Distance - p.FirstDistance < 15) continue;
            _threats.Add(p.Threat with { SecondsToEscape = Math.Max(0, (p.CrossesAtMs - timeMs) / 1000) });
        }
    }

    private sealed class Pending
    {
        public BarThreat Threat = null!;
        public double CrossesAtMs, LastSeenMs, FirstDistance;
        public bool MovingOut;     // has been measured heading outward
    }
    private readonly List<Pending> _pending = new();
    private double _typicalSpeed = 300;

    private void Remember(BarThreat t, double crossesAtMs, double nowMs, bool movingOut)
    {
        foreach (var p in _pending)
        {
            // The same one as before: same kind, same direction, further out.
            if (p.Threat.Target != t.Target || Math.Abs(Wrap(p.Threat.Angle - t.Angle)) > 6 || t.Distance < p.Threat.Distance - 10) continue;
            (p.Threat, p.CrossesAtMs, p.LastSeenMs) = (t, crossesAtMs, nowMs);
            p.MovingOut |= movingOut;
            return;
        }
        _pending.Add(new Pending { Threat = t, CrossesAtMs = crossesAtMs, LastSeenMs = nowMs, FirstDistance = t.Distance, MovingOut = movingOut });
    }

    /// <summary>
    /// Which threat to steer to now. Several are usually in flight at once, so this plans: it tries
    /// every order of dealing with them (the bar turning at <paramref name="degreesPerSecond"/>,
    /// needing only its nearer end there, and waiting at each until it arrives), keeps the order that
    /// blocks the most, and returns its first. "Most" weighs kinds by their place in the list, so a
    /// more valuable one counts for more than a common one, but not for throwing away several easy
    /// blocks to fetch it: the plan gets both if there is time. If nothing can be reached in time,
    /// the one that will escape soonest is returned anyway.
    /// </summary>
    /// <param name="barAngle">Where the bar is now, if the caller knows better than the last picture.</param>
    /// <param name="current">
    /// The one being steered for already. Kept when another plan is only as good, so the bar does
    /// not swap back and forth between equally good choices and reach neither.
    /// </param>
    public BarThreat? Choose(double degreesPerSecond, double? barAngle = null, BarThreat? current = null)
    {
        if (_threats.Count == 0) return null;
        double bar = barAngle ?? BarAngle ?? 0;
        double speed = Math.Max(1, degreesPerSecond);
        double reach = Math.Max(0, BarWidthDegrees / 2 - EdgeMarginDegrees); // the bar's nearer end does
        // The soonest few: with more than this in flight, the later ones can wait for the next look.
        var threats = _threats.OrderBy(t => t.SecondsToEscape).Take(MaxPlanned).ToList();
        int kinds = Math.Max(1, _action.Targets.Count);
        double Weight(BarThreat t) => Math.Pow(KindWeight, kinds - 1 - Math.Clamp(t.Target, 0, kinds - 1));

        BarThreat? bestFirst = null;
        double bestValue = -1, bestFinish = double.MaxValue;
        var used = new bool[threats.Count];

        void Search(int depth, double angle, double time, double value, BarThreat? first)
        {
            // Every order is scored as far as it goes: stopping early is one of the plans too.
            // Better = blocks more; then, equally good, starts with what is already being chased;
            // then finishes sooner.
            if (first is not null)
            {
                int keeps = Same(first, current) ? 1 : 0, bestKeeps = Same(bestFirst, current) ? 1 : 0;
                bool better = value > bestValue + 1e-9
                    || (value > bestValue - 1e-9 && (keeps > bestKeeps || (keeps == bestKeeps && time < bestFinish)));
                if (better) (bestValue, bestFinish, bestFirst) = (value, time, first);
            }
            if (depth == threats.Count) return;
            for (int i = 0; i < threats.Count; i++)
            {
                if (used[i]) continue;
                var t = threats[i];
                double offset = ToNearestBar(t.Angle, angle, _action.BarCount);
                double turn = Math.Max(0, Math.Abs(offset) - reach);
                double arrive = time + turn / speed;
                if (arrive > t.SecondsToEscape) continue; // cannot get there in time: not in this plan
                // Got there: the bar now sits with the target just inside its near end, until it arrives.
                double sitAt = angle + Math.Sign(offset) * turn;
                used[i] = true;
                Search(depth + 1, sitAt, Math.Max(arrive, t.SecondsToEscape), value + Weight(t), first ?? t);
                used[i] = false;
            }
        }
        Search(0, bar, 0, 0, null);

        if (bestFirst is not null) return bestFirst;
        return _threats.OrderBy(t => t.SecondsToEscape).First();

        static bool Same(BarThreat? a, BarThreat? b) =>
            a is not null && b is not null && a.Target == b.Target && Math.Abs(Wrap(a.Angle - b.Angle)) <= 6;
    }

    /// <summary>At most this many threats are planned for at once (the soonest to escape).</summary>
    private const int MaxPlanned = 6;
    /// <summary>Each step up the list of kinds counts this many times as much as the one below it.</summary>
    private const double KindWeight = 3;

    /// <summary>
    /// How far to turn (signed, degrees) to bring the nearest of <paramref name="bars"/> evenly
    /// spaced bars, the first at <paramref name="barAngle"/>, onto <paramref name="target"/>.
    /// </summary>
    public static double ToNearestBar(double target, double barAngle, int bars)
    {
        double step = 360.0 / Math.Max(1, bars), best = double.MaxValue;
        for (int k = 0; k < Math.Max(1, bars); k++)
        {
            double d = Wrap(target - (barAngle + k * step));
            if (Math.Abs(d) < Math.Abs(best)) best = d;
        }
        return best;
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
