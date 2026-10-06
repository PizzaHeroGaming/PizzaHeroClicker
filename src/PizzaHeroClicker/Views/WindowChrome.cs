using System.Windows;
using System.Windows.Interop;
using static PizzaHeroClicker.Native.NativeMethods;

namespace PizzaHeroClicker.Views;

/// <summary>Native window tweaks shared by all windows.</summary>
internal static class WindowChrome
{
    /// <summary>Gives the standard title bar a dark, theme-matched look (Windows 10 2004+ / Windows 11).</summary>
    public static void ApplyDarkTitleBar(Window window)
    {
        void Apply()
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
            int caption = 0x000E1117; // COLORREF 0x00BBGGRR for #17110E; ignored before Windows 11
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply();
        else window.SourceInitialized += (_, _) => Apply();
    }

    /// <summary>Adds extended window styles (click-through, no-activate, hidden from Alt+Tab).</summary>
    public static void AddExStyle(Window window, long styles)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        long current = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(current | styles));
    }

    public static void RemoveExStyle(Window window, long styles)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        long current = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(current & ~styles));
    }
}
