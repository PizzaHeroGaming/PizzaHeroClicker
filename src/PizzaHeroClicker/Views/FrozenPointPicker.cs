using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PizzaHeroClicker.Models;
using PizzaHeroClicker.Services;
using static PizzaHeroClicker.Native.NativeMethods;

namespace PizzaHeroClicker.Views;

/// <summary>
/// Shows a photograph of the whole desktop and lets the user click a few points on it, one after
/// another, with a prompt for each. For setting things up from a moment of a game that does not
/// last long enough to point at it live. After the first point a circle centred on it follows the
/// mouse, which helps when the second point is on something that travels round the first.
/// </summary>
public sealed class FrozenPointPicker : Window
{
    private readonly TaskCompletionSource<List<PickResult>?> _completion = new();
    private readonly List<PickResult> _picks = new();
    private readonly string[] _prompts;
    private readonly int[] _frame;
    private readonly (int X, int Y, int Width, int Height) _bounds;
    private readonly Canvas _canvas = new();
    private readonly Image _still;
    private readonly TextBlock _hint;
    private readonly Ellipse _guide;
    private readonly List<UIElement> _marks = new();
    private IntPtr _hwnd;
    private bool _done;

    private FrozenPointPicker(int[] frame, (int, int, int, int) bounds, BitmapSource still, string[] prompts)
    {
        _frame = frame;
        _bounds = bounds;
        _prompts = prompts;
        WindowStyle = WindowStyle.None;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        Title = "Set up from a snapshot";

        _still = new Image { Source = still, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_still, BitmapScalingMode.NearestNeighbor);
        _canvas.Children.Add(_still);
        _guide = new Ellipse { Stroke = (Brush)FindResource("CheeseBrush"), StrokeThickness = 2, StrokeDashArray = [4, 3], Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        _canvas.Children.Add(_guide);
        _hint = new TextBlock
        {
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x17, 0x11, 0x0E)),
            Padding = new Thickness(12, 7, 12, 7),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            IsHitTestVisible = false,
        };
        _canvas.Children.Add(_hint);
        Content = _canvas;
        ShowPrompt();

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
            GetCursorPos(out var p);
            var monitor = ScreenService.MonitorAt(p.X, p.Y);
            var at = ToCanvas(monitor.Left + 40, monitor.Top + 40);
            Canvas.SetLeft(_hint, at.X);
            Canvas.SetTop(_hint, at.Y);
        };
        Closed += (_, _) => _completion.TrySetResult(_done ? _picks : null);
    }

    /// <summary>
    /// Shows the given photograph of the desktop and asks for one click per prompt. Completes with
    /// the points (and the colour under each, from the photograph), or null if cancelled.
    /// </summary>
    public static Task<List<PickResult>?> PickAsync(int[] frame, int left, int top, int width, int height, params string[] prompts)
    {
        var still = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, frame, width * 4);
        still.Freeze();
        var window = new FrozenPointPicker(frame, (left, top, width, height), still, prompts);
        window.Show();
        return window._completion.Task;
    }

    private void ShowPrompt() =>
        _hint.Text = $"Step {_picks.Count + 1} of {_prompts.Length}: {_prompts[_picks.Count]}   (this is a still picture; take your time.  Backspace undoes, Esc cancels)";

    private void ApplyBounds()
    {
        if (_hwnd == IntPtr.Zero) return;
        SetWindowPos(_hwnd, HWND_TOPMOST, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, 0);
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _still.Width = _bounds.Width / scale;
        _still.Height = _bounds.Height / scale;
    }

    private Point ToCanvas(int screenX, int screenY)
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return new Point((screenX - _bounds.X) / scale, (screenY - _bounds.Y) / scale);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_picks.Count == 0) return;
        // A circle round the first point through the mouse: where something travelling round it would be.
        GetCursorPos(out var p);
        var centre = _picks[0];
        double r = Math.Sqrt(Math.Pow(p.X - centre.X, 2) + Math.Pow(p.Y - centre.Y, 2));
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var c = ToCanvas(centre.X, centre.Y);
        _guide.Width = _guide.Height = 2 * r / scale;
        Canvas.SetLeft(_guide, c.X - r / scale);
        Canvas.SetTop(_guide, c.Y - r / scale);
        _guide.Visibility = Visibility.Visible;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        GetCursorPos(out var p);
        int fx = Math.Clamp(p.X - _bounds.X, 0, _bounds.Width - 1), fy = Math.Clamp(p.Y - _bounds.Y, 0, _bounds.Height - 1);
        int rgb = _frame[fy * _bounds.Width + fx];
        _picks.Add(new PickResult(p.X, p.Y, new PixelColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)));

        var at = ToCanvas(p.X, p.Y);
        var mark = new Ellipse { Width = 14, Height = 14, Stroke = (Brush)FindResource("TomatoBrush"), StrokeThickness = 3, IsHitTestVisible = false };
        Canvas.SetLeft(mark, at.X - 7);
        Canvas.SetTop(mark, at.Y - 7);
        _canvas.Children.Add(mark);
        _marks.Add(mark);

        if (_picks.Count == _prompts.Length)
        {
            _done = true;
            Close();
            return;
        }
        ShowPrompt();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        if (e.Key == Key.Back && _picks.Count > 0)
        {
            _picks.RemoveAt(_picks.Count - 1);
            _canvas.Children.Remove(_marks[^1]);
            _marks.RemoveAt(_marks.Count - 1);
            if (_picks.Count == 0) _guide.Visibility = Visibility.Collapsed;
            ShowPrompt();
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e) => Close();
}
