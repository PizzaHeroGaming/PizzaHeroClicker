using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PizzaHeroClicker.Models;

namespace PizzaHeroClicker.Services;

/// <summary>Converts target pictures between pixel arrays (0x00RRGGBB) and the base64 PNG text stored in profiles.</summary>
public static class TemplateImage
{
    public const int MinSide = 6;
    public const int MaxSide = 256;

    public static string Encode(int[] pixels, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }

    /// <summary>Writes pixels (0x00RRGGBB, row-major) to a PNG file. Safe to call from any thread.</summary>
    public static void SavePng(string path, int[] pixels, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public static bool TryDecode(string? base64, out int[] pixels, out int width, out int height)
    {
        pixels = [];
        width = height = 0;
        if (string.IsNullOrWhiteSpace(base64)) return false;
        try
        {
            if (ToBitmap(base64) is not { } bitmap) return false;
            width = bitmap.PixelWidth;
            height = bitmap.PixelHeight;
            if (width < 1 || height < 1 || width > 4096 || height > 4096) return false;
            pixels = new int[width * height];
            bitmap.CopyPixels(pixels, width * 4, 0);
            for (int i = 0; i < pixels.Length; i++) pixels[i] &= 0x00FFFFFF;
            return true;
        }
        catch
        {
            return false; // not base64, or not an image
        }
    }

    /// <summary>Decodes to a frozen bitmap (usable from any thread), or null if the text is not an image.</summary>
    public static BitmapSource? ToBitmap(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(base64));
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
            converted.Freeze();
            return converted;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>A place in the area that looks like a picture. X/Y are the centre, in pixels from the area's top-left.</summary>
public readonly record struct TemplateMatch(int X, int Y, double Score);

/// <summary>
/// A target picture prepared for matching: colour planes with their average removed, at full
/// size and at a reduced size for the fast first pass.
/// </summary>
public sealed class PreparedTemplate
{
    public int Width { get; private init; }
    public int Height { get; private init; }
    /// <summary>How much the first pass shrinks things (1 = not at all).</summary>
    public int Factor { get; private init; }
    public int CoarseWidth { get; private init; }
    public int CoarseHeight { get; private init; }

    // Three planes (R, G, B), each zero-mean, stored one after another.
    internal float[] Full = [];
    internal float[] Coarse = [];
    internal double FullEnergy;    // sum of squares of Full
    internal double CoarseEnergy;

    /// <summary>Returns null if the picture is too small or has no detail to match on (a flat colour).</summary>
    public static PreparedTemplate? Create(int[] pixels, int width, int height)
    {
        if (width < TemplateImage.MinSide || height < TemplateImage.MinSide || pixels.Length < width * height) return null;

        // Shrink so the smaller side is about 8 cells: enough to keep the shape, cheap to slide around.
        int factor = Math.Clamp(Math.Min(width, height) / 8, 1, 8);
        int cw = width / factor, ch = height / factor;

        var full = Planes(pixels, width, height, 1, width, height);
        var coarse = Planes(pixels, width, height, factor, cw, ch);
        double fullEnergy = RemoveMeans(full, width * height);
        double coarseEnergy = RemoveMeans(coarse, cw * ch);

        // Average deviation under ~2 levels per channel means there is nothing to recognise.
        if (fullEnergy < 4.0 * 3 * width * height || coarseEnergy <= 1e-3) return null;

        return new PreparedTemplate
        {
            Width = width, Height = height, Factor = factor, CoarseWidth = cw, CoarseHeight = ch,
            Full = full, Coarse = coarse, FullEnergy = fullEnergy, CoarseEnergy = coarseEnergy,
        };
    }

    /// <summary>Box-averages an image by <paramref name="factor"/> into three colour planes.</summary>
    internal static float[] Planes(int[] pixels, int width, int height, int factor, int outWidth, int outHeight)
    {
        int n = outWidth * outHeight;
        var planes = new float[n * 3];
        float scale = 1f / (factor * factor);
        for (int y = 0; y < outHeight; y++)
        {
            for (int x = 0; x < outWidth; x++)
            {
                int r = 0, g = 0, b = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    int row = (y * factor + dy) * width + x * factor;
                    for (int dx = 0; dx < factor; dx++)
                    {
                        int p = pixels[row + dx];
                        r += (p >> 16) & 0xFF;
                        g += (p >> 8) & 0xFF;
                        b += p & 0xFF;
                    }
                }
                int i = y * outWidth + x;
                planes[i] = r * scale;
                planes[n + i] = g * scale;
                planes[2 * n + i] = b * scale;
            }
        }
        return planes;
    }

    private static double RemoveMeans(float[] planes, int n)
    {
        double energy = 0;
        for (int c = 0; c < 3; c++)
        {
            double sum = 0;
            for (int i = 0; i < n; i++) sum += planes[c * n + i];
            float mean = (float)(sum / n);
            for (int i = 0; i < n; i++)
            {
                float v = planes[c * n + i] - mean;
                planes[c * n + i] = v;
                energy += v * v;
            }
        }
        return energy;
    }
}

/// <summary>
/// Finds where a target picture appears in a captured frame.
///
/// The measure is zero-mean normalised cross-correlation over the three colour channels: it
/// asks "does the pattern of lighter and darker, redder and bluer here follow the picture's
/// pattern?" and gives 1.0 for a perfect copy. Because it compares patterns rather than
/// colours, two targets with the same colours but different outlines score differently, and
/// overall brightness changes do not matter.
///
/// Speed comes from working coarse-to-fine:
///   1. Shrink the frame and the picture by the same factor and score every position. Flat
///      regions (empty space) are rejected from their variance alone, without correlating.
///   2. Keep the local peaks, order them by urgency, and re-score each at full resolution in
///      a small neighbourhood. The first one that clears the threshold is the answer.
///
/// Limits: the target must look the same size and roughly the same way up as in the picture.
/// One instance per thread; call <see cref="SetFrame"/> once per captured frame.
/// </summary>
public sealed class TemplateMatcher
{
    private const int MaxRefined = 12;
    private const double CoarseSlack = 0.25;      // the shrunken pass is blurrier, so it is given a lower bar
    private const double MinEnergyRatio = 0.08;   // a candidate needs at least this share of the picture's contrast

