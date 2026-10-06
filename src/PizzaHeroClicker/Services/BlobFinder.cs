namespace PizzaHeroClicker.Services;

/// <summary>What a target looks like, reduced to its mix of colours (a normalised 4 x 4 x 4 RGB histogram).</summary>
public sealed class TargetSignature
{
    public float[] Histogram { get; } = new float[BlobFinder.Bins];
    /// <summary>How many pixels of the picture were the target rather than background.</summary>
    public int Pixels { get; set; }
}

/// <summary>A separate object standing out from the background. X/Y are its centre of mass, in area pixels.</summary>
public sealed class Blob
{
    public int X, Y;
    public int Pixels;
    /// <summary>Index of the first marker colour this object contains enough of, or -1.</summary>
    public int Marker = -1;
    /// <summary>Bounding box, in area pixels.</summary>
    public int Left, Top, Right, Bottom;
    public float[] Histogram = new float[BlobFinder.Bins];
}

/// <summary>
/// Finds the separate objects in a frame and describes each by its colour make-up.
///
/// This is how "appearance" pictures are matched, and it suits sprites drifting over a plain
/// backdrop (space, sky, a table):
///   1. The backdrop is whatever colour is most common in the area.
///   2. Every pixel that differs from it is "something". Somethings that touch, or nearly
///      touch, are grouped into objects (connected components), on a 1-in-3 grid to keep it fast.
///   3. Each object gets a colour signature: what share of it is dark, light, orange, grey...
///
/// A signature does not care which way up the object is, how big it is, or which part of it
/// a picture happened to show. That is what makes it work for tumbling meteorites where an
/// exact pixel-for-pixel comparison fails. The price: two things with the same colour mix
/// and different outlines look the same to it (use "Exact" matching for those).
/// </summary>
public sealed class BlobFinder
{
    public const int Bins = 64;
    public const int Step = 3;
    /// <summary>A pixel within this much of the backdrop colour, on every channel, is backdrop.</summary>
    public const int BackgroundTolerance = 26;
    /// <summary>
    /// Pieces this many grid cells apart (9 px) still belong to one object. Parts of a target
    /// that match the backdrop leave gaps; without this a satellite on black would be three objects.
    /// </summary>
    public const int LinkReach = 3;

    /// <summary>
    /// Optional marker colours. An object containing at least <see cref="MarkerMinPixels"/> of one
    /// is tagged with it (<see cref="Blob.Marker"/>). Games often flag a kind of target this way,
    /// for example a red circle drawn around every satellite, and that is a far more dependable
    /// sign than the target's own colours.
    /// </summary>
    public IReadOnlyList<AreaColorRule>? Markers { get; set; }
    public int MarkerMinPixels { get; set; } = 60;
    private int[] _markerCounts = [];

    /// <summary>From the last <see cref="Scan"/>: how many objects were ignored for their size, and the biggest one's size.</summary>
    public int LastTooSmall { get; private set; }
    public int LastTooLarge { get; private set; }
    public int LastLargestPixels { get; private set; }

    private readonly int[] _colourCounts = new int[32 * 32 * 32];
    private readonly List<Blob> _blobs = new();
    private byte[] _mask = [];
    private int[] _queue = [];

    /// <summary>The most common colour in the frame (as 0xRRGGBB), found on a sparse grid.</summary>
    public int DominantColour(int[] pixels, int width, int height)
    {
        Array.Clear(_colourCounts);
        int best = 0, bestCount = -1;
        for (int y = 0; y < height; y += 6)
        {
            int row = y * width;
            for (int x = 0; x < width; x += 6)
            {
                int p = pixels[row + x];
                int bin = ((p >> 19) & 31) << 10 | ((p >> 11) & 31) << 5 | ((p >> 3) & 31); // 5 bits per channel
                if (++_colourCounts[bin] > bestCount)
                {
                    bestCount = _colourCounts[bin];
                    best = bin;
                }
            }
        }
        // Centre of the winning bin.
        return ((best >> 10) & 31) << 19 | ((best >> 5) & 31) << 11 | (best & 31) << 3 | 0x040404;
    }

    public static bool IsBackground(int pixel, int background) => IsNear(pixel, background, BackgroundTolerance);

    private const int FlatTolerance = 10; // how much a truly flat backdrop may vary

