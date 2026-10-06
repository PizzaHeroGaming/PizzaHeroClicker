using PizzaHeroClicker.Models;

namespace PizzaHeroClicker.Services;

/// <summary>A colour to look for, already parsed.</summary>
public readonly record struct AreaColorRule(PixelColor Color, int Tolerance);

/// <summary>Something found in the area: which rule matched and where (pixels from the area's top-left).</summary>
public readonly record struct AreaTarget(int RuleIndex, int X, int Y, int PixelCount);

/// <summary>
/// Finds coloured objects in a captured block of screen pixels. Pure computation: no I/O.
///
/// 1. Coarse pass: test every <c>step</c>-th pixel against the rules (the first rule that
///    matches a pixel claims it). This is cheap even for a large area.
/// 2. Order the hits by urgency: nearest the chosen edge first.
/// 3. For each hit in that order, look at the full-resolution neighbourhood around it. If it
///    holds enough pixels of the same rule it is a real object, and its centre of mass is the
///    click point; otherwise it was a stray speck (a star, a spark) and the next hit is tried.
///
/// Working outward from one hit, rather than averaging every match in the area, is what
/// keeps two same-coloured objects from producing a click in the empty space between them.
/// </summary>
public sealed class AreaScanner
{
    /// <summary>Half-size of the neighbourhood examined around a hit.</summary>
    public const int BlobRadius = 20;
    private const int MaxCandidates = 200;

    private readonly List<(int Key, int X, int Y, int Rule)> _hits = new(); // reused between scans

    /// <param name="pixels">Row-major pixels, 0x00RRGGBB each, <paramref name="width"/> * <paramref name="height"/> long.</param>
    /// <param name="avoid">Spots to pass over (clicked a moment ago); the next most urgent target is returned instead.</param>
    public AreaTarget? Find(int[] pixels, int width, int height, IReadOnlyList<AreaColorRule> rules,
        int step, int minPixels, ScanPriority priority, IReadOnlyList<(int X, int Y)>? avoid = null, int avoidRadius = 0)
    {
        if (width <= 0 || height <= 0 || rules.Count == 0 || pixels.Length < width * height) return null;
        step = Math.Max(1, step);
        minPixels = Math.Max(1, minPixels);

        _hits.Clear();
        for (int y = 0; y < height; y += step)
        {
            int row = y * width;
            for (int x = 0; x < width; x += step)
            {
                int rule = Match(pixels[row + x], rules);
                if (rule < 0) continue;
                _hits.Add((PriorityKey(priority, x, y, width, height), x, y, rule));
            }
        }
        if (_hits.Count == 0) return null;
        _hits.Sort(static (a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));

        int tried = 0;
        foreach (var hit in _hits)
        {
            int x0 = Math.Max(0, hit.X - BlobRadius), x1 = Math.Min(width - 1, hit.X + BlobRadius);
            int y0 = Math.Max(0, hit.Y - BlobRadius), y1 = Math.Min(height - 1, hit.Y + BlobRadius);
            long sumX = 0, sumY = 0;
            int count = 0;
            for (int y = y0; y <= y1; y++)
            {
                int row = y * width;
                for (int x = x0; x <= x1; x++)
                {
                    if (Matches(pixels[row + x], rules[hit.Rule]))
                    {
                        count++;
                        sumX += x;
                        sumY += y;
                    }
                }
            }
            if (count >= minPixels)
            {
                int cx = (int)(sumX / count), cy = (int)(sumY / count);
                if (!IsAvoided(cx, cy, avoid, avoidRadius) && !IsAvoided(hit.X, hit.Y, avoid, avoidRadius))
                    return new AreaTarget(hit.Rule, cx, cy, count);
            }
            if (++tried >= MaxCandidates) break; // an area full of specks (or all recently clicked): give up on this frame
        }
        return null;
    }

    private static bool IsAvoided(int x, int y, IReadOnlyList<(int X, int Y)>? avoid, int radius)
    {
        if (avoid is null) return false;
        foreach (var (ax, ay) in avoid)
        {
            if (Math.Abs(x - ax) <= radius && Math.Abs(y - ay) <= radius) return true;
        }
        return false;
    }

    /// <summary>
    /// Smaller = more urgent: distance from the edge the user chose, or (squared) distance from
    /// the middle of a <paramref name="width"/> x <paramref name="height"/> area.
    /// </summary>
    public static int PriorityKey(ScanPriority priority, int x, int y, int width, int height)
    {
        switch (priority)
        {
            case ScanPriority.Bottom: return -y;
            case ScanPriority.Left: return x;
            case ScanPriority.Right: return -x;
            case ScanPriority.Center:
                // Doubled coordinates keep the exact centre of an even-sized area without fractions.
                int dx = 2 * x - (width - 1), dy = 2 * y - (height - 1);
                return dx * dx + dy * dy;
            default: return y;
        }
    }

    private static int Match(int pixel, IReadOnlyList<AreaColorRule> rules)
    {
        for (int i = 0; i < rules.Count; i++)
        {
            if (Matches(pixel, rules[i])) return i;
        }
        return -1;
    }

    private static bool Matches(int pixel, AreaColorRule rule)
    {
        int r = (pixel >> 16) & 0xFF, g = (pixel >> 8) & 0xFF, b = pixel & 0xFF;
        return Math.Abs(r - rule.Color.R) <= rule.Tolerance
            && Math.Abs(g - rule.Color.G) <= rule.Tolerance
            && Math.Abs(b - rule.Color.B) <= rule.Tolerance;
    }
}
