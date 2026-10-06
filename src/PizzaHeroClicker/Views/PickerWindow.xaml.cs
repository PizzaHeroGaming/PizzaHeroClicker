using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PizzaHeroClicker.Models;
using PizzaHeroClicker.Services;
using static PizzaHeroClicker.Native.NativeMethods;

namespace PizzaHeroClicker.Views;

/// <summary>
/// Point / colour picker (also the eyedropper). A small click-through magnifier follows the
/// cursor; the next left click anywhere on any monitor is intercepted by a low-level mouse
/// hook and returned as the result, without reaching the window underneath.
///
/// The magnifier is always drawn at least 24 px away from the cursor and only an 11 x 11
/// region around the cursor is ever captured, so the picker never samples itself.
/// </summary>
public partial class PickerWindow : Window
{
    private const int Radius = 5;      // capture (2 * Radius + 1) pixels square
    private const int OffsetPx = 24;   // distance between cursor and magnifier
    private const int VK_ESCAPE = 0x1B;

    private readonly ScreenService _screen;
    private readonly TaskCompletionSource<PickResult?> _completion = new();
    private readonly DispatcherTimer _timer;
    // Delegates passed to SetWindowsHookEx must be kept alive by a field or the GC collects them.
    private readonly HookProc _mouseProc;
    private readonly HookProc _keyboardProc;
    private IntPtr _mouseHook, _keyboardHook, _hwnd;
    private PickResult? _result;
    private bool _finishing;

    public static PickerWindow? Current { get; private set; }

    private PickerWindow(ScreenService screen, string? captureKey)
    {
        InitializeComponent();
        _screen = screen;
        _mouseProc = MouseHook;
        _keyboardProc = KeyboardHook;
        if (!string.IsNullOrEmpty(captureKey)) HintText.Text = $"Click or press {captureKey} to pick · Esc cancels";

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) => Follow();
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    /// <summary>Starts a pick. The task completes with the picked point, or null if cancelled.</summary>
    public static Task<PickResult?> PickAsync(ScreenService screen, string? captureKey = null)
    {
        if (Current is not null) return Task.FromResult<PickResult?>(null); // one pick at a time
        var window = new PickerWindow(screen, captureKey);
        Current = window;
        window.Show();
        return window._completion.Task;
    }

    /// <summary>Completes the pick at the current cursor position (used by the capture hotkey).</summary>
    public void AcceptAtCursor()
    {
        if (_finishing) return;
        GetCursorPos(out var p);
        Accept(p.X, p.Y);
        Close();
    }

    private void Accept(int x, int y)
    {
        _result = new PickResult(x, y, _screen.GetPixel(x, y) ?? default);
        _finishing = true;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        WindowChrome.AddExStyle(this, WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        IntPtr module = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
        if (_mouseHook == IntPtr.Zero)
        {
            Log.Error($"Could not install the picker's mouse hook (error {Marshal.GetLastWin32Error()}).");
            Dispatcher.BeginInvoke(Close);
            return;
        }

        _timer.Start();
        Follow();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        _mouseHook = _keyboardHook = IntPtr.Zero;
        if (Current == this) Current = null;
        _completion.TrySetResult(_result);
    }

    /// <summary>Refreshes the magnifier and keeps the window next to the cursor.</summary>
    private void Follow()
    {
        if (_finishing) return;
        GetCursorPos(out var p);

        int size = Radius * 2 + 1;
        Zoom.Source = _screen.CaptureRegion(p.X - Radius, p.Y - Radius, size, size);
        var color = _screen.GetPixel(p.X, p.Y) ?? default;
        Swatch.Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        Info.Text = $"{color.ToHex()}  ({p.X}, {p.Y})";

        // Position in physical pixels; flip to the other side of the cursor at monitor edges.
        if (!GetWindowRect(_hwnd, out var rect)) return;
        var monitor = ScreenService.MonitorAt(p.X, p.Y);
        int x = p.X + OffsetPx, y = p.Y + OffsetPx;
        if (x + rect.Width > monitor.Right) x = p.X - OffsetPx - rect.Width;
        if (y + rect.Height > monitor.Bottom) y = p.Y - OffsetPx - rect.Height;
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // Hook callbacks run on the UI thread (the thread that installed them) and must return
    // quickly. Returning 1 swallows the event so it never reaches other applications.

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            switch (wParam.ToInt32())
            {
                case WM_LBUTTONDOWN:
                    if (!_finishing)
                    {
                        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        Accept(data.pt.X, data.pt.Y);
                    }
                    return 1;
                case WM_RBUTTONDOWN:
                    _finishing = true; // cancel
                    return 1;
                case WM_LBUTTONUP or WM_RBUTTONUP when _finishing:
                    Dispatcher.BeginInvoke(Close); // swallow the matching release too, then finish
                    return 1;
            }
        }
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam.ToInt32() is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (data.vkCode == VK_ESCAPE)
            {
                _finishing = true;
                Dispatcher.BeginInvoke(Close);
                return 1;
            }
        }
        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }
}
