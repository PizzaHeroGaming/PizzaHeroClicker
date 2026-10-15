namespace PizzaHeroClicker.Services;

/// <summary>
/// Follows the things an area watch sees from one look to the next, to tell how fast each is moving.
///
/// A speed worked out from just two looks is mostly noise: looks are only a few hundredths of a
/// second apart, and where an object's centre is measured wobbles by a few pixels from one look to
/// the next (it is found on a coarse grid, against a moving backdrop). A few pixels over 25 ms reads
/// as hundreds of pixels a second. So a speed is only given for an object followed for a while,
/// fitted through all of its recent positions, and only if they lie close to a straight line.
/// </summary>
public sealed class TargetTracker
{
    private sealed class Track
    {
        public readonly List<(double T, double X, double Y)> Points = new();
        public double VX, VY;       // pixels per second; 0 until there is enough to go on
        public bool Trusted;
        public double FirstT;       // when it was first seen (its points are trimmed, this is not)
        public double FirstX, FirstY;
        public double FarthestPx;   // furthest it has been from where it was first seen
        public bool ReportedStill;
        public double LastT => Points[^1].T;
        public (double X, double Y) PredictAt(double t)
        {
            var (lt, lx, ly) = Points[^1];
            return Trusted ? (lx + VX * (t - lt) / 1000, ly + VY * (t - lt) / 1000) : (lx, ly);
        }
    }

    /// <summary>Positions older than this are forgotten: a target may turn or slow down.</summary>
    public const double HistoryMs = 300;
    /// <summary>A speed needs positions spread over at least this long...</summary>
    public const double MinSpanMs = 70;
    /// <summary>...and at least this many of them.</summary>
    public const int MinPoints = 4;
    /// <summary>Positions further than this from the fitted line mean it is not one object moving steadily.</summary>
    public const double MaxScatterPx = 5;
    /// <summary>Faster than this is not believed.</summary>
    public const double MaxSpeed = 2000;
    /// <summary>A track not seen for this long is dropped.</summary>
    public const double LostAfterMs = 150;

    private readonly List<Track> _tracks = new();

    /// <summary>
    /// Feeds one look: everything seen that could be a target, in area pixels, at <paramref name="timeMs"/>.
    /// </summary>
    public void Update(IReadOnlyList<(int X, int Y)> seen, double timeMs)
    {
        _tracks.RemoveAll(t => timeMs - t.LastT > LostAfterMs || timeMs < t.LastT);

        // Pair each sighting with the track it fits best (nearest to where that track should be
        // by now), closest pairs first, each track and each sighting used once.
        var pairs = new List<(double Distance, int Track, int Seen)>();
        for (int ti = 0; ti < _tracks.Count; ti++)
        {
            var track = _tracks[ti];
            var (px, py) = track.PredictAt(timeMs);
            double gapS = (timeMs - track.LastT) / 1000;
            // How far off the prediction may be: a little for measuring wobble, more the longer since
            // it was last seen, and more again while its speed is not yet known.
            double gate = 12 + (track.Trusted ? 200 : MaxSpeed) * gapS;
            for (int si = 0; si < seen.Count; si++)
            {
                double d = Math.Sqrt((seen[si].X - px) * (seen[si].X - px) + (seen[si].Y - py) * (seen[si].Y - py));
                if (d <= gate) pairs.Add((d, ti, si));
            }
        }
        pairs.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var trackUsed = new bool[_tracks.Count];
        var seenUsed = new bool[seen.Count];
        foreach (var (_, ti, si) in pairs)
        {
            if (trackUsed[ti] || seenUsed[si]) continue;
            (trackUsed[ti], seenUsed[si]) = (true, true);
            Add(_tracks[ti], seen[si], timeMs);
        }
        for (int si = 0; si < seen.Count; si++)
        {
            if (seenUsed[si]) continue;
            var track = new Track { FirstT = timeMs, FirstX = seen[si].X, FirstY = seen[si].Y };
            track.Points.Add((timeMs, seen[si].X, seen[si].Y));
            _tracks.Add(track);
        }
    }

    /// <summary>
    /// How fast the object seen at (x, y) in the latest look is moving, in pixels per second.
    /// (0, 0) unless it has been followed long enough, and steadily enough, to be sure.
    /// </summary>
    public (double X, double Y) VelocityOf(int x, int y)
    {
        foreach (var track in _tracks)
        {
            var (_, lx, ly) = track.Points[^1];
            if ((int)lx == x && (int)ly == y) return track.Trusted ? (track.VX, track.VY) : (0, 0);
        }
        return (0, 0);
    }

    public void Clear() => _tracks.Clear();

    /// <summary>
    /// Things that have been seen in the same spot for at least <paramref name="forMs"/>, moving no
    /// more than <paramref name="withinPx"/>, each reported once. Against a moving game scene these
    /// are part of the game's display (a score that keeps changing, an animated icon), not targets.
    /// </summary>
    public List<(int X, int Y)> TakeStandingStill(double forMs, double withinPx)
    {
        var still = new List<(int X, int Y)>();
        foreach (var track in _tracks)
        {
            if (track.ReportedStill || track.LastT - track.FirstT < forMs || track.FarthestPx > withinPx) continue;
            track.ReportedStill = true;
            still.Add(((int)track.FirstX, (int)track.FirstY));
        }
        return still;
    }

    private static void Add(Track track, (int X, int Y) p, double timeMs)
    {
        track.FarthestPx = Math.Max(track.FarthestPx, Math.Sqrt((p.X - track.FirstX) * (p.X - track.FirstX) + (p.Y - track.FirstY) * (p.Y - track.FirstY)));
        track.Points.Add((timeMs, p.X, p.Y));
        track.Points.RemoveAll(q => timeMs - q.T > HistoryMs);
        Fit(track);
    }

    /// <summary>Least-squares straight line through the track's positions over time.</summary>
    private static void Fit(Track track)
    {
        var pts = track.Points;
        track.Trusted = false;
        (track.VX, track.VY) = (0, 0);
        if (pts.Count < MinPoints || pts[^1].T - pts[0].T < MinSpanMs) return;

        double mt = pts.Average(p => p.T), mx = pts.Average(p => p.X), my = pts.Average(p => p.Y);
        double stt = 0, stx = 0, sty = 0;
        foreach (var (t, x, y) in pts)
        {
            stt += (t - mt) * (t - mt);
            stx += (t - mt) * (x - mx);
            sty += (t - mt) * (y - my);
        }
        if (stt <= 0) return;
        double vx = stx / stt, vy = sty / stt; // pixels per millisecond

        double worst = 0;
        foreach (var (t, x, y) in pts)
        {
            double ex = x - (mx + vx * (t - mt)), ey = y - (my + vy * (t - mt));
            worst = Math.Max(worst, Math.Sqrt(ex * ex + ey * ey));
        }
        double speed = Math.Sqrt(vx * vx + vy * vy) * 1000;
        if (worst > MaxScatterPx || speed > MaxSpeed) return;

        (track.VX, track.VY, track.Trusted) = (vx * 1000, vy * 1000, true);
    }
}