    private static bool IsNear(int pixel, int colour, int tolerance) =>
        Math.Abs(((pixel >> 16) & 0xFF) - ((colour >> 16) & 0xFF)) <= tolerance
        && Math.Abs(((pixel >> 8) & 0xFF) - ((colour >> 8) & 0xFF)) <= tolerance
        && Math.Abs((pixel & 0xFF) - (colour & 0xFF)) <= tolerance;

    /// <summary>
    /// The backdrop a picture was snipped against, judged from a band around its edge: the band's most
    /// common colour, if at least half of the edge is almost exactly that colour. A backdrop is flat,
    /// while the surface of a target varies, so the test is deliberately strict. Null when the
    /// target fills the picture (a close-up), in which case there is no backdrop to remove.
    /// (When a picture is snipped from the game itself this does not matter: the game's own
    /// backdrop colour is always left out as well.)
    /// A picture's backdrop is often NOT the game's: the sample may have been snipped from a
    /// menu or preview with a different background than the play field.
    /// </summary>
    public static int? PictureBackdrop(int[] pixels, int width, int height)
    {
        // Look at a band around the edge rather than only the outermost pixels: screenshots often
        // carry a one-pixel frame or selection outline there.
        int band = Math.Clamp(Math.Min(width, height) / 12, 2, 8);
        var edge = new List<int>(2 * band * (width + height));
        for (int y = 0; y < height; y++)
        {
            bool edgeRow = y < band || y >= height - band;
            for (int x = 0; x < width; x++)
            {
                if (edgeRow || x < band || x >= width - band) edge.Add(pixels[y * width + x]);
            }
        }

        var counts = new Dictionary<int, int>();
        int mode = 0, modeCount = 0;
        foreach (int p in edge)
        {
            int bin = ((p >> 19) & 31) << 10 | ((p >> 11) & 31) << 5 | ((p >> 3) & 31);
            int count = counts.GetValueOrDefault(bin) + 1;
            counts[bin] = count;
            if (count > modeCount) (mode, modeCount) = (bin, count);
        }
        int colour = ((mode >> 10) & 31) << 19 | ((mode >> 5) & 31) << 11 | (mode & 31) << 3 | 0x040404;
        int alike = edge.Count(p => IsNear(p, colour, FlatTolerance));
        if (alike * 2 < edge.Count) return null;

        // If taking that colour away leaves almost nothing, it was the target, not a backdrop.
        int remaining = 0;
        for (int i = 0; i < width * height; i++)
        {
            if (!IsBackground(pixels[i], colour)) remaining++;
        }
        return remaining * 100 >= width * height * 6 ? colour : null;
    }

    /// <summary>
    /// Colour signature of a target picture. Left out: the picture's own backdrop, and anything
    /// the colour of the GAME's backdrop. The second matters because parts of a target that are
    /// the same colour as the play field (black solar panels on black space) cannot be seen in
    /// the game either, so they must not be expected.
    /// </summary>
    public static TargetSignature Describe(int[] pixels, int width, int height, int? pictureBackdrop, int? gameBackdrop)
    {
        var signature = new TargetSignature();
        for (int i = 0; i < width * height; i++)
        {
            if (pictureBackdrop is int own && IsBackground(pixels[i], own)) continue;
            if (gameBackdrop is int game && IsBackground(pixels[i], game)) continue;
            AddToHistogram(signature.Histogram, pixels[i]);
            signature.Pixels++;
        }
        Normalise(signature.Histogram, signature.Pixels);
        return signature;
    }

    /// <summary>
    /// Marks every grid cell that differs from a plain backdrop colour. Returns the share of the
    /// area that IS backdrop (0..1): a low share means the backdrop is not plain at all.
    /// Follow with <see cref="Label"/>.
    /// </summary>
    public double MarkAgainstColour(int[] pixels, int width, int height, int background)
    {
        if (!PrepareGrid(width, height, out int gw, out int gh)) return 1;
        int plain = 0;
        for (int gy = 0; gy < gh; gy++)
        {
            int row = gy * Step * width, m = gy * gw;
            for (int gx = 0; gx < gw; gx++)
            {
                bool isBackdrop = IsBackground(pixels[row + gx * Step], background);
                _mask[m + gx] = isBackdrop ? (byte)0 : (byte)1;
                if (isBackdrop) plain++;
            }
        }
        return (double)plain / (gw * gh);
    }

