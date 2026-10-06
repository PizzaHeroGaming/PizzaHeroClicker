using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PizzaHeroClicker.Services;
using static PizzaHeroClicker.Native.NativeMethods;

namespace PizzaHeroClicker.Views;

/// <summary>
/// The small always-on-top ON / OFF pill. It never takes focus from the game. When locked it
/// is click-through (the mouse passes straight to whatever is underneath); when unlocked it
/// can be dragged to a new spot.
/// </summary>
public partial class StatusOverlayWindow : Window
{
    private IntPtr _hwnd;
    private bool _clickThrough = true;
    private (int X, int Y)? _position;

    /// <summary>Raised with the new position (physical pixels) after the user drags the overlay.</summary>
    public event Action<int, int>? Moved;

    public StatusOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            WindowChrome.AddExStyle(this, WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            ApplyClickThrough();
            ApplyPosition();
        };
    }

    public void SetState(string kind, string text)
    {
        if (Label.Text != text) Label.Text = text;
        string key = kind switch
        {
            "Running" => "BasilBrush",
            "Countdown" or "Paused" => "CheeseBrush",
            "Waiting" => "CrustBrush",
            _ => "TomatoBrush",
        };
        var brush = (Brush)FindResource(key);
        if (!ReferenceEquals(Dot.Fill, brush))
        {
            Dot.Fill = brush;
            Pill.BorderBrush = brush;
        }
    }

    public void SetClickThrough(bool value)
    {
        if (_clickThrough == value) return;
        _clickThrough = value;
        ApplyClickThrough();
    }

    /// <summary>Places the overlay at a physical-pixel position, or at the default spot when null or off-screen.</summary>
    public void SetPosition(int? x, int? y)
    {
        var requested = x is int px && y is int py ? (px, py) : ((int, int)?)null;
        if (requested == _position && _hwnd != IntPtr.Zero) return;
        _position = requested;
        ApplyPosition();
    }

    private void ApplyClickThrough()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_clickThrough) WindowChrome.AddExStyle(this, WS_EX_TRANSPARENT);
        else WindowChrome.RemoveExStyle(this, WS_EX_TRANSPARENT);
    }

    private void ApplyPosition()
    {
        if (_hwnd == IntPtr.Zero) return;
        var primary = ScreenService.MonitorAt(0, 0);
        (int x, int y) = _position ?? (primary.Left + 16, primary.Top + 16);

        // A saved position can be on a monitor that is no longer connected: fall back to the default.
        var monitor = ScreenService.MonitorAt(x, y);
        if (!monitor.Contains(x, y)) (x, y) = (primary.Left + 16, primary.Top + 16);

        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_clickThrough) return;
        DragMove(); // returns when the button is released
        if (GetWindowRect(_hwnd, out var rect))
        {
            _position = (rect.Left, rect.Top);
            Moved?.Invoke(rect.Left, rect.Top);
        }
    }
}
