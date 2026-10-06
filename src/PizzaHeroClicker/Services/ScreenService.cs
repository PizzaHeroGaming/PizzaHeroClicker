using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using PizzaHeroClicker.Models;
using static PizzaHeroClicker.Native.NativeMethods;

namespace PizzaHeroClicker.Services;

/// <summary>Monitor geometry and cheap screen sampling. Safe to call from any thread.</summary>
public sealed class ScreenService : IScreenService, IDisposable
{
    private const int CornerMarginPx = 2;
    private const long MonitorCacheMs = 2000;

    private readonly object _lock = new();
    private RECT[] _monitors = [];
    private long _monitorsReadAt = long.MinValue / 2; // halved so "now - readAt" cannot overflow

    /// <summary>Bounding box of all monitors. Left/Top are negative when a monitor sits left of / above the primary.</summary>
    public static (int X, int Y, int Width, int Height) VirtualScreen => (
        GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>Monitor rectangles in physical pixels, cached briefly so the engine can ask often.</summary>
    internal RECT[] GetMonitors()
    {
        lock (_lock)
        {
            long now = Environment.TickCount64;
            if (now - _monitorsReadAt < MonitorCacheMs && _monitors.Length > 0) return _monitors;

            var list = new List<RECT>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr _, IntPtr _, ref RECT rect, IntPtr _) =>
            {
                list.Add(rect);
                return true;
            }, IntPtr.Zero);
            _monitors = list.ToArray();
            _monitorsReadAt = now;
            return _monitors;
        }
    }

    /// <summary>Bounds of the monitor nearest to a point.</summary>
    internal static RECT MonitorAt(int x, int y)
    {
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST), ref info);
        return info.rcMonitor;
    }

    public bool IsAtStuckCorner(int x, int y) => IsStuckCorner(x, y, GetMonitors(), CornerMarginPx);

    /// <summary>
    /// A "stuck" corner is a monitor corner with no other monitor beside it in either
    /// direction, i.e. a place the cursor physically stops. Corners where two monitors meet
    /// are excluded, otherwise sliding along the top edge from one screen to the next would
    /// trigger an emergency stop.
    /// </summary>
    internal static bool IsStuckCorner(int x, int y, IReadOnlyList<RECT> monitors, int margin)
    {
        foreach (var m in monitors)
        {
            if (!m.Contains(x, y)) continue;

            bool left = x <= m.Left + margin, right = x >= m.Right - 1 - margin;
            bool top = y <= m.Top + margin, bottom = y >= m.Bottom - 1 - margin;
            if (!(left || right) || !(top || bottom)) return false;

            int outsideX = left ? m.Left - 1 : m.Right;
            int outsideY = top ? m.Top - 1 : m.Bottom;
            foreach (var other in monitors)
            {
                if (other.Contains(outsideX, y) || other.Contains(x, outsideY)) return false;
            }
            return true;
        }
        return false;
    }

    public PixelColor? GetPixel(int x, int y)
    {
        // One GetPixel on the screen DC: no bitmap, no full-screen capture.
        IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;
        try
        {
            uint c = NativeGetPixel(dc, x, y);
            if (c == CLR_INVALID) return null;
            return new PixelColor((byte)c, (byte)(c >> 8), (byte)(c >> 16)); // COLORREF is 0x00BBGGRR
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    // Captures reuse their GDI objects between calls: an area watch grabs the same rectangle many
    // times a second, and recreating a memory DC and bitmap each time is wasteful. Two sizes are
    // remembered, because a watch with a start/stop marker alternates between its area and the marker.
    private sealed class CaptureSlot
    {
        public IntPtr Dc, Bitmap, Previous, Bits;
        public int Width, Height;
        public long LastUsed;

        public void Release()
        {
            if (Dc != IntPtr.Zero)
            {
                if (Previous != IntPtr.Zero) SelectObject(Dc, Previous);
                DeleteDC(Dc);
            }
            if (Bitmap != IntPtr.Zero) DeleteObject(Bitmap);
            Dc = Bitmap = Previous = Bits = IntPtr.Zero;
            Width = Height = 0;
        }
    }

    private readonly object _captureLock = new();
    private readonly CaptureSlot[] _slots = [new CaptureSlot(), new CaptureSlot()];
    private long _captureClock;

    public bool TryCapture(int x, int y, int width, int height, ref int[] pixels)
    {
        if (width <= 0 || height <= 0) return false;
        lock (_captureLock)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return false;
            try
            {
                // Reuse the slot of this size, or replace the one used longest ago.
                var slot = _slots.FirstOrDefault(s => s.Bitmap != IntPtr.Zero && s.Width == width && s.Height == height);
                if (slot is null)
                {
                    slot = _slots.OrderBy(s => s.LastUsed).First();
                    slot.Release();
                    var header = new BITMAPINFOHEADER
                    {
                        biSize = System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = width,
                        biHeight = -height, // top-down, so row 0 is the top of the area
                        biPlanes = 1,
                        biBitCount = 32,
                    };
                    slot.Dc = CreateCompatibleDC(screen);
                    slot.Bitmap = CreateDIBSection(screen, ref header, 0, out slot.Bits, IntPtr.Zero, 0);
                    if (slot.Dc == IntPtr.Zero || slot.Bitmap == IntPtr.Zero)
                    {
                        slot.Release();
                        return false;
                    }
                    slot.Previous = SelectObject(slot.Dc, slot.Bitmap);
                    slot.Width = width;
                    slot.Height = height;
                }
                slot.LastUsed = ++_captureClock;

                // Only this rectangle is copied, never the whole screen. BitBlt does not include the mouse cursor.
                if (!BitBlt(slot.Dc, 0, 0, width, height, screen, x, y, SRCCOPY)) return false;
                if (pixels.Length != width * height) pixels = new int[width * height];
                System.Runtime.InteropServices.Marshal.Copy(slot.Bits, pixels, 0, pixels.Length);
                return true;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }

    public void Dispose()
    {
        lock (_captureLock)
        {
            foreach (var slot in _slots) slot.Release();
        }
    }

    private static uint NativeGetPixel(IntPtr dc, int x, int y) => PizzaHeroClicker.Native.NativeMethods.GetPixel(dc, x, y);

    /// <summary>Copies a small screen rectangle (for the eyedropper's magnifier). UI thread only.</summary>
    public BitmapSource? CaptureRegion(int x, int y, int width, int height)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bitmap = CreateCompatibleBitmap(screen, width, height);
        IntPtr previous = SelectObject(mem, bitmap);
        try
        {
            if (!BitBlt(mem, 0, 0, width, height, screen, x, y, SRCCOPY)) return null;
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            SelectObject(mem, previous);
            DeleteObject(bitmap);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
