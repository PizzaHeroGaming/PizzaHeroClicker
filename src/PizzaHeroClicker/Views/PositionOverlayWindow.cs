using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using PizzaHeroClicker.Services;
using static PizzaHeroClicker.Native.NativeMethods;

namespace PizzaHeroClicker.Views;

/// <summary>
/// A transparent, click-through, full-screen window covering ONE monitor, drawing a numbered
/// marker at each saved position on that monitor. One window per monitor (rather than one
/// window stretched across the whole desktop) lets each use its own monitor's DPI scale, so
/// markers land on the exact pixel at any mix of scaling factors.
/// </summary>
public sealed class PositionOverlayWindow : Window
{
    private readonly Canvas _canvas = new();
    private readonly RECT _monitor;
    private IReadOnlyList<OverlayMarker> _markers = [];
    private IntPtr _hwnd;

    internal PositionOverlayWindow(RECT monitor)
    {
        _monitor = monitor;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "Pizza Hero Clicker positions";
        Content = _canvas;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            WindowChrome.AddExStyle(this, WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            ApplyBounds();
        };
        // Moving onto a monitor with a different scale makes WPF resize the window; put it back
        // and redraw at the new scale.
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            ApplyBounds();
            Redraw();
        });
        Loaded += (_, _) =>
        {
            ApplyBounds();
            Redraw();
        };
    }

    internal RECT Monitor => _monitor;

    public void SetMarkers(IReadOnlyList<OverlayMarker> markers)
    {
        _markers = markers;
        if (IsLoaded) Redraw();
    }

    private void ApplyBounds()
    {
        if (_hwnd == IntPtr.Zero) return;
        SetWindowPos(_hwnd, HWND_TOPMOST, _monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height, SWP_NOACTIVATE);
    }

    private void Redraw()
    {
        _canvas.Children.Clear();
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX; // physical pixels per WPF unit on this monitor
        var tomato = (Brush)FindResource("TomatoBrush");
        var cheese = (Brush)FindResource("CheeseBrush");
        var dark = (Brush)FindResource("BgBrush");

        foreach (var marker in _markers)
        {
            // Physical screen pixel -> this window's device-independent units. The +0.5 aims at the pixel's centre.
            double x = (marker.X - _monitor.Left + 0.5) / scale;
            double y = (marker.Y - _monitor.Top + 0.5) / scale;

            // Crosshair marks the exact pixel; the numbered badge sits beside it so it doesn't hide the target.
            _canvas.Children.Add(new Line { X1 = x - 9, X2 = x + 9, Y1 = y, Y2 = y, Stroke = dark, StrokeThickness = 4 });
            _canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = y - 9, Y2 = y + 9, Stroke = dark, StrokeThickness = 4 });
            _canvas.Children.Add(new Line { X1 = x - 8, X2 = x + 8, Y1 = y, Y2 = y, Stroke = cheese, StrokeThickness = 2 });
            _canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = y - 8, Y2 = y + 8, Stroke = cheese, StrokeThickness = 2 });

            var badge = new Border
            {
                Background = tomato,
                BorderBrush = dark,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(6, 0, 6, 1),
                Child = new TextBlock
                {
                    Text = marker.Label,
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 13,
                    FontFamily = (FontFamily)FindResource("HeadFont"),
                },
            };
            Canvas.SetLeft(badge, x + 8);
            Canvas.SetTop(badge, y + 8);
            _canvas.Children.Add(badge);
        }
    }
}