    private sealed class CoarseFrame
    {
        public int Width, Height;
        public float[] Planes = [];
        // Summed-area tables, (Width + 1) x (Height + 1): window sums in O(1).
        public double[] SumR = [], SumG = [], SumB = [], SumSquares = [];
    }

    private readonly Dictionary<int, CoarseFrame> _coarse = new();
    private readonly List<(double Score, int X, int Y)> _candidates = new();
    private readonly List<(int Key, int X, int Y)> _peaks = new();
    private int[] _frame = [];
    private int _width, _height;

    public void SetFrame(int[] pixels, int width, int height)
    {
        _frame = pixels;
        _width = width;
        _height = height;
        _coarse.Clear(); // shrunken copies are built on demand, once per factor
    }

    /// <summary>The most urgent place that matches the picture with at least <paramref name="threshold"/> (0..1), if any.</summary>
    public TemplateMatch? FindFirst(PreparedTemplate t, double threshold, ScanPriority priority)
    {
        if (t.Width > _width || t.Height > _height) return null;
        var frame = GetCoarse(t.Factor);
        int cw = t.CoarseWidth, ch = t.CoarseHeight, n = cw * ch, fw = frame.Width, fn = frame.Width * frame.Height;
        if (cw > frame.Width || ch > frame.Height) return null;

        double coarseThreshold = Math.Max(0.3, threshold - CoarseSlack);
        double minEnergy = t.CoarseEnergy * MinEnergyRatio;
        int stride = fw + 1;

        _candidates.Clear();
        for (int y = 0; y + ch <= frame.Height; y++)
        {
            for (int x = 0; x + cw <= frame.Width; x++)
            {
                int a = y * stride + x, b = a + cw, c = a + ch * stride, d = c + cw;
                double sr = frame.SumR[d] - frame.SumR[b] - frame.SumR[c] + frame.SumR[a];
                double sg = frame.SumG[d] - frame.SumG[b] - frame.SumG[c] + frame.SumG[a];
                double sb = frame.SumB[d] - frame.SumB[b] - frame.SumB[c] + frame.SumB[a];
                double sq = frame.SumSquares[d] - frame.SumSquares[b] - frame.SumSquares[c] + frame.SumSquares[a];
                double energy = sq - (sr * sr + sg * sg + sb * sb) / n;
                if (energy < minEnergy) continue; // flat or nearly flat: cannot be the target

                double cross = 0;
                for (int plane = 0; plane < 3; plane++)
                {
                    int tBase = plane * n, fBase = plane * fn + y * fw + x;
                    for (int ty = 0; ty < ch; ty++)
                    {
                        int ti = tBase + ty * cw, fi = fBase + ty * fw;
                        for (int tx = 0; tx < cw; tx++) cross += t.Coarse[ti + tx] * frame.Planes[fi + tx];
                    }
                }
                double score = cross / Math.Sqrt(t.CoarseEnergy * energy);
                if (score >= coarseThreshold) _candidates.Add((score, x, y));
            }
        }
        if (_candidates.Count == 0) return null;

        // Keep one candidate per object: the strongest, suppressing weaker ones that overlap it.
        _candidates.Sort(static (p, q) => q.Score.CompareTo(p.Score));
        _peaks.Clear();
        foreach (var (_, x, y) in _candidates)
        {
            bool overlaps = false;
            foreach (var peak in _peaks)
            {
                if (Math.Abs(peak.X - x) * 2 < cw && Math.Abs(peak.Y - y) * 2 < ch) { overlaps = true; break; }
            }
            if (overlaps) continue;
            int key = AreaScanner.PriorityKey(priority, x * t.Factor + t.Width / 2, y * t.Factor + t.Height / 2, _width, _height);
            _peaks.Add((key, x, y));
            if (_peaks.Count >= 64) break;
        }
        _peaks.Sort(static (p, q) => p.Key.CompareTo(q.Key));

        int refined = 0;
        foreach (var (_, x, y) in _peaks)
        {
            var (score, fx, fy) = Refine(t, x * t.Factor, y * t.Factor);
            if (score >= threshold) return new TemplateMatch(fx + t.Width / 2, fy + t.Height / 2, score);
            if (++refined >= MaxRefined) break;
        }
        return null;
    }