    /// <summary>
    /// Marks every grid cell that differs from a learned scene. Returns the share of the area
    /// that differs (0..1). Follow with <see cref="Label"/>.
    /// </summary>
    public double MarkAgainstScene(int[] pixels, int width, int height, SceneModel scene)
    {
        if (!PrepareGrid(width, height, out int gw, out int gh)) return 0;
        scene.Mark(pixels, width, gw, gh, _mask);
        int changed = 0, cells = gw * gh;
        for (int i = 0; i < cells; i++) changed += _mask[i];
        return (double)changed / cells;
    }

    /// <summary>Forgets what was marked, so <see cref="Label"/> finds nothing.</summary>
    public void ClearMarks() => Array.Clear(_mask);

    private bool PrepareGrid(int width, int height, out int gw, out int gh)
    {
        gw = width / Step;
        gh = height / Step;
        int cells = gw * gh;
        if (gw < 1 || gh < 1) return false;
        if (_mask.Length < cells)
        {
            _mask = new byte[cells];
            _queue = new int[cells];
        }
        return true;
    }

    /// <summary>Plain-backdrop shortcut: mark, then label.</summary>
    public IReadOnlyList<Blob> Scan(int[] pixels, int width, int height, int background, int minPixels, int maxPixels)
    {
        MarkAgainstColour(pixels, width, height, background);
        return Label(pixels, width, height, minPixels, maxPixels);
    }

    /// <summary>
    /// Groups the marked cells into objects and returns those whose size is between the limits
    /// (in real pixels). The returned list is reused by the next call.
    /// </summary>
    public IReadOnlyList<Blob> Label(int[] pixels, int width, int height, int minPixels, int maxPixels)
    {
        _blobs.Clear();
        LastTooSmall = LastTooLarge = LastLargestPixels = 0;
        int gw = width / Step, gh = height / Step, cells = gw * gh;
        if (gw < 1 || gh < 1 || _mask.Length < cells) return _blobs;

        // In _mask: 1 = something, 0 = backdrop, 2 = already assigned to an object.
        int cellArea = Step * Step;
        for (int start = 0; start < cells; start++)
        {
            if (_mask[start] != 1) continue;

            // Breadth-first flood fill; _queue doubles as the list of this object's cells.
            int head = 0, tail = 0;
            _queue[tail++] = start;
            _mask[start] = 2;
            long sumX = 0, sumY = 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = 0, maxY = 0;
            while (head < tail)
            {
                int cell = _queue[head++];
                int cx = cell % gw, cy = cell / gw;
                sumX += cx;
                sumY += cy;
                if (cx < minX) minX = cx;
                if (cx > maxX) maxX = cx;
                if (cy < minY) minY = cy;
                if (cy > maxY) maxY = cy;
                for (int dy = -LinkReach; dy <= LinkReach; dy++)
                {
                    int ny = cy + dy;
                    if (ny < 0 || ny >= gh) continue;
                    for (int dx = -LinkReach; dx <= LinkReach; dx++)
                    {
                        int nx = cx + dx;
                        if (nx < 0 || nx >= gw) continue;
                        int next = ny * gw + nx;
                        if (_mask[next] != 1) continue;
                        _mask[next] = 2;
                        _queue[tail++] = next;
                    }
                }
            }

            int size = tail * cellArea;
            LastLargestPixels = Math.Max(LastLargestPixels, size);
            if (size < minPixels) { LastTooSmall++; continue; }  // a speck
            if (size > maxPixels) { LastTooLarge++; continue; }  // something huge, like a planet

            var blob = new Blob
            {
                X = (int)(sumX * Step / tail) + Step / 2,
                Y = (int)(sumY * Step / tail) + Step / 2,
                Pixels = size,
                Left = minX * Step, Top = minY * Step, Right = maxX * Step + Step - 1, Bottom = maxY * Step + Step - 1,
            };
            int markerCount = Markers?.Count ?? 0;
            if (_markerCounts.Length < markerCount) _markerCounts = new int[markerCount];
            Array.Clear(_markerCounts);
            for (int i = 0; i < tail; i++)
            {
                int cell = _queue[i];
                int pixel = pixels[cell / gw * Step * width + cell % gw * Step];
                AddToHistogram(blob.Histogram, pixel);
                for (int k = 0; k < markerCount; k++)
                {
                    var rule = Markers![k];
                    if (Math.Abs(((pixel >> 16) & 0xFF) - rule.Color.R) <= rule.Tolerance
                        && Math.Abs(((pixel >> 8) & 0xFF) - rule.Color.G) <= rule.Tolerance
                        && Math.Abs((pixel & 0xFF) - rule.Color.B) <= rule.Tolerance)
                    {
                        _markerCounts[k]++;
                        break; // the first rule that matches a pixel claims it
                    }
                }
            }
            for (int k = 0; k < markerCount; k++)
            {
                if (_markerCounts[k] * cellArea >= MarkerMinPixels) { blob.Marker = k; break; }
            }
            Normalise(blob.Histogram, tail);
            _blobs.Add(blob);
        }
        return _blobs;
    }

