using System.Diagnostics;
using System.Text;
using PizzaHeroClicker.Models;
using static PizzaHeroClicker.Native.NativeMethods;

namespace PizzaHeroClicker.Services;

public sealed record WindowInfo(IntPtr Handle, string Title, string ProcessName)
{
    public string Display => $"{Title}  [{ProcessName}]";
}

/// <summary>Finds and inspects other applications' top-level windows. Safe to call from any thread.</summary>
public sealed class WindowService : IWindowService
{
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    /// <summary>Visible, titled, top-level windows of other processes.</summary>
    public IReadOnlyList<WindowInfo> ListWindows()
    {
        var result = new List<WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            int length = GetWindowTextLength(hwnd);
            if (length == 0) return true;

            // Skip "cloaked" windows: suspended UWP apps and windows on other virtual desktops.
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == OwnProcessId) return true;

            var sb = new StringBuilder(length + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            result.Add(new WindowInfo(hwnd, sb.ToString(), ProcessNameOf(pid)));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>
    /// Locates the profile's target window. Titles often change (document names, FPS counters),
    /// so matching is progressively looser: exact title, then title prefix/contains within the
    /// same process, then any window of the process.
    /// </summary>
    public IntPtr Find(WindowTarget target)
    {
        if (!target.HasWindow) return IntPtr.Zero;
        var windows = ListWindows();
        bool hasProcess = !string.IsNullOrWhiteSpace(target.ProcessName);
        bool hasTitle = !string.IsNullOrWhiteSpace(target.Title);

        bool SameProcess(WindowInfo w) => !hasProcess || string.Equals(w.ProcessName, target.ProcessName, StringComparison.OrdinalIgnoreCase);

        WindowInfo? match = null;
        if (hasTitle)
        {
            match = windows.FirstOrDefault(w => SameProcess(w) && string.Equals(w.Title, target.Title, StringComparison.Ordinal))
                ?? windows.FirstOrDefault(w => SameProcess(w) && w.Title.Contains(target.Title, StringComparison.OrdinalIgnoreCase));
        }
        if (match is null && hasProcess)
        {
            match = windows.FirstOrDefault(SameProcess);
        }
        return match?.Handle ?? IntPtr.Zero;
    }

    public bool IsAlive(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    public bool IsForeground(IntPtr hwnd) => hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;

    public (int X, int Y) GetClientOrigin(IntPtr hwnd)
    {
        var p = new POINT();
        ClientToScreen(hwnd, ref p);
        return (p.X, p.Y);
    }

    public bool IsOwnWindowAt(int x, int y)
    {
        // WindowFromPoint respects z-order and skips click-through windows, so our overlays never count.
        IntPtr hwnd = WindowFromPoint(new POINT { X = x, Y = y });
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == OwnProcessId;
    }

    private static string ProcessNameOf(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return ""; // process exited, or access denied
        }
    }
}