    /// <summary>Full-resolution search around a coarse hit. Returns the best score and its top-left corner.</summary>
    private (double Score, int X, int Y) Refine(PreparedTemplate t, int x0, int y0)
    {
        int reach = t.Factor, step = Math.Max(1, t.Factor / 2);
        double best = -1;
        int bestX = x0, bestY = y0;

        void Try(int x, int y)
        {
            if (x < 0 || y < 0 || x + t.Width > _width || y + t.Height > _height) return;
            double score = FullScore(t, x, y);
            if (score > best) (best, bestX, bestY) = (score, x, y);
        }

        for (int dy = -reach; dy <= reach; dy += step)
        for (int dx = -reach; dx <= reach; dx += step)
            Try(x0 + dx, y0 + dy);

        if (step > 1) // polish to the exact pixel
        {
            int cx = bestX, cy = bestY;
            for (int dy = 1 - step; dy < step; dy++)
            for (int dx = 1 - step; dx < step; dx++)
                if (dx != 0 || dy != 0) Try(cx + dx, cy + dy);
        }
        return (best, bestX, bestY);
    }

    private double FullScore(PreparedTemplate t, int x, int y)
    {
        int w = t.Width, h = t.Height, n = w * h;
        double sr = 0, sg = 0, sb = 0, sq = 0, cross = 0;
        for (int ty = 0; ty < h; ty++)
        {
            int fi = (y + ty) * _width + x, ti = ty * w;
            for (int tx = 0; tx < w; tx++)
            {
                int p = _frame[fi + tx];
                int r = (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, b = p & 0xFF;
                sr += r; sg += g; sb += b;
                sq += r * r + g * g + b * b;
                cross += t.Full[ti + tx] * r + t.Full[n + ti + tx] * g + t.Full[2 * n + ti + tx] * b;
            }
        }
        double energy = sq - (sr * sr + sg * sg + sb * sb) / n;
        if (energy < t.FullEnergy * MinEnergyRatio) return 0;
        return cross / Math.Sqrt(t.FullEnergy * energy);
    }

    private CoarseFrame GetCoarse(int factor)
    {
        if (_coarse.TryGetValue(factor, out var cached)) return cached;

        int w = _width / factor, h = _height / factor, n = w * h;
        var frame = new CoarseFrame { Width = w, Height = h, Planes = PreparedTemplate.Planes(_frame, _width, _height, factor, w, h) };
        int stride = w + 1;
        frame.SumR = new double[stride * (h + 1)];
        frame.SumG = new double[stride * (h + 1)];
        frame.SumB = new double[stride * (h + 1)];
        frame.SumSquares = new double[stride * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            double rowR = 0, rowG = 0, rowB = 0, rowSq = 0;
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                float r = frame.Planes[i], g = frame.Planes[n + i], b = frame.Planes[2 * n + i];
                rowR += r; rowG += g; rowB += b;
                rowSq += r * r + g * g + b * b;
                int below = (y + 1) * stride + x + 1, above = y * stride + x + 1;
                frame.SumR[below] = frame.SumR[above] + rowR;
                frame.SumG[below] = frame.SumG[above] + rowG;
                frame.SumB[below] = frame.SumB[above] + rowB;
                frame.SumSquares[below] = frame.SumSquares[above] + rowSq;
            }
        }
        _coarse[factor] = frame;
        return frame;
    }
}