    /// <summary>How alike two signatures are: 1 = the same mix of colours, 0 = nothing in common.</summary>
    public static double Similarity(float[] a, float[] b)
    {
        double shared = 0;
        for (int i = 0; i < Bins; i++) shared += Math.Min(a[i], b[i]);
        return shared;
    }

    /// <summary>
    /// Adds one pixel to a histogram with 4 levels per channel. The pixel is shared between the
    /// neighbouring levels in proportion to how close it is to each, so a colour sitting on a
    /// boundary does not flip between bins from frame to frame.
    /// </summary>
    private static void AddToHistogram(float[] histogram, int pixel)
    {
        float r = ((pixel >> 16) & 0xFF) * (3f / 255), g = ((pixel >> 8) & 0xFF) * (3f / 255), b = (pixel & 0xFF) * (3f / 255);
        int r0 = Math.Min(2, (int)r), g0 = Math.Min(2, (int)g), b0 = Math.Min(2, (int)b);
        float rf = r - r0, gf = g - g0, bf = b - b0;
        for (int dr = 0; dr <= 1; dr++)
        {
            float wr = dr == 0 ? 1 - rf : rf;
            for (int dg = 0; dg <= 1; dg++)
            {
                float wg = dg == 0 ? 1 - gf : gf;
                int index = (r0 + dr) * 16 + (g0 + dg) * 4 + b0;
                histogram[index] += wr * wg * (1 - bf);
                histogram[index + 1] += wr * wg * bf;
            }
        }
    }

    private static void Normalise(float[] histogram, int count)
    {
        if (count <= 0) return;
        float scale = 1f / count;
        for (int i = 0; i < histogram.Length; i++) histogram[i] *= scale;
    }
}

/// <summary>
/// Learns what a busy but unchanging scene looks like (a painted backdrop, a planet, a HUD), so
/// that anything moving across it stands out.
///
/// Every so often the area is sampled onto the same 1-in-3 grid the blob finder uses, and the
/// last few samples are kept. The scene at each cell is the MEDIAN of its samples: a meteorite
/// passing over a cell is only there for a sample or two, so the median still shows what is
/// underneath. Something that stays put for several seconds becomes part of the scene, which is
/// exactly right for craters and wreckage, and means a target must keep moving to be seen.
/// </summary>
public sealed class SceneModel
{
    public const int Samples = 9;
    public const long SampleEveryMs = 700;
    /// <summary>A cell differing from the scene by more than this, on any channel, is "something".</summary>
    public const int Tolerance = 30;
    private const int MinSamples = 3;

    private readonly int[][] _ring = new int[Samples][];
    private int[] _scene = [];
    private int _count, _next, _gw, _gh;
    private long _lastSampleMs = long.MinValue / 2;

    /// <summary>False for the first couple of seconds, while there are too few samples to trust.</summary>
    public bool Ready => _count >= MinSamples;

    /// <summary>Forgets everything learned, to start again on a new screen.</summary>
    public void Reset()
    {
        _count = 0;
        _next = 0;
        _lastSampleMs = long.MinValue / 2;
    }

    /// <summary>Offers a frame. It is sampled only if enough time has passed since the last sample.</summary>
    public void Update(int[] pixels, int width, int height, long nowMs)
    {
        int gw = width / BlobFinder.Step, gh = height / BlobFinder.Step;
        if (gw < 1 || gh < 1) return;
        if (gw != _gw || gh != _gh)
        {
            (_gw, _gh, _count, _next) = (gw, gh, 0, 0);
            _scene = new int[gw * gh];
            for (int i = 0; i < Samples; i++) _ring[i] = new int[gw * gh];
            _lastSampleMs = long.MinValue / 2;
        }
        if (nowMs - _lastSampleMs < SampleEveryMs) return;
        _lastSampleMs = nowMs;

        var sample = _ring[_next];
        _next = (_next + 1) % Samples;
        if (_count < Samples) _count++;
        for (int gy = 0; gy < gh; gy++)
        {
            int row = gy * BlobFinder.Step * width, m = gy * gw;
            for (int gx = 0; gx < gw; gx++) sample[m + gx] = pixels[row + gx * BlobFinder.Step] & 0xFFFFFF;
        }
        Rebuild();
    }

