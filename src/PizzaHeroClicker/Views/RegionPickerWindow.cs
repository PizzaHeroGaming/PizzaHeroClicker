using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PizzaHeroClicker.Services;
using static PizzaHeroClicker.Native.NativeMethods;

namespace PizzaHeroClicker.Views;

/// <summary>A rectangle snipped from a frozen screenshot: where it was, and its pixels (0x00RRGGBB, row-major).</summary>
public sealed record Snip(RegionPick Region, int[] Pixels);

/// <summary>
/// Lets the user drag out a rectangle anywhere on the desktop. Two modes:
///
///   Live   - a dimmed, see-through window over every monitor; returns the rectangle.
///   Frozen - the desktop is photographed first and shown as a still image to drag on, so a
///            moving target holds still; returns the rectangle and the pixels inside it.
///
/// Results always come from GetCursorPos (physical pixels), so they are exact on any monitor
/// regardless of how the preview is scaled.
/// </summary>
public sealed class RegionPickerWindow : Window
{
    private const int MinSize = 4;

    private readonly TaskCompletionSource<RegionPick?> _completion = new();
    private readonly Canvas _canvas = new();
    private readonly Rectangle _box;
    private readonly TextBlock _hint;
    private readonly Image? _still;
    private readonly Rectangle? _dim;
    private readonly (int X, int Y, int Width, int Height) _bounds;
    private IntPtr _hwnd;
    private POINT _start;
    private bool _dragging;
    private RegionPick? _result;

    private RegionPickerWindow(string hint, BitmapSource? still)
    {
        _bounds = ScreenService.VirtualScreen;
        WindowStyle = WindowStyle.None;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.Cross;
        Title = "Select an area";

        var dimBrush = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0));
        if (still is null)
        {
            AllowsTransparency = true;
            Background = dimBrush;
        }
        else
        {
            // An opaque window showing the screenshot, with the dimming drawn on top of it.
            Background = Brushes.Black;
            _still = new Image { Source = still, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(_still, BitmapScalingMode.NearestNeighbor);
            _dim = new Rectangle { Fill = dimBrush };
            _canvas.Children.Add(_still);
            _canvas.Children.Add(_dim);
        }

        _box = new Rectangle
        {
            Stroke = (Brush)FindResource("CheeseBrush"),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xC5, 0x33)),
            Visibility = Visibility.Collapsed,
        };
        _hint = new TextBlock
        {
            Text = hint,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x17, 0x11, 0x0E)),
            Padding = new Thickness(12, 7, 12, 7),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
        };
        _canvas.Children.Add(_box);
        _canvas.Children.Add(_hint);
        Content = _canvas;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyBounds();
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(ApplyBounds);
        Loaded += (_, _) =>
        {
            ApplyBounds();
            Activate();
            Focus();
            // Put the hint on the monitor the mouse is on.
            GetCursorPos(out var p);
            var monitor = ScreenService.MonitorAt(p.X, p.Y);
            var at = ToCanvas(monitor.Left + 40, monitor.Top + 40);
            Canvas.SetLeft(_hint, at.X);
            Canvas.SetTop(_hint, at.Y);
        };
        Closed += (_, _) => _completion.TrySetResult(_result);
    }

    /// <summary>Live mode. The task completes with the rectangle, or null if cancelled.</summary>
    public static Task<RegionPick?> PickAsync()
    {
        var window = new RegionPickerWindow("Drag a box around the area to watch.   Esc or right-click cancels.", null);
        window.Show();
        return window._completion.Task;
    }

    /// <summary>
    /// Frozen mode: photographs the whole desktop now, then lets the user drag a box on the still
    /// image. Completes with the box and its pixels, or null if cancelled or the capture failed.
    /// </summary>
    /// <param name="onFrozen">Called with the frozen frame (pixels, left, top, width, height of the whole desktop) before the user picks.</param>
    public static async Task<Snip?> SnipAsync(ScreenService screen, string hint, Action<int[], int, int, int, int>? onFrozen = null)
    {
        var (vx, vy, vw, vh) = ScreenService.VirtualScreen;
        int[] frame = [];
        if (vw <= 0 || vh <= 0 || !screen.TryCapture(vx, vy, vw, vh, ref frame)) return null;
        onFrozen?.Invoke(frame, vx, vy, vw, vh);

        var still = BitmapSource.Create(vw, vh, 96, 96, PixelFormats.Bgr32, null, frame, vw * 4);
        still.Freeze();
        var window = new RegionPickerWindow(hint, still);
        window.Show();
        var region = await window._completion.Task;
        if (region is null) return null;

        // Crop from the photograph, clipped to it.
        int x0 = Math.Clamp(region.X - vx, 0, vw - 1), y0 = Math.Clamp(region.Y - vy, 0, vh - 1);
        int w = Math.Min(region.Width, vw - x0), h = Math.Min(region.Height, vh - y0);
        var pixels = new int[w * h];
        for (int y = 0; y < h; y++) Array.Copy(frame, (y0 + y) * vw + x0, pixels, y * w, w);
        return new Snip(new RegionPick(x0 + vx, y0 + vy, w, h), pixels);
    }

    private void ApplyBounds()
    {
        if (_hwnd == IntPtr.Zero) return;
        SetWindowPos(_hwnd, HWND_TOPMOST, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, 0);

        // The still image must cover the window exactly, whatever this window's DPI scale is.
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        foreach (FrameworkElement? layer in new FrameworkElement?[] { _still, _dim })
        {
            if (layer is null) continue;
            layer.Width = _bounds.Width / scale;
            layer.Height = _bounds.Height / scale;
        }
    }

    /// <summary>Physical screen pixel to this window's drawing units.</summary>
    private Point ToCanvas(int screenX, int screenY)
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return new Point((screenX - _bounds.X) / scale, (screenY - _bounds.Y) / scale);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        GetCursorPos(out _start);
        _dragging = true;
        _hint.Visibility = Visibility.Collapsed;
        CaptureMouse();
        UpdateBox();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) UpdateBox();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        GetCursorPos(out var end);
        int x = Math.Min(_start.X, end.X), y = Math.Min(_start.Y, end.Y);
        int w = Math.Abs(end.X - _start.X) + 1, h = Math.Abs(end.Y - _start.Y) + 1;
        if (w < MinSize || h < MinSize)
        {
            // A stray click rather than a drag: let the user try again.
            _box.Visibility = Visibility.Collapsed;
            _hint.Visibility = Visibility.Visible;
            return;
        }
        _result = new RegionPick(x, y, w, h);
        Close();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void UpdateBox()
    {
        GetCursorPos(out var now);
        var a = ToCanvas(Math.Min(_start.X, now.X), Math.Min(_start.Y, now.Y));
        var b = ToCanvas(Math.Max(_start.X, now.X), Math.Max(_start.Y, now.Y));
        Canvas.SetLeft(_box, a.X);
        Canvas.SetTop(_box, a.Y);
        _box.Width = Math.Max(1, b.X - a.X);
        _box.Height = Math.Max(1, b.Y - a.Y);
        _box.Visibility = Visibility.Visible;
    }
}
