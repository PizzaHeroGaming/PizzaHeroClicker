using PizzaHeroClicker.Services;
using Xunit;

namespace PizzaHeroClicker.Tests;

public class MarkerSearchTests
{
    private const int W = 30, H = 12, Reach = 20;

    /// <summary>A label-like marker: light strokes on a dark band.</summary>
    private static int[] Label()
    {
        var label = new int[W * H];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
            label[y * W + x] = (x * 7 + y * 3) % 5 < 2 ? 0xF0F0F0 : 0x101418;
        return label;
    }

    /// <summary>The search region round the snipped spot, with the label drawn shifted by (dx, dy).</summary>
    private static (int[] Region, int Width, int Height) Region(int[] label, int dx, int dy, int background = 0x303030)
    {
        int rw = W + 2 * Reach, rh = H + 2 * Reach;
        var region = new int[rw * rh];
        Array.Fill(region, background);
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
            region[(Reach + dy + y) * rw + Reach + dx + x] = label[y * W + x];
        return (region, rw, rh);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-13, 1)]   // the shift seen on the user's screen
    [InlineData(7, -9)]
    [InlineData(20, 20)]   // as far as the search reaches
    public void FindsTheMarkerWhereverItHasMovedTo(int dx, int dy)
    {
        var label = Label();
        var (region, rw, rh) = Region(label, dx, dy);
        var found = ScreenAnchor.FindNear(region, rw, rh, label, W, H, 0.9);
        Assert.NotNull(found);
        Assert.Equal((Reach + dx, Reach + dy), (found.Value.X, found.Value.Y));
        Assert.Equal(1.0, found.Value.Share, 3);
    }

    [Fact]
    public void SomethingMerelySimilarIsNotTakenForTheMarker()
    {
        var label = Label();
        var other = (int[])label.Clone();
        for (int i = 0; i < other.Length; i += 3) other[i] ^= 0xFFFFFF; // a third of it different
        var (region, rw, rh) = Region(other, -5, 4);
        Assert.Null(ScreenAnchor.FindNear(region, rw, rh, label, W, H, 0.9));
    }

    [Fact]
    public void NothingThereFindsNothing()
    {
        int rw = W + 2 * Reach, rh = H + 2 * Reach;
        var empty = new int[rw * rh];
        Array.Fill(empty, 0x808080);
        Assert.Null(ScreenAnchor.FindNear(empty, rw, rh, Label(), W, H, 0.9));
    }
}