    /// <summary>Per-cell, per-channel median of the samples held.</summary>
    private void Rebuild()
    {
        int n = _count, cells = _gw * _gh;
        Span<int> values = stackalloc int[Samples];
        Span<byte> channel = stackalloc byte[Samples];
        for (int c = 0; c < cells; c++)
        {
            int first = _ring[0][c];
            bool same = true;
            for (int i = 0; i < n; i++)
            {
                values[i] = _ring[i][c];
                same &= values[i] == first;
            }
            if (same)
            {
                _scene[c] = first; // the usual case: nothing has passed over this cell
                continue;
            }

            int median = 0;
            for (int shift = 16; shift >= 0; shift -= 8)
            {
                for (int i = 0; i < n; i++) channel[i] = (byte)(values[i] >> shift);
                var slice = channel[..n];
                slice.Sort();
                median |= slice[n / 2] << shift;
            }
            _scene[c] = median;
        }
    }

    /// <summary>The flat "green screen" colour painted over everything that is not the target in a keyed picture.</summary>
    public const int KeyColour = 0x00FF00;

    /// <summary>
    /// Turns a snip taken on a busy scene into a picture of just the moving target: every pixel
    /// that belongs to the unchanging scene is painted <see cref="KeyColour"/>, which the matcher
    /// then recognises as a plain surround and leaves out. Returns how many pixels were kept.
    /// </summary>
    /// <param name="snip">The snipped pixels, modified in place.</param>
    /// <param name="areaPixels">The watched area in the SAME frozen frame the snip was cut from.</param>
    public int KeyOutScene(int[] snip, int snipX, int snipY, int snipWidth, int snipHeight,
        int[] areaPixels, int areaX, int areaY, int areaWidth, int areaHeight)
    {
        int gw = areaWidth / BlobFinder.Step, gh = areaHeight / BlobFinder.Step;
        if (gw < 1 || gh < 1) return snipWidth * snipHeight;
        var moving = new byte[gw * gh];
        Mark(areaPixels, areaWidth, gw, gh, moving);

        // Grow the moving region by one cell so the target's outline is not shaved off.
        var grown = new byte[gw * gh];
        for (int gy = 0; gy < gh; gy++)
        for (int gx = 0; gx < gw; gx++)
        {
            if (moving[gy * gw + gx] == 0) continue;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = gx + dx, ny = gy + dy;
                if (nx >= 0 && ny >= 0 && nx < gw && ny < gh) grown[ny * gw + nx] = 1;
            }
        }

        int kept = 0;
        for (int y = 0; y < snipHeight; y++)
        for (int x = 0; x < snipWidth; x++)
        {
            int cx = (snipX + x - areaX) / BlobFinder.Step, cy = (snipY + y - areaY) / BlobFinder.Step;
            bool inArea = snipX + x >= areaX && snipY + y >= areaY && cx < gw && cy < gh;
            if (inArea && grown[cy * gw + cx] != 0) kept++;
            else snip[y * snipWidth + x] = KeyColour;
        }
        return kept;
    }

    /// <summary>Writes 1 into <paramref name="mask"/> for each grid cell that differs from the scene, else 0.</summary>
    internal void Mark(int[] pixels, int width, int gw, int gh, byte[] mask)
    {
        bool usable = Ready && gw == _gw && gh == _gh;
        for (int gy = 0; gy < gh; gy++)
        {
            int row = gy * BlobFinder.Step * width, m = gy * gw;
            for (int gx = 0; gx < gw; gx++)
            {
                if (!usable) { mask[m + gx] = 0; continue; }
                int p = pixels[row + gx * BlobFinder.Step], s = _scene[m + gx];
                bool differs = Math.Abs(((p >> 16) & 0xFF) - ((s >> 16) & 0xFF)) > Tolerance
                            || Math.Abs(((p >> 8) & 0xFF) - ((s >> 8) & 0xFF)) > Tolerance
                            || Math.Abs((p & 0xFF) - (s & 0xFF)) > Tolerance;
                mask[m + gx] = differs ? (byte)1 : (byte)0;
            }
        }
    }
}
